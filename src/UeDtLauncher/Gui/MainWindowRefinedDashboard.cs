using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private readonly LauncherStartupOptions _startupOptions;
    private readonly LauncherDashboardViewModel _viewModel = new();
    private LauncherConfig _config { get => _viewModel.Config; set => _viewModel.Config = value; }
    private ProjectUiConfig _selectedProject { get => _viewModel.SelectedProject; set => _viewModel.SelectedProject = value; }
    private CatalogSnapshot _catalog { get => _viewModel.Catalog; set => _viewModel.Catalog = value; }
    private string _search { get => _viewModel.Search; set => _viewModel.Search = value; }
    private string _catalogState { get => _viewModel.CatalogState; set => _viewModel.CatalogState = value; }
    private string _installState { get => _viewModel.InstallState; set => _viewModel.InstallState = value; }
    private string _installDetail { get => _viewModel.InstallDetail; set => _viewModel.InstallDetail = value; }
    private string _releaseNotes { get => _viewModel.ReleaseNotes; set => _viewModel.ReleaseNotes = value; }
    private bool _running { get => _viewModel.Running; set => _viewModel.Running = value; }
    private bool _lastRepair;
    private bool _lastLaunch = true;
    private string _agentState { get => _viewModel.AgentState; set => _viewModel.AgentState = value; }
    private bool _agentStatusRefreshing;
    private bool _startupInitialized;
    private bool _windowMetricsInitialized;
    private int _layoutBucket = -1;
    private int _nextTabIndex;
    private readonly DispatcherTimer _agentStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, Bitmap> _projectVisualCache = new(StringComparer.OrdinalIgnoreCase);

    private TextBox? _configPathBox;
    private TextBox? _logBox;
    private TextBlock? _statusText;
    private TextBlock? _percentText;
    private TextBlock? _installStateText;
    private TextBlock? _installDetailText;
    private ProgressBar? _progress;
    private StackPanel? _projectList;
    private readonly List<Button> _actionButtons = new();
    private FileLogger? _fileLogger;

    // Download speed tracking for the status line.
    private long _speedLastBytes;
    private long _speedLastTickMs;
    private double _speedBytesPerSecond;

    private string BaseDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private string ConfigPath => ResolvePath(_configPathBox?.Text ?? _startupOptions.ConfigPath);
    private bool IsDeveloper => _viewModel.IsDeveloper;
    private string CurrentPlatform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    private ProjectStatePaths SelectedStatePaths =>
        LauncherPaths.For(_config, ConfigPath, _selectedProject.ProjectId, CurrentPlatform);

    public MainWindow() : this(LauncherStartupOptions.Discover(Array.Empty<string>()))
    {
    }

    public MainWindow(LauncherStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        InitializeComponent();
        LoadConfig();
        try { _fileLogger = new FileLogger(LauncherPaths.ResolveConfigRelative(ConfigPath, _config.LogDir)); } catch { _fileLogger = null; }
        Build();
        _agentStatusTimer.Tick += async (_, _) => await RefreshAgentStatusAsync();
        _agentStatusTimer.Start();
        Closed += (_, _) =>
        {
            _agentStatusTimer.Stop();
            foreach (var bitmap in _projectVisualCache.Values) bitmap.Dispose();
            _projectVisualCache.Clear();
        };
        Opened += async (_, _) => await InitializeStartupAsync();
        SizeChanged += (_, args) =>
        {
            var nextBucket = GeneralLayoutBucket(args.NewSize.Width);
            if (!IsDeveloper && _windowMetricsInitialized && nextBucket != _layoutBucket)
            {
                _layoutBucket = nextBucket;
                Build();
            }
        };
    }

    private async Task InitializeStartupAsync()
    {
        if (_startupInitialized) return;
        _startupInitialized = true;
        if (!File.Exists(ConfigPath))
        {
            _viewModel.GeneralState = GeneralLauncherState.ConfigurationRequired;
            return;
        }

        _viewModel.GeneralState = GeneralLauncherState.Checking;
        var serviceAvailable = await RefreshAgentStatusAsync();
        if (_config.IsManagedDeployment && !serviceAvailable)
        {
            _installState = "확인 필요";
            _installDetail = "업데이트 서비스에 연결한 뒤 다시 확인해 주세요.";
            _viewModel.GeneralState = GeneralLauncherState.RecoverableError;
            Build();
            return;
        }
        await RefreshCatalog(false, suppressDialog: true);
        await RefreshInstallStatusAsync(suppressDialog: true);
    }

    private void LoadConfig()
    {
        _config = new LauncherConfig();
        if (File.Exists(ConfigPath))
        {
            try { _config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath), JsonFiles.Options) ?? new LauncherConfig(); }
            catch (Exception ex) { _installState = "오류"; _installDetail = $"설정 파일 오류: {ex.GetBaseException().Message}"; }
        }
        else
        {
            _installState = "오류";
            _installDetail = IsDeveloper
                ? $"설정 파일이 없습니다: {ConfigPath}"
                : "런처 설정이 필요합니다. 관리자에게 문의해 주세요.";
            _viewModel.GeneralState = GeneralLauncherState.ConfigurationRequired;
        }

        _config.TargetPlatform = CurrentPlatform;
        if (!IsDeveloper)
        {
            _config.Environment = "prod";
            _config.Channel = "stable";
            _config.VersionPolicy = "latest";
            _config.RequestedVersion = null;
        }
        _agentState = LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Checking);

        if (_config.Projects.Count == 0)
        {
            _config.Projects.Add(new ProjectUiConfig
            {
                ProjectId = _config.ProjectId ?? "ue-dt-project",
                DisplayName = _config.ProjectId ?? "UE-DT 프로젝트",
                Description = IsDeveloper ? "개발자용 배포 프로젝트입니다." : "운영 안정화 배포 프로젝트입니다.",
                Status = IsDeveloper ? "개발 중" : "안정 버전",
                InstallPath = _config.InstallDir,
                Technology = CurrentPlatform,
                IsPinned = true
            });
        }
        SelectProject();
    }

    private void SelectProject()
    {
        var projects = VisibleProjects().ToList();
        _selectedProject = projects.FirstOrDefault(p => string.Equals(p.ProjectId, _config.ProjectId, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault()
            ?? _config.Projects.First();
        _config.ProjectId = _selectedProject.ProjectId;
        UpdateReleaseNotes();
    }

    private IEnumerable<ProjectUiConfig> VisibleProjects()
    {
        return _viewModel.VisibleProjects();
    }

    private void Build()
    {
        _actionButtons.Clear(); // controls are recreated below; Track() re-registers them
        _nextTabIndex = 0;
        Title = IsDeveloper ? "UE-DT Launcher - Developer" : "UE-DT Launcher";
        if (!_windowMetricsInitialized)
        {
            Width = IsDeveloper ? 1480 : 1280;
            Height = IsDeveloper ? 920 : 720;
            _windowMetricsInitialized = true;
        }
        var layout = CurrentLayout();
        MinWidth = layout.MinWidth;
        MinHeight = layout.MinHeight;
        _layoutBucket = GeneralLayoutBucket(CurrentLayoutWidth());
        Background = LauncherVisualTokens.Background(IsDeveloper);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions(layout.ShowSidebar ? "340,*" : "*"),
            Background = Background
        };
        root.Children.Add(Header());
        if (layout.ShowSidebar) root.Children.Add(Sidebar());
        root.Children.Add(MainArea(layout.ShowSidebar ? 1 : 0));
        Content = root;
    }

    private double CurrentLayoutWidth() => ClientSize.Width > 0 ? ClientSize.Width : Width;
    private int VisibleProjectCount() => _viewModel.ProjectsForProfile().Take(2).Count();
    private LauncherLayoutPolicy CurrentLayout() => LauncherLayoutPolicy.For(CurrentLayoutWidth(), VisibleProjectCount(), IsDeveloper);
    private static int GeneralLayoutBucket(double width) => width < 760 ? 0 : width < 980 ? 1 : width < 1100 ? 2 : 3;

    private Control Header()
    {
        var header = new Grid { Height = IsDeveloper ? 72 : 64, Margin = new Thickness(18, 8, 18, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumnSpan(header, 2);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = IsDeveloper ? 38 : 34, Height = IsDeveloper ? 38 : 34, CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl), Background = LauncherVisualTokens.Brush(LauncherVisualTokens.Accent), Child = new TextBlock { Text = "U", FontSize = IsDeveloper ? 24 : 21, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                new StackPanel { Children = { Txt(IsDeveloper ? "UE-DT Launcher" : "UE-DT 런처", IsDeveloper ? 24 : 21, true), Muted(IsDeveloper ? $"개발자용 배포 콘솔 · {CurrentPlatform}" : "프로젝트 업데이트 및 실행", 12) } }
            }
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (IsDeveloper)
        {
            right.Children.Add(Pill("개발자", "#1D4ED8", "#FFFFFF"));
            right.Children.Add(Pill(CurrentPlatform, "#1E293B", "#BFDBFE"));
        }
        else
        {
            right.Children.Add(Muted(CurrentPlatform, LauncherVisualTokens.FontCaption));
        }
        var serviceHealthy = _agentState.StartsWith("연결", StringComparison.Ordinal) || _agentState.EndsWith("정상", StringComparison.Ordinal);
        var servicePill = Pill(_agentState, serviceHealthy ? "#DCFCE7" : "#FEE2E2", serviceHealthy ? "#166534" : "#991B1B");
        ToolTip.SetTip(servicePill, IsDeveloper ? "관리 Agent 연결 상태" : "PC에서 업데이트를 안전하게 처리하는 서비스 상태");
        right.Children.Add(servicePill);
        if (!IsDeveloper && !CurrentLayout().ShowSidebar)
            right.Children.Add(SmallButton("설정", (_, _) => GeneralSettings()));
        Grid.SetColumn(right, 2);
        header.Children.Add(right);
        return header;
    }

    private async Task<bool> RefreshAgentStatusAsync()
    {
        if (_agentStatusRefreshing) return _agentState.StartsWith("연결", StringComparison.Ordinal) || _agentState.EndsWith("정상", StringComparison.Ordinal);
        _agentStatusRefreshing = true;
        var nextState = LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Disconnected);
        var connected = false;
        try
        {
            var response = await new ManagedAgentClient().SendAsync("status", timeout: TimeSpan.FromSeconds(2));
            var displayVersion = response.AgentVersion.Split('+')[0];
            connected = response.Success;
            nextState = response.Success
                ? LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Connected, displayVersion)
                : LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Error);
        }
        catch
        {
        }
        finally
        {
            _agentStatusRefreshing = false;
        }
        if (string.Equals(_agentState, nextState, StringComparison.Ordinal)) return connected;
        _agentState = nextState;
        await Dispatcher.UIThread.InvokeAsync(Build);
        return connected;
    }

    private Control Sidebar()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(Txt("배포 선택", 18, true));
        title.Children.Add(At(Track(SmallButton("↻", async (_, _) => await RefreshCatalog(true))), 1));
        grid.Children.Add(title);

        var search = new TextBox { Text = _search, Watermark = "프로젝트 검색", FontSize = 13, Background = B(IsDeveloper ? "#0F172A" : "#F9FAFB"), Foreground = Fg(), TabIndex = _nextTabIndex++ };
        AutomationProperties.SetName(search, "프로젝트 검색");
        search.TextChanged += (_, _) => { _search = search.Text ?? string.Empty; RenderProjects(); };
        grid.Children.Add(AtRow(search, 1));
        grid.Children.Add(AtRow(Filters(), 2));

        _projectList = new StackPanel { Spacing = 12 };
        RenderProjects();
        grid.Children.Add(AtRow(new ScrollViewer { Content = _projectList }, 3));

        var bottom = new StackPanel { Spacing = 8 };
        if (IsDeveloper)
        {
            _configPathBox = new TextBox { Text = ConfigPath, Watermark = "launcher.config.json", FontSize = 12, Background = B("#0F172A"), Foreground = Fg() };
            bottom.Children.Add(_configPathBox);
            bottom.Children.Add(SecondaryButton("설정 다시 읽기", (_, _) => { LoadConfig(); Build(); }, 38));
        }
        else
        {
            _configPathBox = new TextBox { Text = ConfigPath, IsVisible = false };
            bottom.Children.Add(SecondaryButton("설정", (_, _) => GeneralSettings(), 42));
        }
        grid.Children.Add(AtRow(bottom, 4));

        var card = Card(grid, 14);
        card.Margin = new Thickness(14, 8, 8, 14);
        return AtRow(card, 1);
    }

    private Control Filters()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Label($"카탈로그: {_catalogState}", 12, MutedBrush()));
        if (IsDeveloper)
        {
            panel.Children.Add(ComboLine("가동/개발", _config.Environment, new[] { "prod", "dev" }, v => { _config.Environment = v; if (v == "prod" && _config.Channel == "dev") _config.Channel = "stable"; SelectionChanged(); }));
            panel.Children.Add(ComboLine("채널", _config.Channel, _config.Environment == "dev" ? new[] { "dev", "beta", "stable" } : new[] { "stable", "beta" }, v => { _config.Channel = v; SelectionChanged(); }));
            panel.Children.Add(ComboLine("버전", _config.VersionPolicy, new[] { "latest", "exact" }, v => { _config.VersionPolicy = v; SelectionChanged(); }));
            if (string.Equals(_config.VersionPolicy, "exact", StringComparison.OrdinalIgnoreCase)) panel.Children.Add(ExactVersionLine());
        }
        else
        {
            var stable = Pill("운영 안정화 버전", "#EFF6FF", "#1D4ED8");
            AutomationProperties.SetName(stable, "운영 안정화 버전");
            panel.Children.Add(stable);
        }
        if (IsDeveloper) panel.Children.Add(ReadOnlyLine("OS", CurrentPlatform));
        return Card(panel, 12);
    }

    private Control ComboLine(string label, string selected, IEnumerable<string> values, Action<string> apply)
    {
        var list = values.ToList();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("82,*"), ColumnSpacing = 8 };
        row.Children.Add(Muted(label, 12));
        var combo = new ComboBox { ItemsSource = list, SelectedItem = list.FirstOrDefault(v => string.Equals(v, selected, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault(), MinHeight = 32, Background = B(IsDeveloper ? "#0F172A" : "#FFFFFF"), Foreground = Fg() };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string v) { apply(v); Build(); } };
        row.Children.Add(At(combo, 1));
        return row;
    }

    private Control ExactVersionLine()
    {
        var versions = MatchingReleases().Select(r => r.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (versions.Count > 0)
        {
            return ComboLine("요청 버전", _config.RequestedVersion ?? versions.First(), versions, v => { _config.RequestedVersion = v; SelectionChanged(); });
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("82,*"), ColumnSpacing = 8 };
        row.Children.Add(Muted("요청 버전", 12));
        var box = new TextBox { Text = _config.RequestedVersion ?? string.Empty, Watermark = "예: 1.0.3", FontSize = 12, MinHeight = 32, Background = B(IsDeveloper ? "#0F172A" : "#FFFFFF"), Foreground = Fg() };
        box.TextChanged += (_, _) => { _config.RequestedVersion = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim(); SelectionChanged(); };
        row.Children.Add(At(box, 1));
        return row;
    }

    private Control ReadOnlyLine(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("82,*"), ColumnSpacing = 8 };
        row.Children.Add(Muted(label, 12));
        row.Children.Add(At(Pill(value, IsDeveloper ? "#1E293B" : "#EFF6FF", IsDeveloper ? "#BFDBFE" : "#1D4ED8"), 1));
        return row;
    }

    private void SelectionChanged()
    {
        _config.TargetPlatform = CurrentPlatform;
        _installState = "확인 필요";
        _installDetail = "배포 선택이 변경되었습니다. 상태 확인을 다시 실행하세요.";
        UpdateReleaseNotes();
    }

    private void RenderProjects()
    {
        if (_projectList is null) return;
        _projectList.Children.Clear();
        foreach (var p in VisibleProjects()) _projectList.Children.Add(ProjectCard(p));
        if (_projectList.Children.Count == 0) _projectList.Children.Add(Card(Muted("검색 결과가 없습니다.", 13), 14));
    }

    private Control ProjectCard(ProjectUiConfig p)
    {
        var selected = string.Equals(p.ProjectId, _selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase);
        var count = _catalog.Releases.Count(r => string.Equals(r.ProjectId, p.ProjectId, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Platform, CurrentPlatform, StringComparison.OrdinalIgnoreCase));
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(Txt(p.DisplayName, 14, true));
        root.Children.Add(Label(count > 0 ? $"카탈로그 릴리스 {count}개" : p.Status ?? ModeStatus(), 12, count > 0 ? B("#16A34A") : StatusBrush(p.Status)));
        var badges = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 76, ItemHeight = 26 };
        foreach (var b in new[] { _config.Environment, _config.Channel, _config.VersionPolicy, CurrentPlatform.Replace("-x64", "") }) badges.Children.Add(MiniBadge(b));
        root.Children.Add(badges);
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*"), ColumnSpacing = 12 };
        content.Children.Add(ProjectThumbnail(p));
        content.Children.Add(At(root, 1));
        var card = Card(content, 12);
        card.MinHeight = 110;
        card.Background = B(IsDeveloper ? selected ? "#1E293B" : "#151E2A" : selected ? "#EFF6FF" : "#FFFFFF");
        card.BorderBrush = B(selected ? "#2563EB" : IsDeveloper ? "#253142" : "#E5E7EB");
        card.BorderThickness = new Thickness(selected ? 2 : 1);
        card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        card.PointerPressed += (_, _) => { _selectedProject = p; _config.ProjectId = p.ProjectId; SelectionChanged(); Build(); };
        return card;
    }

    private Control MiniBadge(string text) => new Border { Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(7, 3), CornerRadius = new CornerRadius(9), Background = B(IsDeveloper ? "#0F172A" : "#DBEAFE"), Child = Label(text, 11, IsDeveloper ? B("#BFDBFE") : B("#1D4ED8"), true) };

    private Control MainArea(int column)
    {
        var scroll = new ScrollViewer { Margin = new Thickness(8, 8, 14, 14), Content = IsDeveloper ? DeveloperBody() : GeneralBody() };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, column);
        return scroll;
    }

    private Control GeneralBody()
    {
        var layout = CurrentLayout();
        var body = new StackPanel { Spacing = 12 };
        if (layout.ShowTopProjectSelector) body.Children.Add(CompactProjectSelector());
        body.Children.Add(Hero(layout.HeroHeight));
        body.Children.Add(GeneralInfo());
        body.Children.Add(GeneralActions());
        body.Children.Add(StatusPanel(false));
        return body;
    }

    private Control CompactProjectSelector()
    {
        var projects = _viewModel.ProjectsForProfile().ToList();
        var names = projects.Select(project => project.DisplayName).ToList();
        var combo = new ComboBox
        {
            ItemsSource = names,
            SelectedIndex = Math.Max(0, projects.FindIndex(project => project.ProjectId.Equals(_selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase))),
            MinHeight = 40,
            TabIndex = _nextTabIndex++
        };
        AutomationProperties.SetName(combo, "프로젝트 선택");
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= projects.Count) return;
            _selectedProject = projects[combo.SelectedIndex];
            _config.ProjectId = _selectedProject.ProjectId;
            SelectionChanged();
            Build();
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        grid.Children.Add(Muted("프로젝트", 13));
        grid.Children.Add(At(combo, 1));
        return Card(grid, 12);
    }

    private Control DeveloperBody()
    {
        var lower = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        lower.Children.Add(ReleaseInfo());
        lower.Children.Add(At(StatusPanel(true), 1));
        return new StackPanel { Spacing = 16, Children = { Hero(250), DeveloperActions(), lower } };
    }

    private Control Hero(double h)
    {
        var hero = new Grid { Height = h };
        var asset = ProjectVisualResolver.Resolve(_selectedProject, ConfigPath, ProjectVisualKind.Hero);
        hero.Children.Add(ProjectHeroVisual(asset));
        hero.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusHero),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(230, 9, 27, 57), 0),
                    new GradientStop(Color.FromArgb(135, 15, 42, 86), 0.55),
                    new GradientStop(Color.FromArgb(35, 15, 42, 86), 1)
                }
            }
        });
        hero.Children.Add(new StackPanel { Spacing = 8, Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Bottom, Children = { Label(_selectedProject.DisplayName, IsDeveloper ? 28 : LauncherVisualTokens.FontProject, Brushes.White, true), Label(IsDeveloper ? $"개발자 모드 · {_config.Environment} · {_config.Channel} · {CurrentPlatform}" : $"운영 안정화 · stable · {CurrentPlatform}", LauncherVisualTokens.FontBody, B("#DCE8FF")), Label(_releaseNotes, LauncherVisualTokens.FontCaption, B("#EDF2FA")) } });
        return hero;
    }

    private Control ProjectHeroVisual(ProjectVisualAsset asset)
    {
        var bitmap = CachedProjectBitmap(asset);
        if (bitmap is not null)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusHero),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            };
        }

        return BrandedFallback(asset, hero: true);
    }

    private Control ProjectThumbnail(ProjectUiConfig project)
    {
        var asset = ProjectVisualResolver.Resolve(project, ConfigPath, ProjectVisualKind.Thumbnail);
        var bitmap = CachedProjectBitmap(asset);
        if (bitmap is not null)
        {
            return new Border
            {
                Width = 54,
                Height = 54,
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            };
        }

        var fallback = BrandedFallback(asset, hero: false);
        fallback.Width = 54;
        fallback.Height = 54;
        return fallback;
    }

    private Border BrandedFallback(ProjectVisualAsset asset, bool hero)
    {
        var accents = new[]
        {
            Color.Parse("#2563EB"),
            Color.Parse("#0F766E"),
            Color.Parse("#5B4BDB")
        };
        var accent = accents[Math.Clamp(asset.FallbackVariant, 0, accents.Length - 1)];
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(LauncherVisualTokens.BrandNavyDeep, 0),
                    new GradientStop(LauncherVisualTokens.BrandNavy, 0.55),
                    new GradientStop(accent, 1)
                }
            }
        });
        grid.Children.Add(new Border
        {
            Width = hero ? 360 : 44,
            Height = hero ? 360 : 44,
            CornerRadius = new CornerRadius(hero ? 180 : 22),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, hero ? 70 : -10, 0),
            Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255))
        });
        grid.Children.Add(new TextBlock
        {
            Text = asset.Initials,
            FontSize = hero ? 88 : 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(hero ? (byte)55 : (byte)220, 255, 255, 255)),
            HorizontalAlignment = hero ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = hero ? new Thickness(0, 0, 130, 0) : new Thickness(0)
        });
        return new Border
        {
            CornerRadius = new CornerRadius(hero ? LauncherVisualTokens.RadiusHero : LauncherVisualTokens.RadiusControl),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Bitmap? CachedProjectBitmap(ProjectVisualAsset asset)
    {
        if (!asset.HasImage || asset.ResolvedPath is null) return null;
        if (_projectVisualCache.TryGetValue(asset.ResolvedPath, out var cached)) return cached;
        try
        {
            var bitmap = new Bitmap(asset.ResolvedPath);
            _projectVisualCache[asset.ResolvedPath] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private Control GeneralInfo()
    {
        var columns = CurrentLayout().InfoColumns;
        var rows = (int)Math.Ceiling(3d / columns);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", columns))),
            RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", rows))),
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        var tiles = new Control[]
        {
            GeneralSummaryTile(),
            InfoTile(
                "설치",
                ReadInstalledVersion() is null ? "설치 전" : "설치됨",
                "설정에서 설치 위치 확인",
                _selectedProject.InstallPath ?? _config.InstallDir),
            InfoTile(
                "배포 정보",
                CatalogSummary(),
                "운영 안정화 배포",
                _catalogState)
        };
        for (var index = 0; index < tiles.Length; index++)
        {
            Grid.SetColumn(tiles[index], index % columns);
            Grid.SetRow(tiles[index], index / columns);
            grid.Children.Add(tiles[index]);
        }
        return grid;
    }

    private Control GeneralSummaryTile()
    {
        var installed = ReadInstalledVersion();
        var latest = LatestCatalogVersion();
        var stateColor = StatusBrush(_installState);
        var stateSoft = _installState.Contains("오류", StringComparison.Ordinal)
            ? LauncherVisualTokens.DangerSoft
            : _installState.Contains("업데이트", StringComparison.Ordinal) || _installState.Contains("설치 필요", StringComparison.Ordinal)
                ? LauncherVisualTokens.WarningSoft
                : _installState.Contains("최신", StringComparison.Ordinal) || _installState.Contains("설치", StringComparison.Ordinal)
                    ? LauncherVisualTokens.SuccessSoft
                    : LauncherVisualTokens.AccentSoft;
        var iconKind = _installState.Contains("오류", StringComparison.Ordinal)
            ? LauncherIconKind.Warning
            : _installState.Contains("업데이트", StringComparison.Ordinal) || _installState.Contains("설치 필요", StringComparison.Ordinal)
                ? LauncherIconKind.Download
                : _installState.Contains("최신", StringComparison.Ordinal) || _installState.Contains("설치", StringComparison.Ordinal)
                    ? LauncherIconKind.Check
                    : LauncherIconKind.Shield;
        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = LauncherVisualTokens.Brush(stateSoft),
            Child = LauncherIconFactory.Create(iconKind, 21, stateColor)
        };
        _installStateText = Label(_installState, LauncherVisualTokens.FontStatus, stateColor, true);
        _installDetailText = Label(_installDetail, LauncherVisualTokens.FontCaption, MutedBrush());
        _installDetailText.MaxLines = 2;
        _installDetailText.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                Muted("프로젝트 상태", LauncherVisualTokens.FontCaption),
                _installStateText,
                _installDetailText
            }
        };
        var status = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        status.Children.Add(icon);
        status.Children.Add(At(text, 1));
        var versions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, Margin = new Thickness(54, 8, 0, 0) };
        versions.Children.Add(VersionValue("설치 버전", installed ?? "미설치"));
        versions.Children.Add(At(VersionValue("최신 버전", latest ?? "확인 필요"), 1));
        return Card(new StackPanel { Spacing = 4, MinHeight = 92, Children = { status, versions } }, 16);
    }

    private Control VersionValue(string label, string value) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            Muted(label, 11),
            Label(value, LauncherVisualTokens.FontBody, Fg(), true)
        }
    };

    private string CatalogSummary()
    {
        if (_catalogState.Contains("완료", StringComparison.Ordinal)) return "배포 정보 정상";
        if (_catalogState.Contains("오류", StringComparison.Ordinal)) return "확인 필요";
        if (_catalogState.Contains("확인 중", StringComparison.Ordinal)) return "확인 중";
        return "미확인";
    }

    private Control VersionTile()
    {
        var installed = ReadInstalledVersion();
        var latest = LatestCatalogVersion();
        var upToDate = installed is not null && latest is not null && string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);
        var valueBrush = installed is null ? MutedBrush() : upToDate ? B("#16A34A") : latest is null ? Fg() : B("#F97316");
        var caption = latest is null
            ? "최신: 카탈로그 확인 필요"
            : upToDate ? $"최신: {latest} · 최신 상태입니다"
            : installed is null ? $"최신: {latest}"
            : $"최신: {latest} · 업데이트가 있습니다";

        var panel = new StackPanel { Spacing = 6, MinHeight = 92 };
        panel.Children.Add(Muted("설치 버전", 13));
        panel.Children.Add(Label(installed ?? "미설치", 17, valueBrush, true));
        panel.Children.Add(Muted(caption, 12));
        return Card(panel, 18);
    }

    private string? ReadInstalledVersion()
    {
        try
        {
            var statePath = SelectedStatePaths.InstallStatePath;
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath), JsonFiles.Options);
                if (!string.IsNullOrWhiteSpace(state?.Version)) return state.Version;
            }

            var manifestPath = SelectedStatePaths.InstalledManifestPath;
            if (File.Exists(manifestPath))
            {
                var manifest = JsonSerializer.Deserialize<LauncherManifest>(File.ReadAllText(manifestPath), JsonFiles.Options);
                if (!string.IsNullOrWhiteSpace(manifest?.Version)) return manifest.Version;
            }
        }
        catch
        {
            // Unreadable local metadata simply shows as "not installed".
        }

        return null;
    }

    private string? LatestCatalogVersion()
    {
        if (string.Equals(_config.VersionPolicy, "exact", StringComparison.OrdinalIgnoreCase)) return _config.RequestedVersion;
        var releases = MatchingReleases().ToList();
        return releases.FirstOrDefault(r => r.IsLatest)?.Version ?? releases.FirstOrDefault()?.Version;
    }

    private Control StatusTile()
    {
        var panel = new StackPanel { Spacing = 6, MinHeight = 92 };
        panel.Children.Add(Muted("설치 상태", 13));
        _installStateText = Label(_installState, 17, StatusBrush(_installState), true);
        _installDetailText = Label(_installDetail, 12, MutedBrush());
        panel.Children.Add(_installStateText);
        panel.Children.Add(_installDetailText);
        return Card(panel, 18);
    }

    private Control GeneralActions()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.2*,*,*"), ColumnSpacing = 10, MinHeight = 80 };
        var run = Track(PrimaryButton("▶ " + _viewModel.PrimaryActionText, async (_, _) => await ExecutePrimaryActionAsync(), 80));
        run.IsEnabled = _viewModel.PrimaryAction != PrimaryActionKind.Disabled && !_running;
        run.HotKey = new KeyGesture(Key.F5);
        grid.Children.Add(run);
        var secondaryText = _viewModel.GeneralState == GeneralLauncherState.RecoverableError ? "문제 해결" : "상태 새로고침";
        var status = Track(SecondaryButton(secondaryText, async (_, _) =>
        {
            if (_viewModel.GeneralState == GeneralLauncherState.RecoverableError) await TroubleshootAsync();
            else await RefreshInstallStatusAsync();
        }, 80));
        status.HotKey = new KeyGesture(Key.F6);
        grid.Children.Add(At(status, 1));
        var folder = SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 80);
        Grid.SetColumn(folder, 2); grid.Children.Add(folder);
        if (run.Content is TextBlock runText) runText.FontSize = 18;
        return Card(grid, 12);
    }

    private Control DeveloperActions()
    {
        var panel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*"), ColumnSpacing = 10 };
        var run = Track(PrimaryButton("▶ 실행", async (_, _) => await RunAsync(false, true), 50));
        run.HotKey = new KeyGesture(Key.F5);
        row1.Children.Add(run);
        Add(row1, Track(SecondaryButton("업데이트", async (_, _) => await RunAsync(false, false), 50)), 1);
        Add(row1, Track(SecondaryButton("상태 확인", async (_, _) => await RefreshInstallStatusAsync(), 50)), 2);
        Add(row1, Track(SecondaryButton("검증/복구", async (_, _) => await RunAsync(true, false), 50)), 3);
        Add(row1, SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 50), 4);
        Add(row1, Track(SecondaryButton("캐시 정리", (_, _) => ClearCache(), 50)), 5);
        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("로그 ZIP", (_, _) => ExportLogsZip(), 42));
        Add(row2, SecondaryButton("로그 지우기", (_, _) => ClearLog(), 42), 1);
        Add(row2, Track(SecondaryButton("다시 시도", async (_, _) => await RunAsync(_lastRepair, _lastLaunch), 42)), 2);
        Add(row2, Track(SecondaryButton("롤백", async (_, _) => await RollbackLatestAsync(), 42)), 3);
        Add(row2, Track(SecondaryButton("백업 정리", (_, _) => CleanupBackups(), 42)), 4);
        Add(row2, SecondaryButton("설정 팝업", (_, _) => DeveloperSettings(), 42), 5);
        panel.Children.Add(row1); panel.Children.Add(row2);
        return Card(panel, 16);
    }

    private Control ReleaseInfo()
    {
        return Card(new StackPanel { Spacing = 8, Children = { Txt("선택된 배포 정보", 18, true), KeyValue("프로젝트", _selectedProject.ProjectId), KeyValue("프로필", _config.ClientProfile), KeyValue("가동/개발", _config.Environment), KeyValue("채널", _config.Channel), KeyValue("버전 정책", _config.VersionPolicy == "exact" ? $"exact / {_config.RequestedVersion ?? "미입력"}" : "latest"), KeyValue("설치 버전", ReadInstalledVersion() ?? "미설치"), KeyValue("최신 버전", LatestCatalogVersion() ?? "카탈로그 확인 필요"), KeyValue("OS", CurrentPlatform), KeyValue("카탈로그", _catalogState), KeyValue("릴리스 노트", _releaseNotes), KeyValue("캐시/백업", StorageSummary()) } }, 18);
    }

    private Control StatusPanel(bool developerLog)
    {
        var panel = new StackPanel { Spacing = developerLog ? 10 : 8 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        _statusText = Txt("준비 완료", developerLog ? 18 : LauncherVisualTokens.FontStatus, true);
        header.Children.Add(_statusText);
        _percentText = Label("0%", developerLog ? 18 : LauncherVisualTokens.FontStatus, IsDeveloper ? B("#BFDBFE") : LauncherVisualTokens.Brush(LauncherVisualTokens.Accent), true);
        header.Children.Add(At(_percentText, 1));
        panel.Children.Add(header);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = developerLog ? 12 : 8 };
        panel.Children.Add(_progress);
        if (!developerLog) panel.Children.Add(WorkflowSteps());
        _logBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = developerLog ? 260 : 72, Text = IsDeveloper ? $"config: {ConfigPath}{Environment.NewLine}profile: {_config.ClientProfile}{Environment.NewLine}platform: {CurrentPlatform}" : "업데이트 상태가 여기에 표시됩니다.", Background = B(IsDeveloper ? "#0B1220" : "#FFFFFF"), Foreground = Fg() };
        if (developerLog) panel.Children.Add(_logBox);
        return Card(panel, developerLog ? 20 : 16);
    }

    private Control WorkflowSteps()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (stage, text) in new[]
                 {
                     (LauncherWorkflowStage.Catalog, "배포 확인"),
                     (LauncherWorkflowStage.Download, "다운로드"),
                     (LauncherWorkflowStage.Verify, "검증"),
                     (LauncherWorkflowStage.Apply, "설치"),
                     (LauncherWorkflowStage.Launch, "실행")
                 })
        {
            var active = _viewModel.WorkflowStage == stage;
            var complete = _viewModel.WorkflowStage > stage;
            var iconKind = complete ? LauncherIconKind.Check : stage switch
            {
                LauncherWorkflowStage.Catalog => LauncherIconKind.Shield,
                LauncherWorkflowStage.Download => LauncherIconKind.Download,
                LauncherWorkflowStage.Verify => LauncherIconKind.Check,
                LauncherWorkflowStage.Apply => LauncherIconKind.Package,
                _ => LauncherIconKind.Play
            };
            var foreground = LauncherVisualTokens.Brush(active
                ? LauncherVisualTokens.Accent
                : complete ? LauncherVisualTokens.Success : LauncherVisualTokens.LightMutedText);
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children =
                {
                    LauncherIconFactory.Create(iconKind, 13, foreground),
                    Label(text, LauncherVisualTokens.FontCaption, foreground, active || complete)
                }
            };
            row.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 8, 2),
                Padding = new Thickness(9, 4),
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
                Background = LauncherVisualTokens.Brush(active
                    ? LauncherVisualTokens.AccentSoft
                    : complete ? LauncherVisualTokens.SuccessSoft : LauncherVisualTokens.LightSurfaceMuted),
                Child = content
            });
        }
        AutomationProperties.SetName(row, "업데이트 진행 단계");
        return row;
    }

    private async Task ExecutePrimaryActionAsync()
    {
        switch (_viewModel.PrimaryAction)
        {
            case PrimaryActionKind.InstallAndLaunch:
            case PrimaryActionKind.UpdateAndLaunch:
            case PrimaryActionKind.Launch:
                await RunAsync(false, true);
                break;
            case PrimaryActionKind.RetryCheck:
                await RefreshAgentStatusAsync();
                await RefreshCatalog(false, suppressDialog: true);
                await RefreshInstallStatusAsync();
                break;
        }
    }

    private async Task RefreshCatalog(bool rebuild, bool suppressDialog = false)
    {
        try
        {
            _catalogState = "카탈로그 확인 중...";
            if (rebuild) Build();
            var catalogConfig = await RunConfig(false, false);
            _catalog = await CatalogSnapshotService.LoadAsync(catalogConfig, CurrentPlatform);
            _catalogState = _catalog.Status;
            MergeCatalogProjects();
            UpdateReleaseNotes();
        }
        catch (Exception ex)
        {
            _catalogState = "카탈로그 오류";
            AppendLog("카탈로그 확인 실패: " + FriendlyError(ex), true);
            if (!IsDeveloper && !suppressDialog) ErrorDialog("카탈로그 확인 실패", FriendlyError(ex));
        }
        finally { if (rebuild) Build(); }
    }

    private void MergeCatalogProjects()
    {
        foreach (var cp in _catalog.Projects)
        {
            if (_config.Projects.Any(p => string.Equals(p.ProjectId, cp.ProjectId, StringComparison.OrdinalIgnoreCase))) continue;
            _config.Projects.Add(new ProjectUiConfig { ProjectId = cp.ProjectId, DisplayName = cp.DisplayName, Description = "카탈로그에서 발견된 프로젝트입니다.", Status = $"릴리스 {cp.ReleaseCount}개", InstallPath = $"apps/{cp.ProjectId}", Technology = CurrentPlatform, SortOrder = 100, VisibleToProfiles = new List<string> { _config.ClientProfile } });
        }
        SelectProject();
    }

    private IEnumerable<CatalogReleaseOption> MatchingReleases() => _catalog.Releases.Where(r => r.ProjectId.Equals(_selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase) && r.Platform.Equals(CurrentPlatform, StringComparison.OrdinalIgnoreCase) && r.Environment.Equals(_config.Environment, StringComparison.OrdinalIgnoreCase) && r.Channel.Equals(_config.Channel, StringComparison.OrdinalIgnoreCase));

    private void UpdateReleaseNotes()
    {
        var release = MatchingReleases().FirstOrDefault(r => _config.VersionPolicy == "exact" ? r.Version.Equals(_config.RequestedVersion, StringComparison.OrdinalIgnoreCase) : r.IsLatest) ?? MatchingReleases().FirstOrDefault();
        _releaseNotes = string.IsNullOrWhiteSpace(release?.Notes) ? _selectedProject.Description ?? "릴리스 노트가 없습니다." : release.Notes!;
    }

    private async Task<LauncherConfig> RunConfig(bool repair, bool launch)
    {
        var c = await LauncherPaths.LoadResolvedAsync(ConfigPath);
        c.ProjectId = _selectedProject.ProjectId; c.Environment = _config.Environment; c.Channel = _config.Channel; c.TargetPlatform = CurrentPlatform; c.VersionPolicy = _config.VersionPolicy; c.RequestedVersion = _config.RequestedVersion; c.RepairMode = repair; c.LaunchAfterUpdate = launch;
        LauncherPaths.ResolveInPlace(c, ConfigPath, _selectedProject.InstallPath);
        return c;
    }

    private async Task RunAsync(bool repair, bool launch)
    {
        if (_running) return;
        if (IsDeveloper && launch && !await ConfirmDevLaunch()) return;
        _running = true; _viewModel.GeneralState = GeneralLauncherState.Working; SetBusy(true); _lastRepair = repair; _lastLaunch = launch; Progress(0); ResetSpeedTracking();
        try
        {
            if (_statusText is not null) _statusText.Text = launch ? "실행 준비 중..." : "업데이트 확인 중...";
            var c = await RunConfig(repair, launch);
            if (c.IsManagedDeployment)
            {
                var response = await new ManagedAgentClient().SendStreamingAsync(
                    repair ? "repair" : "update",
                    c.ProjectId,
                    ReportManagedProgress);
                if (!response.Success) throw new InvalidOperationException(response.Message);
                if (launch) _ = await ManagedAppLauncher.LaunchAsync(c);
            }
            else
            {
                // The whole resolve + update pipeline runs off the UI thread; progress is marshaled back.
                await Task.Run(async () =>
                {
                    using var http = SecureHttpClientFactory.Create(c);
                    await CatalogResolver.ResolveAsync(c, http, (s, m, p) => Dispatcher.UIThread.Post(() => UiProgress(s, m, p)));
                    using var engine = new LauncherEngine(c, p => Dispatcher.UIThread.Post(() => EngineProgress(p)), _fileLogger);
                    await engine.RunAsync();
                });
            }
            _viewModel.GeneralState = GeneralLauncherState.Ready;
            _viewModel.WorkflowStage = LauncherWorkflowStage.Complete;
            _installState = "최신 상태"; _installDetail = "현재 설치된 파일이 최신 배포 정보와 일치합니다.";
            Build(); // refresh the version tile and release info with the new install state
            Progress(100); if (_statusText is not null) _statusText.Text = launch ? "실행되었습니다." : "최신 상태입니다.";
        }
        catch (Exception ex) { MarkError(ex); }
        finally { _running = false; SetBusy(false); }
    }

    private async Task RefreshInstallStatusAsync(bool suppressDialog = false)
    {
        if (_running) return;
        _running = true; _viewModel.GeneralState = GeneralLauncherState.Checking; SetBusy(true); Progress(0);
        try
        {
            if (_statusText is not null) _statusText.Text = "설치 상태 확인 중..."; Progress(5);
            var c = await RunConfig(false, false);
            if (c.IsManagedDeployment)
            {
                var response = await new ManagedAgentClient().SendStreamingAsync(
                    "check",
                    c.ProjectId,
                    ReportManagedProgress,
                    timeout: TimeSpan.FromMinutes(5));
                if (!response.Success) throw new InvalidOperationException(response.Message);
                if (response.ProjectStatus is not null) _viewModel.ApplyProjectStatus(response.ProjectStatus);
                else _viewModel.GeneralState = ReadInstalledVersion() is null ? GeneralLauncherState.NotInstalled : GeneralLauncherState.Ready;
                _installState = _viewModel.GeneralState switch
                {
                    GeneralLauncherState.NotInstalled => "설치 필요",
                    GeneralLauncherState.UpdateAvailable => "업데이트 가능",
                    _ => "최신 상태"
                };
                _installDetail = IsDeveloper
                    ? response.Message
                    : response.ProjectStatus is { UpdateRequired: true, IsInstalled: true } status
                        ? $"새 버전 {status.AvailableVersion}을 설치할 수 있습니다."
                        : response.ProjectStatus is { IsInstalled: false }
                            ? "프로젝트를 처음 설치할 수 있습니다."
                            : "설치된 파일이 최신 배포와 일치합니다.";
                Build();
                Progress(100);
                if (_statusText is not null) _statusText.Text = _installState;
                return;
            }
            var (missing, changed, total, version) = await Task.Run(async () =>
            {
                using var http = SecureHttpClientFactory.Create(c);
                await CatalogResolver.ResolveAsync(c, http, (s, m, p) => Dispatcher.UIThread.Post(() => UiProgress(s, m, p)));
                var manifestDocument = await ManifestDownloader.DownloadAsync(c, http);
                var manifest = manifestDocument.Manifest;
                var missingCount = 0; var changedCount = 0;
                foreach (var file in manifest.Files)
                {
                    var installed = SafePath.ResolveInside(c.InstallDir, file.Path);
                    if (!File.Exists(installed)) { missingCount++; continue; }
                    if (!await Hashing.Sha256MatchesAsync(installed, file.Sha256)) changedCount++;
                }
                return (missingCount, changedCount, manifest.Files.Count, manifest.Version);
            });
            if (missing == total) { _viewModel.GeneralState = GeneralLauncherState.NotInstalled; _installState = "설치 필요"; _installDetail = "아직 설치된 파일을 찾지 못했습니다."; }
            else if (missing > 0 || changed > 0) { _viewModel.GeneralState = GeneralLauncherState.UpdateAvailable; _installState = "업데이트 가능"; _installDetail = $"누락 {missing}개, 변경 {changed}개 파일이 있습니다."; }
            else { _viewModel.GeneralState = GeneralLauncherState.Ready; _installState = "최신 상태"; _installDetail = $"{version} 버전이 설치되어 있습니다."; }
            Build();
            Progress(100); if (_statusText is not null) _statusText.Text = _installState;
        }
        catch (Exception ex) { MarkError(ex, "상태 확인 실패", showDialog: !suppressDialog); }
        finally { _running = false; SetBusy(false); }
    }

    private async Task TroubleshootAsync()
    {
        if (_running) return;
        _running = true;
        _viewModel.GeneralState = GeneralLauncherState.Working;
        SetBusy(true);
        Progress(0);
        try
        {
            var client = new ManagedAgentClient();
            var service = await client.SendAsync("status", timeout: TimeSpan.FromSeconds(3));
            if (!service.Success) throw new InvalidOperationException(service.Message);
            var config = await RunConfig(false, false);
            var check = await client.SendStreamingAsync(
                "check",
                config.ProjectId,
                ReportManagedProgress,
                timeout: TimeSpan.FromMinutes(5));
            if (!check.Success) throw new InvalidOperationException(check.Message);
            if (check.ProjectStatus is not null) _viewModel.ApplyProjectStatus(check.ProjectStatus);

            if (check.ProjectStatus is { UpdateRequired: true })
            {
                var repair = await client.SendStreamingAsync(
                    "repair",
                    config.ProjectId,
                    ReportManagedProgress);
                if (!repair.Success) throw new InvalidOperationException(repair.Message);
                var verified = await client.SendStreamingAsync(
                    "check",
                    config.ProjectId,
                    ReportManagedProgress,
                    timeout: TimeSpan.FromMinutes(5));
                if (!verified.Success || verified.ProjectStatus is { UpdateRequired: true })
                    throw new InvalidOperationException(verified.Message);
                if (verified.ProjectStatus is not null) _viewModel.ApplyProjectStatus(verified.ProjectStatus);
            }

            _viewModel.GeneralState = GeneralLauncherState.Ready;
            _viewModel.WorkflowStage = LauncherWorkflowStage.Complete;
            _installState = "문제 해결 완료";
            _installDetail = "프로젝트 파일과 업데이트 서비스를 정상 상태로 복구했습니다.";
            Progress(100);
            Build();
        }
        catch (Exception ex)
        {
            var canRollback = _viewModel.ProjectStatus?.HasBackup == true;
            if (canRollback && await ConfirmRollback("업데이트 서비스 최신 백업", null))
            {
                try
                {
                    var config = await RunConfig(false, false);
                    var response = await new ManagedAgentClient().SendStreamingAsync(
                        "rollback",
                        config.ProjectId,
                        ReportManagedProgress,
                        timeout: TimeSpan.FromMinutes(10));
                    if (!response.Success) throw new InvalidOperationException(response.Message);
                    _viewModel.GeneralState = GeneralLauncherState.Ready;
                    _installState = "복구 완료";
                    _installDetail = "안정적인 이전 버전으로 복구했습니다.";
                    Progress(100);
                    Build();
                }
                catch (Exception rollbackError) { MarkError(rollbackError, "문제 해결 실패"); }
            }
            else
            {
                MarkError(ex, "문제 해결 실패");
            }
        }
        finally
        {
            _running = false;
            SetBusy(false);
        }
    }

    private async Task RollbackLatestAsync()
    {
        if (_running) return;
        var config = await RunConfig(false, false);
        if (config.IsManagedDeployment)
        {
            if (!await ConfirmRollback("Agent 최신 백업", null)) return;
            _running = true; SetBusy(true); Progress(0);
            try
            {
                var response = await new ManagedAgentClient().SendAsync("rollback", config.ProjectId);
                foreach (var progress in response.Progress) UiProgress(progress.Stage, progress.Message, progress.Percent);
                if (!response.Success) throw new InvalidOperationException(response.Message);
                _installState = "롤백 완료";
                _installDetail = "관리 Agent가 가장 최근 백업을 복원했습니다.";
                Build();
                Progress(100);
            }
            catch (Exception ex) { MarkError(ex, "롤백 실패"); }
            finally { _running = false; SetBusy(false); }
            return;
        }
        var backups = BackupManager.List(config.BackupDir);
        if (backups.Count == 0)
        {
            AppendLog("롤백할 백업이 없습니다.", true);
            if (!IsDeveloper) ErrorDialog("롤백 불가", "보관된 백업이 없어 이전 버전으로 되돌릴 수 없습니다.");
            return;
        }

        var (backupRoot, info) = backups[0];
        if (!await ConfirmRollback(Path.GetFileName(backupRoot), info)) return;

        _running = true; SetBusy(true); Progress(0);
        try
        {
            if (_statusText is not null) _statusText.Text = "이전 버전으로 되돌리는 중...";
            await Task.Run(() => BackupManager.RestoreAsync(backupRoot, config.InstallDir, config.InstalledManifestPath, config.InstallStatePath,
                m => Dispatcher.UIThread.Post(() => AppendLog("롤백: " + m, true))));
            _installState = "롤백 완료"; _installDetail = $"{info?.PreviousVersion ?? "이전"} 버전으로 되돌렸습니다. 상태 확인으로 검증하세요.";
            _fileLogger?.Log("Rollback", $"GUI rollback to backup {Path.GetFileName(backupRoot)} completed.");
            Build();
            Progress(100); if (_statusText is not null) _statusText.Text = "롤백이 완료되었습니다.";
        }
        catch (Exception ex) { MarkError(ex, "롤백 실패"); }
        finally { _running = false; SetBusy(false); }
    }

    private async Task<bool> ConfirmRollback(string backupName, BackupInfo? info)
    {
        var dialog = new Window { Title = "이전 버전으로 롤백", Width = 500, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B(IsDeveloper ? "#0B111A" : "#F5F7FB") };
        var cancel = SecondaryButton("취소", (_, _) => dialog.Close(false), 42);
        var ok = PrimaryButton("롤백 실행", (_, _) => dialog.Close(true), 42);
        dialog.Content = new Border { Padding = new Thickness(22), Child = new StackPanel { Spacing = 12, Children = { Txt("가장 최근 백업으로 되돌릴까요?", 20, true), KeyValue("백업", backupName), KeyValue("되돌릴 버전", info?.PreviousVersion ?? "알 수 없음"), KeyValue("현재(업데이트된) 버전", info?.NewVersion ?? "알 수 없음"), Muted("롤백 후에는 '상태 확인'으로 파일 상태를 검증하는 것을 권장합니다.", 12), new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Children = { cancel, At(ok, 1) } } } } };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmDevLaunch()
    {
        var dialog = new Window { Title = "개발자 배포 실행 확인", Width = 500, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("#0B111A") };
        var cancel = SecondaryButton("취소", (_, _) => dialog.Close(false), 42); var run = PrimaryButton("실행", (_, _) => dialog.Close(true), 42);
        dialog.Content = new Border { Padding = new Thickness(22), Child = new StackPanel { Spacing = 12, Children = { Txt("선택한 개발자 배포를 실행할까요?", 22, true), KeyValue("프로젝트", _selectedProject.ProjectId), KeyValue("가동/개발", _config.Environment), KeyValue("채널", _config.Channel), KeyValue("버전", _config.VersionPolicy == "exact" ? _config.RequestedVersion ?? "미입력" : "latest"), KeyValue("OS", CurrentPlatform), new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Children = { cancel, At(run, 1) } } } } };
        return await dialog.ShowDialog<bool>(this);
    }

    private void EngineProgress(LauncherProgress p)
    {
        if (p.Stage == "DownloadProgress" && p.TotalBytes is > 0 && p.BytesDownloaded is not null)
        {
            UpdateSpeed(p.BytesDownloaded.Value);
            var overall = Math.Clamp(p.BytesDownloaded.Value / (double)p.TotalBytes.Value * 100, 0, 100);
            var speedText = _speedBytesPerSecond > 0 ? $"{FormatBytes((long)_speedBytesPerSecond)}/s" : "측정 중";
            if (_statusText is not null) _statusText.Text = $"다운로드 중 · 파일 {p.FileIndex}/{p.FileCount} · {speedText} · 전체 {overall:0}%";
            if (p.Percent.HasValue) Progress(p.Percent.Value);
            return; // byte-level ticks update the status line only, not the log
        }

        UiProgress(p.Stage, p.Message, p.Percent);
    }

    private void ResetSpeedTracking() { _speedLastBytes = 0; _speedLastTickMs = 0; _speedBytesPerSecond = 0; }

    private void UpdateSpeed(long bytesDownloaded)
    {
        var now = Environment.TickCount64;
        if (_speedLastTickMs == 0 || bytesDownloaded < _speedLastBytes)
        {
            _speedLastTickMs = now; _speedLastBytes = bytesDownloaded;
            return;
        }

        var elapsedMs = now - _speedLastTickMs;
        if (elapsedMs < 400) return; // smooth the reading
        var instant = (bytesDownloaded - _speedLastBytes) * 1000.0 / elapsedMs;
        _speedBytesPerSecond = _speedBytesPerSecond <= 0 ? instant : _speedBytesPerSecond * 0.6 + instant * 0.4;
        _speedLastTickMs = now; _speedLastBytes = bytesDownloaded;
    }

    private Button Track(Button button) { _actionButtons.Add(button); return button; }

    private void SetBusy(bool busy)
    {
        foreach (var button in _actionButtons) button.IsEnabled = !busy;
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        _viewModel.ApplyProgressStage(stage);
        if (_statusText is not null) _statusText.Text = IsDeveloper ? $"{stage}: {message}" : FriendlyProgress(stage, message);
        if (percent.HasValue) Progress(percent.Value); else Progress(stage switch { "Catalog" => 10, "Manifest" => 20, "Plan" => 35, "Download" => Math.Max(_progress?.Value ?? 0, 45), "Apply" => 85, "Package" => 88, "Launch" => 95, _ => Math.Max(_progress?.Value ?? 0, 5) });
        AppendLog(IsDeveloper ? $"[{stage}] {message}" : FriendlyProgress(stage, message));
    }

    private void ReportManagedProgress(ManagedAgentProgress progress)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UiProgress(progress.Stage, progress.Message, progress.Percent);
            return;
        }

        Dispatcher.UIThread.InvokeAsync(() => UiProgress(progress.Stage, progress.Message, progress.Percent))
            .GetAwaiter()
            .GetResult();
    }

    private void Progress(double v) { var c = Math.Clamp(v, 0, 100); if (_progress is not null) _progress.Value = c; if (_percentText is not null) _percentText.Text = $"{c:0}%"; }
    private string FriendlyProgress(string stage, string message) => stage switch { "Catalog" => "배포 정보를 확인하고 있습니다...", "Manifest" => "업데이트 정보를 확인하고 있습니다...", "Plan" => "필요한 파일을 확인하고 있습니다...", "Download" => "필요한 파일을 다운로드하고 있습니다...", "Apply" => "업데이트를 적용하고 있습니다...", "Package" => "패키지를 처리하고 있습니다...", "Launch" => "프로젝트를 실행하고 있습니다...", _ => message };
    private void MarkError(Exception ex, string status = "작업 실패", bool showDialog = true)
    {
        _installState = "오류";
        _installDetail = FriendlyError(ex);
        _viewModel.GeneralState = GeneralLauncherState.RecoverableError;
        _viewModel.WorkflowStage = LauncherWorkflowStage.None;
        Build();
        if (_statusText is not null) _statusText.Text = status;
        AppendLog("오류: " + FriendlyError(ex), true);
        if (IsDeveloper) AppendLog(ex.ToString(), true);
        else if (showDialog) ErrorDialog(status, FriendlyError(ex));
    }
    private string FriendlyError(Exception ex) => _viewModel.FriendlyError(ex);
    private void UpdateInstallTile() { if (_installStateText is not null) { _installStateText.Text = _installState; _installStateText.Foreground = StatusBrush(_installState); } if (_installDetailText is not null) _installDetailText.Text = _installDetail; }

    private void ErrorDialog(string title, string message)
    {
        var d = new Window { Title = title, Width = 520, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("#F5F7FB") };
        d.Content = new Border { Padding = new Thickness(22), Child = new StackPanel { Spacing = 12, Children = { Label(title, 22, B("#111827"), true), Label(message, 14, B("#374151")), SecondaryButton("다시 시도", async (_, _) => { d.Close(); await RunAsync(_lastRepair, _lastLaunch); }, 42), SecondaryButton("로그 ZIP 저장", (_, _) => ExportLogsZip(), 42), SecondaryButton("닫기", (_, _) => d.Close(), 42) } } };
        d.Show(this);
    }

    private void GeneralSettings() { SettingsDialog(false); }
    private void DeveloperSettings() { SettingsDialog(true); }
    private void SettingsDialog(bool dev)
    {
        var d = new Window { Title = dev ? "개발자 설정" : "설정", Width = dev ? 640 : 520, Height = dev ? 560 : 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B(dev ? "#0B111A" : "#F5F7FB") };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Txt(dev ? "개발자 설정" : "런처 설정", 24, true));
        if (dev)
        {
            content.Children.Add(KeyValue("설정 파일", ConfigPath));
            content.Children.Add(KeyValue("프로젝트", _selectedProject.ProjectId));
            content.Children.Add(KeyValue("가동/개발", _config.Environment));
            content.Children.Add(KeyValue("채널", _config.Channel));
            content.Children.Add(KeyValue("버전", _config.VersionPolicy == "exact" ? _config.RequestedVersion : _config.VersionPolicy));
        }
        else
        {
            content.Children.Add(KeyValue("프로젝트", _selectedProject.DisplayName));
            content.Children.Add(KeyValue("배포", "운영 안정화 버전"));
        }
        content.Children.Add(KeyValue("OS", CurrentPlatform));
        content.Children.Add(KeyValue("설치 버전", ReadInstalledVersion() ?? "미설치"));
        content.Children.Add(KeyValue("캐시/백업", StorageSummary()));
        content.Children.Add(SecondaryButton("설치 폴더 열기", (_, _) => OpenInstallFolder(), 40));
        if (dev) content.Children.Add(SecondaryButton("이전 버전으로 롤백", async (_, _) => { d.Close(); await RollbackLatestAsync(); }, 40));
        content.Children.Add(SecondaryButton("로그 ZIP 저장", (_, _) => ExportLogsZip(), 40));
        content.Children.Add(SecondaryButton("설정 새로고침", (_, _) => { LoadConfig(); Build(); }, 40));
        content.Children.Add(SecondaryButton("닫기", (_, _) => d.Close(), 40));
        d.Content = new Border { Padding = new Thickness(22), Child = content };
        d.Show(this);
    }

    private void OpenInstallFolder() { var path = LauncherPaths.ResolveConfigRelative(ConfigPath, _selectedProject.InstallPath ?? _config.InstallDir); Directory.CreateDirectory(path); try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch (Exception ex) { AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), true); } }
    private void ClearCache() { try { var p = SelectedStatePaths.StagingDir; if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); AppendLog("캐시를 정리했습니다.", true); } catch (Exception ex) { AppendLog("캐시 정리 실패: " + FriendlyError(ex), true); } }
    private void CleanupBackups() { try { var p = SelectedStatePaths.BackupDir; BackupManager.Prune(p, _config.MaxBackupCount, m => AppendLog("백업 정리: " + m, true)); AppendLog($"백업을 정리했습니다. 최근 {_config.MaxBackupCount}개는 롤백을 위해 보관합니다.", true); } catch (Exception ex) { AppendLog("백업 정리 실패: " + FriendlyError(ex), true); } }
    private void ClearLog() { if (_logBox is not null) _logBox.Text = string.Empty; }
    private string SaveLogFile() { var dir = Path.Combine(BaseDir, "logs"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log"); File.WriteAllText(path, _logBox?.Text ?? string.Empty); return path; }
    private void ExportLogsZip() { try { SaveLogFile(); var dir = Path.Combine(BaseDir, "logs"); var zip = Path.Combine(BaseDir, $"launcher-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip"); ZipFile.CreateFromDirectory(dir, zip); AppendLog("로그 ZIP 저장 완료: " + zip, true); } catch (Exception ex) { AppendLog("로그 ZIP 저장 실패: " + FriendlyError(ex), true); } }
    private string StorageSummary() => $"캐시 {FormatBytes(DirSize(SelectedStatePaths.StagingDir))} / 백업 {FormatBytes(DirSize(SelectedStatePaths.BackupDir))}";
    private static long DirSize(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { return 0; } }
    private static string FormatBytes(long b) { string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; var i = 0; while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; } return $"{v:0.##} {u[i]}"; }
    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);

    private TextBlock Txt(string text, double size, bool bold) => Label(text, size, Fg(), bold);
    private TextBlock Muted(string text, double size) => Label(text, size, MutedBrush());
    private TextBlock Label(string text, double size, IBrush color, bool bold = false) => new() { Text = text ?? string.Empty, FontSize = size, Foreground = color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private IBrush Fg() => LauncherVisualTokens.Text(IsDeveloper);
    private IBrush MutedBrush() => LauncherVisualTokens.MutedText(IsDeveloper);
    private IBrush StatusBrush(string? s) { var v = s ?? string.Empty; if (v.Contains("오류")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Danger); if (v.Contains("업데이트")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Warning); if (v.Contains("설치 필요")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); if (v.Contains("최신") || v.Contains("설치")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Success); return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); }
    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private Border Card(Control child, double padding) => new() { Padding = new Thickness(padding), CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusCard), Background = LauncherVisualTokens.Surface(IsDeveloper), BorderBrush = LauncherVisualTokens.Border(IsDeveloper), BorderThickness = new Thickness(1), Child = child };
    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Brushes.White);
        button.Classes.Add("accent");
        button.Background = LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderBrush = LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderThickness = new Thickness(1);
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Fg());
        button.Background = LauncherVisualTokens.Surface(IsDeveloper);
        button.BorderBrush = LauncherVisualTokens.Border(IsDeveloper);
        button.BorderThickness = new Thickness(1);
        return button;
    }
    private Button SmallButton(string text, EventHandler<RoutedEventArgs> handler) => SecondaryButton(text, handler, 34);
    private Button BaseButton(string text, EventHandler<RoutedEventArgs> handler, double height, IBrush color)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = color,
                FontSize = height >= 100 ? 22 : LauncherVisualTokens.FontBody,
                FontWeight = height >= 100 ? FontWeight.SemiBold : FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Height = height,
            MinWidth = 110,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = true,
            TabIndex = _nextTabIndex++,
            Transitions = new Transitions
            {
                new BrushTransition { Property = Button.BackgroundProperty, Duration = LauncherVisualTokens.MotionFast },
                new BrushTransition { Property = Button.BorderBrushProperty, Duration = LauncherVisualTokens.MotionFast }
            }
        };
        AutomationProperties.SetName(button, text.TrimStart('▶', '↻', ' '));
        button.Click += handler;
        return button;
    }
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Border Pill(string text, string bg, string fg) => new() { Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl), Background = B(bg), Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = B(fg), FontSize = 13, TextAlignment = TextAlignment.Center } };
    private static Control At(Control c, int col) { Grid.SetColumn(c, col); return c; }
    private static Control AtRow(Control c, int row) { Grid.SetRow(c, row); return c; }
    private static void Add(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }
    private const int MaxLogLines = 500;

    private void AppendLog(string msg, bool force = false)
    {
        _fileLogger?.Log("UI", msg);
        if (_logBox is null) return;
        var text = _logBox.Text ?? string.Empty;
        text += $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}";
        var lines = text.Split(Environment.NewLine);
        if (lines.Length > MaxLogLines) text = string.Join(Environment.NewLine, lines[^MaxLogLines..]);
        _logBox.Text = text;
        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
    }
}
