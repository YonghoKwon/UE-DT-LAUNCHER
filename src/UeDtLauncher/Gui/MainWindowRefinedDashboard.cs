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
    private string _agentState { get => _viewModel.AgentState; set => _viewModel.AgentState = value; }
    private bool _agentStatusRefreshing;
    private bool _startupInitialized;
    private Exception? _configurationError;
    private readonly ILauncherUiBackend _uiBackend;
    private readonly Func<string,Task<LauncherConfig>> _runtimeConfigLoader;
    private readonly Func<LauncherConfig,string,CancellationToken,Task<CatalogSnapshot>> _catalogLoader;
    private readonly Func<string,bool,DoctorTarget?,CancellationToken,Task<DoctorReport>> _doctor;
    private bool _windowMetricsInitialized;
    private readonly bool _allowAutomaticChecks;
    private EventHandler<Avalonia.Platform.PlatformColorValues>? _colorValuesChanged;
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
    private string ConfigPath => ResolvePath(_configPathBox?.Text ?? _configDraft ?? _startupOptions.ConfigPath);
    private bool IsDeveloper => _viewModel.IsDeveloper;
    private string CurrentPlatform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    private ProjectStatePaths SelectedStatePaths =>
        _selectedRuntimeConfig is { } runtime
            ? new ProjectStatePaths(Path.GetDirectoryName(runtime.InstallStatePath)!, runtime.StagingDir, runtime.BackupDir,
                runtime.InstalledManifestPath, runtime.InstallStatePath, runtime.AppPidPath, LauncherPaths.UpdateLockPath(runtime))
            : LauncherPaths.For(_config, ConfigPath, _selectedProject.ProjectId, CurrentPlatform);
    private LauncherConfig? _selectedRuntimeConfig;
    private bool UsesDistributionServer => !string.IsNullOrWhiteSpace(_config.DistributionServerUrl);

    public MainWindow() : this(LauncherStartupOptions.Discover(Array.Empty<string>()))
    {
    }

    public MainWindow(LauncherStartupOptions startupOptions) : this(startupOptions, null, null, true) { }

    internal MainWindow(LauncherStartupOptions startupOptions, LauncherDashboardViewModel? model, LauncherUiPreferences? preferences, bool startServices, ILauncherUiBackend? backend=null,
        Func<LauncherConfig,string,CancellationToken,Task<CatalogSnapshot>>? catalogLoader=null,
        Func<string,bool,DoctorTarget?,CancellationToken,Task<DoctorReport>>? doctor=null,
        Func<string,Task<LauncherConfig>>? runtimeConfigLoader=null)
    {
        _uiBackend=backend??new LauncherUiBackend();
        _runtimeConfigLoader=runtimeConfigLoader??(path=>LauncherPaths.LoadResolvedAsync(path));
        _catalogLoader=catalogLoader??CatalogSnapshotService.LoadAsync;
        _doctor=doctor??((path,online,target,token)=>LauncherDoctor.RunAsync(path,online,token,target:target));
        _startupOptions = startupOptions;
        _allowAutomaticChecks = startServices;
        if (model is not null) _viewModel = model;
        _preferences = preferences ?? (startServices ? LauncherUiPreferences.Load() : new());
        InitializeComponent();
        if (model is null) LoadConfig(); else SelectProject();
        if (startServices) { try { _fileLogger = new FileLogger(LauncherPaths.ResolveConfigRelative(ConfigPath, _config.LogDir)); } catch { _fileLogger = null; } }
        Build();
        if (startServices && PlatformSettings is not null)
        {
            _colorValuesChanged = (_, _) => Dispatcher.UIThread.Post(()=>{Build();RefreshOpenDialogs();RecordDisplayDiagnostic();});
            PlatformSettings.ColorValuesChanged += _colorValuesChanged;
        }
        _agentStatusTimer.Tick += async (_, _) => await RefreshAgentStatusAsync();
        if (startServices && _config.IsManagedDeployment && _configurationError is null) _agentStatusTimer.Start();
        Closed += (_, _) =>
        {
            _agentStatusTimer.Stop();
            if (_colorValuesChanged is not null && PlatformSettings is not null) PlatformSettings.ColorValuesChanged -= _colorValuesChanged;
            foreach (var bitmap in _projectVisualCache.Values) bitmap.Dispose();
            _projectVisualCache.Clear();
            _brandLogo?.Dispose();
        };
        if (startServices) Opened += async (_, _) => {RecordDisplayDiagnostic();await InitializeStartupAsync();};
        SizeChanged += (_, args) =>
        {
            UpdateActionDetailsLimit(); RecordDisplayDiagnostic();
            var nextBucket = GeneralLayoutBucket(args.NewSize.Width);
            if (_windowMetricsInitialized && nextBucket != _layoutBucket)
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
        if (!File.Exists(ConfigPath) || _configurationError is not null)
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
            _presentation.Title = "업데이트 서비스 연결이 필요합니다"; _presentation.Percent = null;
            _presentation.ErrorCode = "service-unavailable";
            _presentation.SupportId = Guid.NewGuid().ToString("N");
            Build();
            return;
        }
        if(await RefreshCatalog(false,suppressDialog:true) && HasProject)await RefreshInstallStatusAsync(suppressDialog:true);
    }

    private void LoadConfig()
    {
        _configurationError=null;
        _config = new LauncherConfig();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var parsed=JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath),JsonFiles.Options)
                    ?? throw new ArgumentException("Launcher configuration is empty.");
                if(parsed.Projects is null || parsed.Security is null || parsed.Performance is null ||
                    parsed.Projects.Any(p=>p is null || string.IsNullOrWhiteSpace(p.ProjectId) || p.VisibleToProfiles is null || p.VisibleToProfiles.Any(v=>v is null)))
                    throw new ArgumentException("Launcher display configuration is incomplete.");
                foreach(var project in parsed.Projects)if(string.IsNullOrWhiteSpace(project.DisplayName))project.DisplayName=project.ProjectId;
                _config=parsed;
            }
            catch (Exception ex)
            {
                _configurationError=ex;var error=LauncherUiError.From(new ArgumentException("Invalid launcher configuration.",ex));
                _installState="설정 확인 필요";_installDetail=error.Message;
                _viewModel.GeneralState=GeneralLauncherState.ConfigurationRequired;
                _presentation.Title="런처 설정을 확인해 주세요.";_presentation.ErrorCode="configuration-invalid";_presentation.SupportId=error.SupportId;
            }
        }
        else
        {
            _configurationError=new FileNotFoundException("Launcher display configuration is missing.");
            _presentation.Title="런처 설정을 확인해 주세요.";_presentation.ErrorCode="configuration-invalid";_presentation.SupportId=Guid.NewGuid().ToString("N");
            _installState = "설정 확인 필요";
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
        _agentState = _config.IsManagedDeployment ? LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Checking) : "로컬 모드";

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
            ?? new ProjectUiConfig { ProjectId = "", DisplayName = "사용 가능한 프로젝트가 없습니다" };
        _config.ProjectId = _selectedProject.ProjectId;
        UpdateReleaseNotes();
    }

    private IEnumerable<ProjectUiConfig> VisibleProjects()
    {
        return _viewModel.VisibleProjects();
    }

    private void Build() => BuildEnterprise();

    private double CurrentLayoutWidth() => ClientSize.Width > 0 ? ClientSize.Width : Width;
    private int VisibleProjectCount() => _viewModel.ProjectsForProfile().Take(2).Count();
    private LauncherLayoutPolicy CurrentLayout() => LauncherLayoutPolicy.For(CurrentLayoutWidth(), VisibleProjectCount(), IsDeveloper);
    private static int GeneralLayoutBucket(double width) => width < 800 ? 0 : width < 1100 ? 1 : 2;

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
        if(!_config.IsManagedDeployment){_agentState="로컬 모드";if(_serviceLabel is not null)_serviceLabel.Text=_agentState;return true;}
        // The v1 agent handles one operation at a time; a separate probe must not label active work disconnected.
        if (_running) return _agentState.StartsWith("연결", StringComparison.Ordinal) || _agentState.EndsWith("정상", StringComparison.Ordinal);
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
        await Dispatcher.UIThread.InvokeAsync(() => { if (_serviceLabel is not null) _serviceLabel.Text = nextState; });
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
        var combo = new ComboBox { ItemsSource = list, SelectedItem = list.FirstOrDefault(v => string.Equals(v, selected, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault(), FontSize = 14 * _preferences.TextScale, MinHeight = 32, Background = SurfaceBrush, Foreground = Fg() };
        Identify(combo, "filter-" + label, label); _selectionControls.Add(combo); combo.IsEnabled = !_running;
        combo.SelectionChanged += (_, _) => { if (!_building && !_running && combo.SelectedItem is string v && !string.Equals(v, selected, StringComparison.OrdinalIgnoreCase)) { apply(v); Build(); } };
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
        var box = new TextBox { Text = _config.RequestedVersion ?? string.Empty, Watermark = "예: 1.0.3", FontSize = 14 * _preferences.TextScale, MinHeight = 32, Background = SurfaceBrush, Foreground = Fg() };
        Identify(box, "filter-exact-version", "요청 버전"); _selectionControls.Add(box);
        box.TextChanged += (_, _) => { var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim(); if (_building || _running || value == _config.RequestedVersion) return; _config.RequestedVersion = value; SelectionChanged(); };
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
        _presentation.Retry = null;
        _selectedRuntimeConfig = null;
        _viewModel.ProjectStatus = null;
        _viewModel.GeneralState = GeneralLauncherState.Checking;
        _config.TargetPlatform = CurrentPlatform;
        _installState = "확인 필요";
        _installDetail = "배포 선택이 변경되었습니다. 상태 확인을 다시 실행하세요.";
        UpdateReleaseNotes();
        if (UsesDistributionServer && _allowAutomaticChecks)
            Dispatcher.UIThread.Post(async () => await RefreshInstallStatusAsync(suppressDialog: true));
    }

    private void RenderProjects()
    {
        RenderEnterpriseProjects();
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
        card.PointerPressed += (_, _) => { if (_running) return; _selectedProject = p; _config.ProjectId = p.ProjectId; SelectionChanged(); Build(); };
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
            FontSize = 14 * _preferences.TextScale,
            TabIndex = _nextTabIndex++
        };
        Identify(combo, "compact-project", "프로젝트 선택"); _selectionControls.Add(combo);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || _running || combo.SelectedIndex < 0 || combo.SelectedIndex >= projects.Count) return;
            if (_selectedProject.ProjectId == projects[combo.SelectedIndex].ProjectId) return;
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
        return new StackPanel { Spacing = 12, Children = { Hero(CurrentLayout().HeroHeight), DeveloperActions(), lower } };
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
            LauncherVisualTokens.Accent,
            LauncherVisualTokens.PoscoLightBlue,
            LauncherVisualTokens.BrandNavyDeep
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
            FontSize = hero ? 32 : 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(hero ? (byte)55 : (byte)220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
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
                _selectedRuntimeConfig?.InstallDir ?? _selectedProject.InstallPath ?? _config.InstallDir),
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
        if (_config.IsManagedDeployment || UsesDistributionServer) return _viewModel.ProjectStatus?.InstalledVersion ?? (!IsDeveloper ? _viewModel.ProjectStatus?.PreviousInstallation?.Release.Version : null);
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
        return CatalogRecommendation.Find(_catalog.SelectionPolicy, releases, r => r.IsLatest)?.Version;
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
        run.Tag = "general-primary-action";
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
        var run = Track(CommandButton("실행", LauncherIconKind.Play, async (_, _) => await RunAsync(false, true), primary: true));
        run.HotKey = new KeyGesture(Key.F5);
        var status = Track(CommandButton("상태 확인", LauncherIconKind.Shield, async (_, _) => await RefreshInstallStatusAsync()));
        status.HotKey = new KeyGesture(Key.F6);
        var groups = new Grid { ColumnDefinitions = new ColumnDefinitions("1.05*,1.25*,1.35*"), ColumnSpacing = 10 };
        groups.Children.Add(DeveloperCommandGroup(
            "배포",
            LauncherIconKind.Package,
            run,
            Track(CommandButton("업데이트", LauncherIconKind.Download, async (_, _) => await RunAsync(false, false))),
            status));
        groups.Children.Add(At(DeveloperCommandGroup(
            "유지보수",
            LauncherIconKind.Wrench,
            Track(CommandButton("검증/복구", LauncherIconKind.Wrench, async (_, _) => await RunAsync(true, false))),
            Track(CommandButton("롤백", LauncherIconKind.Refresh, async (_, _) => await RollbackLatestAsync())),
            Track(CommandButton("캐시 정리", LauncherIconKind.Package, (_, _) => ClearCache())),
            Track(CommandButton("백업 정리", LauncherIconKind.Package, (_, _) => CleanupBackups()))), 1));
        groups.Children.Add(At(DeveloperCommandGroup(
            "진단",
            LauncherIconKind.Log,
            CommandButton("설치 폴더", LauncherIconKind.Folder, (_, _) => OpenInstallFolder()),
            CommandButton("로그 ZIP", LauncherIconKind.Log, (_, _) => ExportLogsZip()),
            CommandButton("로그 지우기", LauncherIconKind.Log, (_, _) => ClearLog()),
            Track(CommandButton("다시 시도", LauncherIconKind.Refresh, async (_, _) => await RetryCurrentAsync())),
            CommandButton("설정", LauncherIconKind.Settings, (_, _) => DeveloperSettings())), 2));
        return groups;
    }

    private Control DeveloperCommandGroup(string title, LauncherIconKind iconKind, params Button[] buttons)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                LauncherIconFactory.Create(iconKind, 16, LauncherVisualTokens.MutedText(dark: true)),
                Label(title, LauncherVisualTokens.FontBody, Fg(), true)
            }
        };
        var columns = Math.Min(buttons.Length, 3);
        var rows = (int)Math.Ceiling(buttons.Length / (double)columns);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", columns))),
            RowDefinitions = new RowDefinitions(string.Join(',', Enumerable.Repeat("Auto", rows))),
            ColumnSpacing = 7,
            RowSpacing = 7
        };
        for (var index = 0; index < buttons.Length; index++)
        {
            Grid.SetColumn(buttons[index], index % columns);
            Grid.SetRow(buttons[index], index / columns);
            grid.Children.Add(buttons[index]);
        }
        return Card(new StackPanel { Spacing = 9, Children = { header, grid } }, 12);
    }

    private Button CommandButton(
        string text,
        LauncherIconKind iconKind,
        EventHandler<RoutedEventArgs> handler,
        bool primary = false)
    {
        var button = primary ? PrimaryButton(text, handler, 44) : SecondaryButton(text, handler, 44);
        var foreground = primary ? Brushes.White : Fg();
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                LauncherIconFactory.Create(iconKind, 15, foreground),
                Label(text, 12, foreground, true)
            }
        };
        AutomationProperties.SetName(button, text);
        return button;
    }

    private Control ReleaseInfo()
    {
        return Card(new StackPanel { Spacing = 7, Children = { Txt("선택된 배포 정보", LauncherVisualTokens.FontStatus, true), DeveloperKeyValue("프로젝트", _selectedProject.ProjectId), DeveloperKeyValue("프로필", _config.ClientProfile), DeveloperKeyValue("가동/개발", _config.Environment), DeveloperKeyValue("채널", _config.Channel), DeveloperKeyValue("버전 정책", _config.VersionPolicy == "exact" ? $"exact / {_config.RequestedVersion ?? "미입력"}" : "latest"), DeveloperKeyValue("설치 버전", ReadInstalledVersion() ?? "미설치"), DeveloperKeyValue("최신 버전", LatestCatalogVersion() ?? "카탈로그 확인 필요"), DeveloperKeyValue("OS", CurrentPlatform), DeveloperKeyValue("카탈로그", _catalogState), DeveloperKeyValue("릴리스 노트", _releaseNotes), DeveloperKeyValue("캐시/백업", StorageSummary()) } }, 16);
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
        _logBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = developerLog ? 250 : 72, Text = IsDeveloper ? $"config: {ConfigPath}{Environment.NewLine}profile: {_config.ClientProfile}{Environment.NewLine}platform: {CurrentPlatform}" : "업데이트 상태가 여기에 표시됩니다.", Background = LauncherVisualTokens.Brush(IsDeveloper ? LauncherVisualTokens.BrandNavyDeep : LauncherVisualTokens.LightSurface), Foreground = Fg(), FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12 };
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
        if(_running)return;
        if(_configurationError is not null)
        {
            _agentStatusTimer.Stop();_selectedRuntimeConfig=null;_viewModel.ProjectStatus=null;_catalog=new();
            _presentation.ErrorCode=null;_presentation.SupportId=null;_presentation.Retry=null;
            LoadConfig();Build();
            if(_configurationError is not null)return;
            if(_allowAutomaticChecks && _config.IsManagedDeployment)_agentStatusTimer.Start();
            _startupInitialized=false;await InitializeStartupAsync();return;
        }
        switch (_viewModel.PrimaryAction)
        {
            case PrimaryActionKind.InstallAndLaunch:
            case PrimaryActionKind.UpdateAndLaunch:
            case PrimaryActionKind.Launch:
                await RunAsync(false, true);
                break;
            case PrimaryActionKind.RetryCheck:
                await RefreshAgentStatusAsync();
                if(await RefreshCatalog(false,suppressDialog:true) && HasProject)await RefreshInstallStatusAsync();
                break;
        }
    }

    private async Task<bool> RefreshCatalog(bool rebuild, bool suppressDialog = false)
    {
        var ownsBusy = !_running;
        if (ownsBusy) { BeginOperation(LauncherUiOperation.Catalog); _running=true; SetBusy(true); }
        try
        {
            _catalogState = "카탈로그 확인 중...";
            if (rebuild) Build();
            var catalogConfig = await RunConfig(false, false);
            _catalog = await _catalogLoader(catalogConfig, CurrentPlatform, CancellationToken.None);
            _presentation.ErrorCode=null;_presentation.SupportId=null;
            _catalogState = _catalog.Status;
            MergeCatalogProjects();
            UpdateReleaseNotes();
            return true;
        }
        catch (Exception ex)
        {
            _catalogState = "카탈로그 오류";
            MarkError(ex,"카탈로그 확인 실패",showDialog: !suppressDialog);
            return false;
        }
        finally { if (ownsBusy) { _running=false; SetBusy(false); } if (rebuild) Build(); }
    }

    private void MergeCatalogProjects()
    {
        if (UsesDistributionServer)
            _config.Projects.RemoveAll(p => !_catalog.Projects.Any(c => c.ProjectId.Equals(p.ProjectId, StringComparison.OrdinalIgnoreCase)));
        foreach (var cp in _catalog.Projects)
        {
            var configured = _config.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, cp.ProjectId, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
            {
                if (UsesDistributionServer) { configured.DisplayName = cp.DisplayName; configured.HeroPath = cp.HeroPath; configured.ThumbnailPath = cp.ThumbnailPath; }
                continue;
            }
            _config.Projects.Add(new ProjectUiConfig { ProjectId = cp.ProjectId, DisplayName = cp.DisplayName, Description = "카탈로그에서 발견된 프로젝트입니다.", Status = $"릴리스 {cp.ReleaseCount}개", InstallPath = $"apps/{cp.ProjectId}", Technology = CurrentPlatform, SortOrder = 100, VisibleToProfiles = new List<string> { _config.ClientProfile } });
            _config.Projects[^1].HeroPath = cp.HeroPath; _config.Projects[^1].ThumbnailPath = cp.ThumbnailPath;
        }
        if (_config.Projects.Count > 0) SelectProject();
        else
        {
            _selectedProject = new ProjectUiConfig { ProjectId = "unavailable", DisplayName = "사용 가능한 프로젝트가 없습니다" };
            _viewModel.GeneralState = GeneralLauncherState.RecoverableError;
            _presentation.Title = "허용된 배포가 없습니다"; _presentation.Percent = null;
            _installState = "허용된 배포 없음";
            _installDetail = "이 PC에 허용된 배포가 없습니다. 관리자에게 문의해 주세요.";
        }
    }

    private IEnumerable<CatalogReleaseOption> MatchingReleases() => _catalog.Releases.Where(r => r.ProjectId.Equals(_selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase) && r.Platform.Equals(CurrentPlatform, StringComparison.OrdinalIgnoreCase) && r.Environment.Equals(_config.Environment, StringComparison.OrdinalIgnoreCase) && r.Channel.Equals(_config.Channel, StringComparison.OrdinalIgnoreCase));

    private void UpdateReleaseNotes()
    {
        var release = SelectedCatalogRelease();
        _releaseNotes = string.IsNullOrWhiteSpace(release?.Notes) ? _selectedProject.Description ?? "릴리스 노트가 없습니다." : release.Notes!;
    }

    private async Task<LauncherConfig> RunConfig(bool repair, bool launch)
    {
        var runtimePath = UsesDistributionServer && _config.IsManagedDeployment
            ? Path.Combine(ManagedLauncherPathLayout.Current().ConfigRoot, "launcher.config.json") : ConfigPath;
        var c = await _runtimeConfigLoader(runtimePath);
        c.ProjectId = _selectedProject.ProjectId; c.Environment = _config.Environment; c.Channel = _config.Channel; c.TargetPlatform = CurrentPlatform; c.VersionPolicy = _config.VersionPolicy; c.RequestedVersion = _config.RequestedVersion; c.RepairMode = repair; c.LaunchAfterUpdate = launch;
        c.ClientProfile = _viewModel.EffectiveProfile;
        c.SelfUpdate = null;
        LauncherPaths.ResolveInPlace(c, runtimePath, UsesDistributionServer ? null : _selectedProject.InstallPath);
        return c;
    }

    private ReleaseSelection? CurrentReleaseSelection()
    {
        if (!UsesDistributionServer) return null;
        var release = SelectedCatalogRelease();
        if (release is null && _config.VersionPolicy != "exact" && _catalog.SelectionPolicy == CatalogRecommendation.ExplicitPolicy) throw new NoPromotedReleaseException();
        if (release is null) throw new InvalidOperationException("허용된 배포 버전을 먼저 선택해 주세요.");
        return new(release.ProjectId, release.Environment, release.Channel, release.Platform, release.Version);
    }

    private CatalogReleaseOption? SelectedCatalogRelease() => _config.VersionPolicy == "exact"
        ? MatchingReleases().FirstOrDefault(r => r.Version.Equals(_config.RequestedVersion, StringComparison.OrdinalIgnoreCase))
        : CatalogRecommendation.Find(_catalog.SelectionPolicy, MatchingReleases(), r => r.IsLatest);

    private void AwaitPromotion()
    {
        _selectedRuntimeConfig = null; _viewModel.ProjectStatus = null;
        _viewModel.GeneralState = GeneralLauncherState.AwaitingPromotion;
        _installState = "실행 버전 지정 대기"; _installDetail = "관리자가 실행 버전을 지정하지 않았습니다.";
        _presentation.ErrorCode = null; _presentation.SupportId = null; _presentation.Retry = null;
        _presentation.Complete(_installDetail); Build();
    }

    private void ApplyManagedSelection(LauncherConfig config, ManagedAgentResponse response, ReleaseSelection? requested)
    {
        if (!UsesDistributionServer) return;
        if (response.SelectedRelease is null || (requested is not null && (response.SelectedRelease != requested || CurrentReleaseSelection() != requested)))
            throw new InvalidDataException("업데이트 서비스가 선택한 버전을 확인하지 못했습니다. 서비스 업데이트가 필요합니다.");
        VersionedReleasePaths.Bind(config, response.SelectedRelease);
        _selectedRuntimeConfig = config;
    }

    private async Task RunAsync(bool repair, bool launch, ReleaseSelection? expectedSelection = null)
    {
        if(_running)return;
        _running=true;SetBusy(true);
        using var operationCancellation = new CancellationTokenSource();
        try
        {
            var context=CaptureUiOperation(expectedSelection);
            if(IsDeveloper && launch && !await ConfirmDevLaunch(context))return;
            BeginOperation(launch?LauncherUiOperation.Launch:repair?LauncherUiOperation.Repair:LauncherUiOperation.Update);
            Progress(0);ResetSpeedTracking();StartCancellableUiOperation(operationCancellation);
            var config=await RunConfig(repair,launch);
            var result=await _uiBackend.ExecuteAsync(context,config,repair,launch,CreateUiProgress(),_fileLogger,operationCancellation.Token);
            _resumeOperation=null;
            if(result.Completion==LauncherUiCompletion.CommittedRefreshRequired){CommittedUiRefreshRequired();return;}
            _presentation.Complete(result.Completion==LauncherUiCompletion.CommittedLaunchSkipped?"설치 완료 · 프로그램 실행은 생략했습니다.":launch?"실행 준비 완료":repair?"파일 복구 완료":"업데이트 확인 완료");
            if(ApplyUiResult(context,result))Build();
        }
        catch(LauncherUiCancelledException cancelled){RecordCancelledOperation(cancelled.Operation);}
        catch(OperationCanceledException){RecordCancelledOperation(null);}
        catch(Exception ex){MarkError(ex);}
        finally{FinishUiOperation();}
    }

    private async Task RefreshInstallStatusAsync(bool suppressDialog = false)
    {
        if(_running)return;
        if(!HasProject){await RefreshCatalog(true,suppressDialog:true);return;}
        if (_catalog.SelectionPolicy == CatalogRecommendation.ExplicitPolicy && _config.VersionPolicy != "exact" && SelectedCatalogRelease() is null)
        { AwaitPromotion(); return; }
        _running=true;SetBusy(true);
        try
        {
            var context=CaptureUiOperation();
            BeginOperation(LauncherUiOperation.Check);_viewModel.GeneralState=GeneralLauncherState.Checking;Progress(0);
            var result=await _uiBackend.CheckAsync(context,await RunConfig(false,false),CreateUiProgress());
            if(ApplyUiResult(context,result)){_presentation.Complete(_installState);Build();}
        }
        catch(Exception ex){MarkError(ex,"상태 확인 실패",showDialog:!suppressDialog);}
        finally{FinishUiOperation();}
    }

    private async Task TroubleshootAsync()
    {
        if(_running)return;
        _running=true;SetBusy(true);
        LauncherUiOperationContext? context=null;
        LauncherConfig? config=null;
        LauncherUiOperationResult? checkedResult=null;
        var repairAttempted=false;
        using var cancellation=new CancellationTokenSource();
        try
        {
            if(UsesDistributionServer && _catalog.Releases.Count==0)
            {
                if(!await RefreshCatalog(false,suppressDialog:true) || !HasProject)return;
            }
            context=CaptureUiOperation();config=await RunConfig(false,false);
            BeginOperation(LauncherUiOperation.Troubleshoot);Progress(0);StartCancellableUiOperation(cancellation);
            checkedResult=await _uiBackend.CheckAsync(context,config,CreateUiProgress(),cancellation.Token);
            ValidateUiResult(context,checkedResult);_viewModel.RequireRuntimeQuiescent(checkedResult.Runtime);
            var action=LauncherUiOperations.TroubleshootAction(checkedResult.Status);
            if(action==LauncherTroubleshootAction.Repair)
            {
                repairAttempted=true;
                var repaired=await _uiBackend.ExecuteAsync(context,config,true,false,CreateUiProgress(),_fileLogger,cancellation.Token);
                if(repaired.Completion==LauncherUiCompletion.CommittedRefreshRequired || cancellation.IsCancellationRequested){CommittedUiRefreshRequired();return;}
                ValidateUiResult(context,repaired);_viewModel.RequireRuntimeQuiescent(repaired.Runtime);
                checkedResult=await _uiBackend.CheckAsync(context,config,CreateUiProgress());
                if(checkedResult.Status.UpdateRequired)throw new InvalidDataException("파일 복구 후 검증을 완료하지 못했습니다.");
            }
            if(ApplyUiResult(context,checkedResult))
            {
                _presentation.Complete(action==LauncherTroubleshootAction.OfferInstall?"확인 완료 · 설치/업데이트가 필요합니다.":repairAttempted?"파일 복구 완료":"설치 상태 점검 완료");
                Build();
            }
        }
        catch(Exception ex)
        {
            var offer=LauncherDashboardViewModel.CanOfferRecoveryRollback(repairAttempted,checkedResult?.Status.HasBackup==true,ex);
            if(ex is LauncherUiCancelledException cancelled){RecordCancelledOperation(cancelled.Operation);return;}
            if(ex is OperationCanceledException){RecordCancelledOperation(null);return;}
            if(offer && context is not null && config is not null)
            {
                try
                {
                    var preview=context.Managed?await PreviewManagedRollbackAsync(config):await Task.Run(()=>RollbackPreviewService.ReadAsync(config));
                    if(preview is {CanRestore:true} && await ConfirmPreviewAsync(preview))await RestoreUiPreviewAsync(context,config,preview);
                    else MarkError(ex,"문제 해결 실패");
                }
                catch(Exception rollbackError){MarkError(rollbackError,"문제 해결 실패");}
            }
            else if(ex is OperationCanceledException)_presentation.Complete("작업 취소 완료 · 설치 상태를 다시 확인해 주세요.");
            else MarkError(ex,"문제 해결 실패");
        }
        finally{FinishUiOperation();}
    }

    private async Task RollbackLatestAsync()
    {
        if (_running) return;
        _running=true;SetBusy(true);
        try
        {
            var context=CaptureUiOperation();
            var config=await RunConfig(false,false);context.Pin(config);
            if(context.Selection is not null)VersionedReleasePaths.Bind(config,context.Selection);
            var preview=config.IsManagedDeployment
                ? await PreviewManagedRollbackAsync(config)
                : await Task.Run(()=>RollbackPreviewService.ReadAsync(config));
            if(preview is null || !preview.CanRestore)
            {SetStatus("복원할 수 있는 백업 정보를 확인해 주세요.");return;}
            if(!await ConfirmPreviewAsync(preview))return;
            BeginOperation(LauncherUiOperation.Rollback);
            await RestoreUiPreviewAsync(context,config,preview);
        }
        catch(Exception ex){MarkError(ex,"백업 복원 실패");}
        finally{FinishUiOperation();}
    }

    private Task<bool> ConfirmRollback(string backupName, BackupInfo? info)
    {
        return ShowConfirmationAsync("백업 복원 확인", new StackPanel {Spacing=12,Children={
            Txt("선택한 설치의 백업 시점으로 복원합니다.",16,true),
            KeyValue("백업",backupName),KeyValue("현재 버전",info?.NewVersion??"기록 없음"),
            KeyValue("복원 상태",info?.PreviousVersion??"기록 없음"),
            Muted("다른 버전의 설치 경로로 전환하는 작업이 아닙니다. 설치 전 상태 백업은 선택 설치 파일을 제거할 수 있습니다.",14)}}, "백업 복원");
    }
    private Task<bool> ConfirmDevLaunch(LauncherUiOperationContext context)
    {
        return ShowConfirmationAsync("개발자 배포 실행 확인",new StackPanel {Spacing=12,Children={
            KeyValue("프로젝트",context.ProjectId),KeyValue("환경",context.Environment),KeyValue("채널",context.Channel),
            KeyValue("버전",context.Selection?.Version ?? context.RequestedVersion ?? "직접 Manifest"),KeyValue("OS",context.Platform)}}, "선택 버전 실행");
    }

    private void EngineProgress(LauncherProgress p)
    {
        if (p.Stage == "DownloadProgress" && p.TotalBytes is > 0 && p.BytesDownloaded is not null)
        {
            // Logical file progress includes reused/resumed bytes; network speed does not.
            UpdateSpeed(p.Performance?.NetworkBytes ?? p.BytesDownloaded.Value);
            var overall = Math.Clamp(p.BytesDownloaded.Value / (double)p.TotalBytes.Value * 100, 0, 100);
            var speedText = _speedBytesPerSecond > 0 ? $"{FormatBytes((long)_speedBytesPerSecond)}/s" : "측정 중";
            if (_statusText is not null) SetStatus($"다운로드 중 · 파일 {p.FileIndex}/{p.FileCount} · {speedText} · 전체 {overall:0}%");
            Progress(overall, measured: true);
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
        foreach (var button in _actionButtons)
        {
            var id = AutomationProperties.GetAutomationId(button);
            var mutation = id is "primary-action" or "update" or "repair" or "rollback" or "resume-operation" or "cache-clear" or "backup-cleanup";
            button.IsEnabled = !busy && (!mutation || (HasProject && _viewModel.GeneralState != GeneralLauncherState.RuntimeBlocked)) &&
                (!Equals(button.Tag, "general-primary-action") || (HasProject && (IsDeveloper || _viewModel.PrimaryAction != PrimaryActionKind.Disabled)));
            if (_config.IsManagedDeployment && id is "cache-clear" or "backup-cleanup")
            {
                button.IsEnabled = false;
                ToolTip.SetTip(button, "관리형 캐시/백업 정리는 업데이트 서비스의 별도 관리 기능이 필요합니다.");
            }
        }
        foreach (var control in _selectionControls) control.IsEnabled = !busy;
        RefreshPresentation();
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        _viewModel.ApplyProgressStage(stage); _presentation.Stage(stage);
        if (_statusText is not null) SetStatus(IsDeveloper ? $"{stage}: {message}" : FriendlyProgress(stage, message));
        if (percent.HasValue) Progress(percent.Value); else Progress(stage switch { "Catalog" => 10, "Manifest" => 20, "Plan" => 35, "Download" => Math.Max(_progress?.Value ?? 0, 45), "Apply" => 85, "Package" => 88, "Launch" => 95, _ => Math.Max(_progress?.Value ?? 0, 5) });
        AppendLog(IsDeveloper ? $"[{stage}] {message}" : FriendlyProgress(stage, message));
    }

    private void ReportManagedProgress(ManagedAgentProgress progress)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            EngineProgress(progress.ToLauncherProgress());
            return;
        }

        Dispatcher.UIThread.InvokeAsync(() => EngineProgress(progress.ToLauncherProgress()))
            .GetAwaiter()
            .GetResult();
    }

    private void Progress(double v, bool measured = false) { _presentation.Percent = v >= 100 || measured ? Math.Clamp(v, 0, 100) : null; RefreshPresentation(); }
    private string FriendlyProgress(string stage, string message) => LauncherOperationPresentation.GeneralProgress(stage);
    private void MarkError(Exception ex, string status = "작업 실패", bool showDialog = true)
    {
        var uiError = LauncherUiError.From(ex);
        if (uiError.Code == "no-promoted-release") { AwaitPromotion(); return; }
        _presentation.Title=status;
        _presentation.ErrorCode=uiError.Code; _presentation.SupportId=uiError.SupportId;
        var runtimeBlocked = ex.GetBaseException() as RuntimeBlockedException;
        _installState = runtimeBlocked is null ? "오류" : runtimeBlocked.Observation.State == RuntimeState.Running ? "실행 중" : "실행 상태 확인 필요";
        _installDetail = FriendlyError(ex);
        _viewModel.GeneralState = runtimeBlocked is null ? GeneralLauncherState.RecoverableError : GeneralLauncherState.RuntimeBlocked;
        _viewModel.WorkflowStage = LauncherWorkflowStage.None;
        Build();
        if (_statusText is not null) SetStatus(status);
        AppendLog($"[{uiError.SupportId}] {uiError.Code}: {FriendlyError(ex)}", true);
        if (IsDeveloper) AppendLog(ex.ToString(), true);
        else if (showDialog && runtimeBlocked is null) ErrorDialog(status, FriendlyError(ex)+"\n지원 ID: "+uiError.SupportId);
    }
    private string FriendlyError(Exception ex) => _viewModel.FriendlyError(ex);
    private void UpdateInstallTile() { if (_installStateText is not null) { _installStateText.Text = _installState; _installStateText.Foreground = StatusBrush(_installState); } if (_installDetailText is not null) _installDetailText.Text = _installDetail; }

    private void ErrorDialog(string title,string message) => ShowEnterpriseError(title,message);
    private void GeneralSettings() => SettingsDialog(false);
    private void DeveloperSettings() => SettingsDialog(true);
    private void SettingsDialog(bool dev) => ShowEnterpriseSettings(dev);

    private void OpenInstallFolder() { var path = _selectedRuntimeConfig?.InstallDir ?? LauncherPaths.ResolveConfigRelative(ConfigPath, _selectedProject.InstallPath ?? _config.InstallDir); Directory.CreateDirectory(path); try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch (Exception ex) { AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), true); } }
    private void ClearCache() { try { var p = SelectedStatePaths.StagingDir; if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); AppendLog("캐시를 정리했습니다.", true); } catch (Exception ex) { AppendLog("캐시 정리 실패: " + FriendlyError(ex), true); } }
    private void CleanupBackups() { try { var p = SelectedStatePaths.BackupDir; BackupManager.Prune(p, _config.MaxBackupCount, m => AppendLog("백업 정리: " + m, true)); AppendLog($"백업을 정리했습니다. 최근 {_config.MaxBackupCount}개는 롤백을 위해 보관합니다.", true); } catch (Exception ex) { AppendLog("백업 정리 실패: " + FriendlyError(ex), true); } }
    private void ClearLog() { _presentation.Logs.Clear(); if (_logBox is not null) _logBox.Text = string.Empty; }
    private string SaveLogFile() { var dir = Path.Combine(Path.GetDirectoryName(LauncherUiPreferences.DefaultPath)!, "support", "logs"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log"); File.WriteAllText(path, DiagnosticRedactor.Redact(_presentation.LogText)); return path; }
    private void ExportLogsZip()
    {
        try
        {
            SaveLogFile();
            var support=Path.Combine(Path.GetDirectoryName(LauncherUiPreferences.DefaultPath)!,"support");
            var zip=Path.Combine(support,"launcher-logs-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".zip");
            ZipFile.CreateFromDirectory(Path.Combine(support,"logs"),zip);
            if (_lastDoctorReport is not null)
            {
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
                using var writer = new StreamWriter(archive.CreateEntry("doctor.json").Open());
                writer.Write(DiagnosticRedactor.Redact(JsonSerializer.Serialize(_lastDoctorReport, JsonFiles.Options)));
            }
            SetStatus("지원 로그 ZIP을 사용자 지원 폴더에 저장했습니다.");
            AppendLog(IsDeveloper?"지원 로그 저장: "+zip:"지원 로그 ZIP 저장 완료",true);
        }
        catch(Exception ex){MarkError(ex,"지원 로그 저장 실패");}
    }

    private string StorageSummary() => $"캐시 {FormatBytes(DirSize(SelectedStatePaths.StagingDir))} / 백업 {FormatBytes(DirSize(SelectedStatePaths.BackupDir))}";
    private static long DirSize(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { return 0; } }
    private static string FormatBytes(long b) { string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; var i = 0; while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; } return $"{v:0.##} {u[i]}"; }
    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);

    private TextBlock Txt(string text, double size, bool bold) => Label(text, size, Fg(), bold);
    private TextBlock Muted(string text, double size) => Label(text, size, MutedBrush());
    private TextBlock Label(string text, double size, IBrush color, bool bold = false) => new() { Text = text ?? string.Empty, FontSize = size * _preferences.TextScale, Foreground = HighContrast ? Brushes.White : color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private IBrush Fg() => HighContrast ? Brushes.White : LauncherVisualTokens.Text(IsDeveloper);
    private IBrush MutedBrush() => HighContrast ? Brushes.White : LauncherVisualTokens.MutedText(IsDeveloper);
    private IBrush StatusBrush(string? s) { var v = s ?? string.Empty; if (v.Contains("오류")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Danger); if (v.Contains("업데이트")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Warning); if (v.Contains("설치 필요")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); if (v.Contains("최신") || v.Contains("설치")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Success); return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); }
    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private Border Card(Control child, double padding) => new() { Padding = new Thickness(padding), CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusCard), Background = LauncherVisualTokens.Surface(IsDeveloper), BorderBrush = LauncherVisualTokens.Border(IsDeveloper), BorderThickness = new Thickness(1), Child = child };
    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Brushes.White);
        button.Classes.Add("posco-primary");
        button.Background = HighContrast ? Brushes.Black : LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderBrush = HighContrast ? Brushes.White : LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderThickness = new Thickness(1);
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Fg());
        button.Classes.Add("posco-secondary");
        button.Background = SurfaceBrush;
        button.BorderBrush = HighContrast ? Brushes.White : LauncherVisualTokens.Border(IsDeveloper);
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
                FontSize = (height >= 100 ? 22 : LauncherVisualTokens.FontBody) * _preferences.TextScale,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = height >= 100 ? FontWeight.SemiBold : FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            MinHeight = height,
            MinWidth = 100,
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
        AutomationProperties.SetName(button, text == "↻" ? "카탈로그 새로고침" : text.TrimStart('▶', ' '));
        button.GotFocus += (_, e) =>
        {
            if(e.NavigationMethod==NavigationMethod.Pointer)return;
            button.BorderBrush=HighContrast?Brushes.Yellow:LauncherVisualTokens.Brush(IsDeveloper?LauncherVisualTokens.PoscoLightBlue:LauncherVisualTokens.Accent);
            button.BorderThickness=new Thickness(3);
        };
        button.LostFocus += (_,_)=>{button.BorderBrush=HighContrast?Brushes.White:LauncherVisualTokens.Border(IsDeveloper);button.BorderThickness=new Thickness(1);};
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
        var safe=DiagnosticRedactor.Redact(msg);
        _fileLogger?.Log("UI", safe);
        _presentation.Append($"{DateTime.Now:HH:mm:ss} {safe}");
        if (_logBox is null) return;
        var caret=_logBox.CaretIndex; var start=_logBox.SelectionStart;var stop=_logBox.SelectionEnd;
        var reading=_logBox.IsKeyboardFocusWithin || start!=stop;
        _logBox.Text=_presentation.LogText;
        if(reading){_logBox.CaretIndex=Math.Min(caret,_logBox.Text.Length);_logBox.SelectionStart=Math.Min(start,_logBox.Text.Length);_logBox.SelectionEnd=Math.Min(stop,_logBox.Text.Length);}
        else _logBox.CaretIndex=_logBox.Text.Length;

    }
}
