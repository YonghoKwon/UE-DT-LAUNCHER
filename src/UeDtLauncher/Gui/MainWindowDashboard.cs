using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private bool _isRunning;
    private bool _lastRunRepair;
    private bool _lastRunLaunch = true;
    private string _projectSearch = string.Empty;
    private string _installState = "확인 필요";
    private string _installStateDetail = "상태 확인을 눌러 설치 상태를 확인하세요.";

    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();

    private TextBox _configPathBox = null!;
    private TextBox _logBox = null!;
    private TextBlock _statusText = null!;
    private TextBlock _installStateText = null!;
    private TextBlock _installStateCaption = null!;
    private ProgressBar _progress = null!;
    private Button _retryButton = null!;
    private StackPanel _projectListPanel = null!;

    private bool IsDeveloper => string.Equals(_config.ClientProfile, "developer", StringComparison.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        LoadConfigForUi();
        BuildDashboard();
    }

    private void LoadConfigForUi()
    {
        var path = GetConfigPathSafe();
        if (File.Exists(path))
        {
            try
            {
                _config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), JsonFiles.Options) ?? new LauncherConfig();
            }
            catch
            {
                _config = new LauncherConfig();
            }
        }

        if (_config.Projects.Count == 0)
        {
            _config.Projects.Add(new ProjectUiConfig
            {
                ProjectId = _config.ProjectId ?? "ue-dt-simulator",
                DisplayName = "UE-DT 프로젝트",
                Description = IsDeveloper ? "개발/테스트용 Unreal 패키지" : "운영 안정화 Unreal 패키지",
                Status = IsDeveloper ? "개발 중" : "최신 버전",
                InstallPath = _config.InstallDir,
                EngineVersion = "Unreal",
                Technology = _config.TargetPlatform,
                SortOrder = 0,
                IsPinned = true
            });
        }

        var projects = VisibleProjects().ToList();
        _selectedProject = projects.FirstOrDefault(project => string.Equals(project.ProjectId, _config.ProjectId, StringComparison.OrdinalIgnoreCase))
                           ?? projects.FirstOrDefault()
                           ?? _config.Projects.First();
    }

    private IEnumerable<ProjectUiConfig> VisibleProjects()
    {
        var query = _projectSearch.Trim();
        return _config.Projects
            .Where(project => project.VisibleToProfiles.Count == 0 || project.VisibleToProfiles.Any(profile => string.Equals(profile, _config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
            .Where(project => string.IsNullOrWhiteSpace(query)
                              || project.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                              || project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || (project.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(project => project.IsPinned)
            .ThenBy(project => project.SortOrder)
            .ThenBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private void BuildDashboard()
    {
        Title = IsDeveloper ? "UE-DT Launcher - Developer" : "UE-DT Launcher";
        Width = IsDeveloper ? 1440 : 1260;
        Height = IsDeveloper ? 900 : 800;
        Background = B(IsDeveloper ? "#0B111A" : "#F5F7FB");

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("320,*"),
            Background = B(IsDeveloper ? "#0B111A" : "#F5F7FB")
        };

        root.Children.Add(BuildHeader());
        root.Children.Add(BuildSidebar());
        root.Children.Add(BuildMainArea());
        Content = root;
    }

    private Control BuildHeader()
    {
        var header = new Grid
        {
            Height = 72,
            Margin = new Thickness(18, 10, 18, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        Grid.SetColumnSpan(header, 2);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
            Background = B("#2563EB"),
            Child = new TextBlock
            {
                Text = "U",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        brand.Children.Add(new StackPanel
        {
            Children =
            {
                T(IsDeveloper ? "UE-DT Launcher" : "UE-DT 런처", 24, true),
                Muted(IsDeveloper ? "개발자용 배포 콘솔" : "프로젝트 업데이트 및 실행", 12)
            }
        });
        header.Children.Add(brand);

        var profile = Pill(IsDeveloper ? "개발자" : "일반 사용자", IsDeveloper ? "#1D4ED8" : "#DBEAFE", IsDeveloper ? "#FFFFFF" : "#2563EB");
        Grid.SetColumn(profile, 2);
        header.Children.Add(profile);
        return header;
    }

    private Control BuildSidebar()
    {
        var sidebar = Card(new DockPanel(), 14);
        sidebar.Margin = new Thickness(14, 8, 8, 14);
        Grid.SetRow(sidebar, 1);

        var panel = (DockPanel)sidebar.Child!;
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        title.Children.Add(T("프로젝트", 18, true));
        if (IsDeveloper)
        {
            var reloadButton = SmallButton("↻", (_, _) => { LoadConfigForUi(); BuildDashboard(); });
            Grid.SetColumn(reloadButton, 1);
            title.Children.Add(reloadButton);
        }
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var searchBox = new TextBox
        {
            Text = _projectSearch,
            Watermark = "프로젝트 검색",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Background = B(IsDeveloper ? "#0F172A" : "#F9FAFB"),
            Foreground = Fg()
        };
        searchBox.TextChanged += (_, _) =>
        {
            _projectSearch = searchBox.Text ?? string.Empty;
            RenderProjectList();
        };
        DockPanel.SetDock(searchBox, Dock.Top);
        panel.Children.Add(searchBox);

        _projectListPanel = new StackPanel { Spacing = 12 };
        RenderProjectList();
        panel.Children.Add(new ScrollViewer { Content = _projectListPanel });

        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        if (IsDeveloper)
        {
            _configPathBox = new TextBox
            {
                Text = GetConfigPathSafe(),
                Watermark = "launcher.config.json",
                FontSize = 12,
                Background = B("#0F172A"),
                Foreground = Fg()
            };
            bottom.Children.Add(_configPathBox);
            bottom.Children.Add(SecondaryButton("설정 파일 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }, 38));
        }
        else
        {
            _configPathBox = new TextBox { Text = GetConfigPathSafe(), IsVisible = false };
            bottom.Children.Add(SecondaryButton("설치 폴더 열기", (_, _) => OpenInstallFolder(), 42));
            bottom.Children.Add(SecondaryButton("설정", (_, _) => ShowGeneralSettingsDialog(), 42));
        }

        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        return sidebar;
    }

    private void RenderProjectList()
    {
        if (_projectListPanel is null) return;
        _projectListPanel.Children.Clear();
        foreach (var project in VisibleProjects())
        {
            _projectListPanel.Children.Add(ProjectCard(project));
        }

        if (_projectListPanel.Children.Count == 0)
        {
            _projectListPanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(14),
                Background = B(IsDeveloper ? "#151E2A" : "#FFFFFF"),
                BorderBrush = B(IsDeveloper ? "#253142" : "#E5E7EB"),
                BorderThickness = new Thickness(1),
                Child = Muted("검색 결과가 없습니다.", 13)
            });
        }
    }

    private Control ProjectCard(ProjectUiConfig project)
    {
        var selected = string.Equals(project.ProjectId, _selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(16),
            Background = B(IsDeveloper ? selected ? "#1E293B" : "#151E2A" : "#FFFFFF"),
            BorderBrush = B(selected ? "#2563EB" : IsDeveloper ? "#253142" : "#E5E7EB"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        card.PointerPressed += (_, _) =>
        {
            _selectedProject = project;
            _config.ProjectId = project.ProjectId;
            _installState = "확인 필요";
            _installStateDetail = "상태 확인을 눌러 설치 상태를 확인하세요.";
            BuildDashboard();
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 12 };
        row.Children.Add(ImageBox(project.ThumbnailPath, 96, 64, 10, project.DisplayName, thumbnail: true));
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(T(project.DisplayName, 14, true));
        info.Children.Add(new TextBlock { Text = project.Status ?? ModeStatus(), FontSize = 12, Foreground = StatusBrush(project.Status) });
        info.Children.Add(Muted(project.Technology ?? _config.TargetPlatform, 11));
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        card.Child = row;
        return card;
    }

    private Control BuildMainArea()
    {
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(8, 8, 14, 14),
            Content = IsDeveloper ? DeveloperBody() : GeneralBody()
        };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, 1);
        return scroll;
    }

    private Control GeneralBody()
    {
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(Hero(320));
        body.Children.Add(GeneralInfo());
        body.Children.Add(GeneralActions());
        body.Children.Add(StatusPanel(showDeveloperLog: false));
        return body;
    }

    private Control DeveloperBody()
    {
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Hero(260));
        body.Children.Add(DeveloperControls());
        var lower = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        lower.Children.Add(ReleaseInfo());
        var status = StatusPanel(showDeveloperLog: true);
        Grid.SetColumn(status, 1);
        lower.Children.Add(status);
        body.Children.Add(lower);
        return body;
    }

    private Control Hero(double height)
    {
        var overlay = new Grid { Height = height };
        overlay.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            Background = B(IsDeveloper ? "#111827" : "#E5E7EB"),
            Child = ImageBox(_selectedProject.HeroPath, 1000, height, 0, _selectedProject.DisplayName, thumbnail: false)
        });
        overlay.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#B0000000"), 0),
                    new GradientStop(Color.Parse("#44000000"), 0.55),
                    new GradientStop(Color.Parse("#00000000"), 1)
                }
            }
        });
        overlay.Children.Add(new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(34),
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                new TextBlock { Text = _selectedProject.DisplayName, FontSize = IsDeveloper ? 30 : 38, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                new TextBlock { Text = IsDeveloper ? $"{_config.Environment} · {_config.Channel} · {_config.TargetPlatform}" : "안정화 최신 버전", FontSize = 16, Foreground = B("#93C5FD") },
                new TextBlock { Text = _selectedProject.Description ?? "프로젝트를 최신 상태로 유지합니다.", FontSize = 13, Foreground = B("#D1D5DB") }
            }
        });
        return overlay;
    }

    private Control GeneralInfo()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        grid.Children.Add(StatusInfoTile());
        var install = InfoTile("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir, "프로젝트 파일 위치");
        Grid.SetColumn(install, 1);
        grid.Children.Add(install);
        var profile = InfoTile("사용자 유형", "일반 사용자", "개발 빌드는 표시되지 않습니다.");
        Grid.SetColumn(profile, 2);
        grid.Children.Add(profile);
        return grid;
    }

    private Control StatusInfoTile()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Muted("설치 상태", 13));
        _installStateText = new TextBlock { Text = _installState, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = StatusBrush(_installState) };
        panel.Children.Add(_installStateText);
        _installStateCaption = Muted(_installStateDetail, 12);
        panel.Children.Add(_installStateCaption);
        return Card(panel, 18);
    }

    private Control GeneralActions()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), ColumnSpacing = 14 };
        grid.Children.Add(PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true), 64));
        var status = SecondaryButton("상태 확인", (_, _) => _ = RefreshInstallStatusAsync(), 64);
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);
        var settings = SecondaryButton("설정", (_, _) => ShowGeneralSettingsDialog(), 64);
        Grid.SetColumn(settings, 2);
        grid.Children.Add(settings);
        return grid;
    }

    private Control DeveloperControls()
    {
        var panel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,Auto,Auto"), ColumnSpacing = 10 };
        row1.Children.Add(ComboTile("환경", _config.Environment, new[] { "prod", "dev" }, value => _config.Environment = value));
        Add(row1, ComboTile("채널", _config.Channel, new[] { "stable", "beta", "dev" }, value => _config.Channel = value), 1);
        Add(row1, ComboTile("플랫폼", _config.TargetPlatform, new[] { "windows-x64", "linux-x64" }, value => _config.TargetPlatform = value), 2);
        Add(row1, ComboTile("버전 정책", _config.VersionPolicy, new[] { "latest", "exact" }, value => _config.VersionPolicy = value), 3);
        Add(row1, PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true), 54), 4);
        Add(row1, SecondaryButton("업데이트", (_, _) => _ = RunAsync(repair: false, launch: false), 54), 5);
        panel.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("상태 확인", (_, _) => _ = RefreshInstallStatusAsync(), 46));
        Add(row2, SecondaryButton("검증/복구", (_, _) => _ = RunAsync(repair: true, launch: false), 46), 1);
        Add(row2, SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 46), 2);
        Add(row2, SecondaryButton("캐시 정리", (_, _) => ClearCache(), 46), 3);
        Add(row2, SecondaryButton("로그 저장", (_, _) => SaveLog(), 46), 4);
        panel.Children.Add(row2);

        var row3 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row3.Children.Add(SecondaryButton("설정 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }, 42));
        Add(row3, SecondaryButton("로그 지우기", (_, _) => ClearLog(), 42), 1);
        Add(row3, SecondaryButton("다시 시도", (_, _) => _ = RunAsync(_lastRunRepair, _lastRunLaunch), 42), 2);
        Add(row3, SecondaryButton("설정 팝업", (_, _) => ShowDeveloperSettingsDialog(), 42), 3);
        Add(row3, SecondaryButton("폴더 크기 새로고침", (_, _) => UpdateStorageText(), 42), 4);
        panel.Children.Add(row3);
        return Card(panel, 16);
    }

    private Control ComboTile(string label, string selectedValue, IEnumerable<string> values, Action<string> apply)
    {
        var combo = new ComboBox
        {
            ItemsSource = values.ToList(),
            SelectedItem = selectedValue,
            MinHeight = 34,
            Background = B(IsDeveloper ? "#0F172A" : "#FFFFFF"),
            Foreground = Fg()
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string value || string.Equals(value, selectedValue, StringComparison.OrdinalIgnoreCase)) return;
            apply(value);
            _installState = "확인 필요";
            _installStateDetail = "환경 설정이 변경되었습니다. 상태 확인을 다시 실행하세요.";
            BuildDashboard();
        };

        return Card(new StackPanel
        {
            Spacing = 6,
            Children = { Muted(label, 12), combo }
        }, 10);
    }

    private Control ReleaseInfo()
    {
        return Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                T("릴리스 정보", 18, true),
                KeyValue("프로젝트", _selectedProject.ProjectId),
                KeyValue("환경", _config.Environment),
                KeyValue("채널", _config.Channel),
                KeyValue("플랫폼", _config.TargetPlatform),
                KeyValue("엔진", _selectedProject.EngineVersion ?? "-"),
                KeyValue("설치 경로", _selectedProject.InstallPath ?? _config.InstallDir),
                KeyValue("카탈로그", string.IsNullOrWhiteSpace(_config.CatalogUrl) ? "직접 manifest" : "사용 중"),
                KeyValue("캐시/백업", StorageSummary())
            }
        }, 18);
    }

    private Control StatusPanel(bool showDeveloperLog)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _statusText = T("준비 완료", 18, true);
        header.Children.Add(_statusText);
        _retryButton = SecondaryButton("다시 시도", (_, _) => _ = RunAsync(_lastRunRepair, _lastRunLaunch), 32);
        _retryButton.IsVisible = false;
        Grid.SetColumn(_retryButton, 1);
        header.Children.Add(_retryButton);
        var saveButton = SecondaryButton("로그 저장", (_, _) => SaveLog(), 32);
        saveButton.IsVisible = IsDeveloper;
        Grid.SetColumn(saveButton, 2);
        header.Children.Add(saveButton);
        panel.Children.Add(header);

        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 10 };
        panel.Children.Add(_progress);
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = showDeveloperLog ? 260 : 70,
            Text = showDeveloperLog ? "실행 로그가 여기에 표시됩니다." : "업데이트 상태가 여기에 표시됩니다.",
            Background = B(IsDeveloper ? "#0B1220" : "#FFFFFF"),
            Foreground = Fg()
        };
        panel.Children.Add(_logBox);
        return Card(panel, 20);
    }

    private Control InfoTile(string title, string value, string caption)
    {
        return Card(new StackPanel { Spacing = 6, Children = { Muted(title, 13), T(value, 17, true), Muted(caption, 12) } }, 18);
    }

    private Control KeyValue(string key, string? value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        grid.Children.Add(Muted(key, 13));
        var valueBlock = T(value ?? "-", 13, false);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private Border Card(Control child, double padding)
    {
        return new Border
        {
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(18),
            Background = B(IsDeveloper ? "#111827" : "#FFFFFF"),
            BorderBrush = B(IsDeveloper ? "#243244" : "#E5E7EB"),
            BorderThickness = new Thickness(1),
            Child = child
        };
    }

    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = new Button
        {
            Content = text,
            Height = height,
            Padding = new Thickness(24, 0),
            Background = B("#2563EB"),
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += handler;
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = new Button
        {
            Content = text,
            Height = height,
            Padding = new Thickness(14, 0),
            Background = B(IsDeveloper ? "#1F2937" : "#FFFFFF"),
            Foreground = Fg(),
            BorderBrush = B(IsDeveloper ? "#374151" : "#D1D5DB"),
            BorderThickness = new Thickness(1),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += handler;
        return button;
    }

    private Button SmallButton(string text, EventHandler<RoutedEventArgs>? handler)
    {
        var button = new Button { Content = text, Height = 34, Padding = new Thickness(12, 0), FontSize = 13 };
        if (handler is not null) button.Click += handler;
        return button;
    }

    private Control ImageBox(string? path, double width, double height, double radius, string label, bool thumbnail)
    {
        var fullPath = ResolveAssetPath(path, thumbnail);
        if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
        {
            return new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                ClipToBounds = true,
                Child = new Image { Source = new Bitmap(fullPath), Stretch = Stretch.UniformToFill }
            };
        }

        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(IsDeveloper ? "#1E3A8A" : "#DBEAFE"), 0),
                    new GradientStop(Color.Parse(IsDeveloper ? "#0F172A" : "#EFF6FF"), 1)
                }
            },
            Child = new TextBlock
            {
                Text = label,
                Foreground = IsDeveloper ? Brushes.White : B("#1E40AF"),
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12)
            }
        };
    }

    private string? ResolveAssetPath(string? path, bool thumbnail)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        var baseDir = Path.Combine(AppContext.BaseDirectory, _config.ProjectAssetsDir, _selectedProject.ProjectId);
        var preferred = Path.Combine(baseDir, thumbnail ? "thumbnail.png" : "hero.png");
        var fallback = Path.Combine(baseDir, thumbnail ? "hero.png" : "thumbnail.png");
        return File.Exists(preferred) ? preferred : File.Exists(fallback) ? fallback : null;
    }

    private async Task<LauncherConfig> LoadRunConfigAsync(bool repair, bool launch)
    {
        var config = await JsonFiles.ReadAsync<LauncherConfig>(GetConfigPathSafe());
        config.ProjectId = _selectedProject.ProjectId;
        config.Environment = _config.Environment;
        config.Channel = _config.Channel;
        config.TargetPlatform = _config.TargetPlatform;
        config.VersionPolicy = _config.VersionPolicy;
        config.RepairMode = repair;
        config.LaunchAfterUpdate = launch;
        if (!string.IsNullOrWhiteSpace(_selectedProject.InstallPath)) config.InstallDir = _selectedProject.InstallPath;
        return config;
    }

    private async Task RunAsync(bool repair, bool launch)
    {
        if (_isRunning)
        {
            AppendLog("이미 작업이 실행 중입니다.", forceGeneral: true);
            return;
        }

        _lastRunRepair = repair;
        _lastRunLaunch = launch;
        _isRunning = true;
        if (_retryButton is not null) _retryButton.IsVisible = false;

        try
        {
            _progress.Value = 0;
            _statusText.Text = launch ? "실행 준비 중..." : "업데이트 확인 중...";
            var config = await LoadRunConfigAsync(repair, launch);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(config, httpClient, UiProgress);
            await new LauncherEngine(config, progress => Dispatcher.UIThread.Post(() => UiProgress(progress.Stage, progress.Message, progress.Percent))).RunAsync();
            _progress.Value = 100;
            _statusText.Text = launch ? "실행되었습니다." : "최신 상태입니다.";
            _installState = "최신 상태";
            _installStateDetail = "현재 설치된 파일이 최신 배포 정보와 일치합니다.";
            UpdateInstallStateTile();
            AppendLog(launch ? "프로젝트 실행 요청이 완료되었습니다." : "업데이트 확인이 완료되었습니다.", forceGeneral: true);
        }
        catch (Exception ex)
        {
            MarkError(ex);
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task RefreshInstallStatusAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            _statusText.Text = "설치 상태 확인 중...";
            _progress.Value = 5;
            var config = await LoadRunConfigAsync(repair: false, launch: false);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(config, httpClient, UiProgress);
            var manifestJson = await httpClient.GetStringAsync(config.ManifestUrl);
            await ManifestSignatureVerifier.VerifyIfConfiguredAsync(manifestJson, config, httpClient);
            var manifest = JsonSerializer.Deserialize<LauncherManifest>(manifestJson, JsonFiles.Options)
                           ?? throw new InvalidOperationException("manifest.json을 읽을 수 없습니다.");

            var missing = 0;
            var changed = 0;
            foreach (var file in manifest.Files)
            {
                var installedPath = SafePath.ResolveInside(config.InstallDir, file.Path);
                if (!File.Exists(installedPath))
                {
                    missing++;
                    continue;
                }

                if (!await Hashing.Sha256MatchesAsync(installedPath, file.Sha256)) changed++;
            }

            if (missing == manifest.Files.Count)
            {
                _installState = "설치 필요";
                _installStateDetail = "아직 설치된 파일을 찾지 못했습니다.";
            }
            else if (missing > 0 || changed > 0)
            {
                _installState = "업데이트 가능";
                _installStateDetail = $"누락 {missing}개, 변경 {changed}개 파일이 있습니다.";
            }
            else
            {
                _installState = "최신 상태";
                _installStateDetail = $"{manifest.Version} 버전이 설치되어 있습니다.";
            }

            _progress.Value = 100;
            _statusText.Text = _installState;
            UpdateInstallStateTile();
            AppendLog($"설치 상태: {_installState} - {_installStateDetail}", forceGeneral: true);
        }
        catch (Exception ex)
        {
            MarkError(ex, "상태 확인 실패");
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void MarkError(Exception ex, string statusText = "작업 실패")
    {
        _statusText.Text = statusText;
        _installState = "오류";
        _installStateDetail = FriendlyError(ex);
        UpdateInstallStateTile();
        AppendLog("오류: " + FriendlyError(ex), forceGeneral: true);
        if (IsDeveloper) AppendLog(ex.ToString());
        if (_retryButton is not null) _retryButton.IsVisible = true;
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _statusText.Text = FriendlyProgress(stage, message);
            if (percent.HasValue) _progress.Value = percent.Value;
            AppendLog(IsDeveloper ? $"[{stage}] {message}" : FriendlyProgress(stage, message));
        });
    }

    private string FriendlyProgress(string stage, string message)
    {
        if (IsDeveloper) return $"{stage}: {message}";
        return stage switch
        {
            "Catalog" => "배포 정보를 확인하고 있습니다...",
            "Manifest" => "업데이트 정보를 확인하고 있습니다...",
            "Plan" => "필요한 파일을 확인하고 있습니다...",
            "Download" => "필요한 파일을 다운로드하고 있습니다...",
            "Apply" => "업데이트를 적용하고 있습니다...",
            "Launch" => "프로젝트를 실행하고 있습니다...",
            _ => message
        };
    }

    private string FriendlyError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return "업데이트 서버에 연결할 수 없습니다. 네트워크와 서버 주소를 확인하세요.";
        if (message.Contains("No allowed release", StringComparison.OrdinalIgnoreCase))
            return "현재 사용자 권한으로 받을 수 있는 배포 버전이 없습니다.";
        return IsDeveloper ? message : "작업 중 문제가 발생했습니다. 잠시 후 다시 시도하거나 관리자에게 문의하세요.";
    }

    private void UpdateInstallStateTile()
    {
        if (_installStateText is not null)
        {
            _installStateText.Text = _installState;
            _installStateText.Foreground = StatusBrush(_installState);
        }
        if (_installStateCaption is not null) _installStateCaption.Text = _installStateDetail;
    }

    private void ShowGeneralSettingsDialog()
    {
        var dialog = new Window
        {
            Title = "설정",
            Width = 520,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("#F5F7FB")
        };
        var close = DialogButton("닫기");
        close.Click += (_, _) => dialog.Close();
        var save = DialogButton("문제 보고용 로그 저장");
        save.Click += (_, _) => SaveLog();
        var open = DialogButton("설치 폴더 열기");
        open.Click += (_, _) => OpenInstallFolder();
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "런처 설정", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = B("#111827") },
                    InfoLine("프로젝트", _selectedProject.DisplayName),
                    InfoLine("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir),
                    InfoLine("배포 채널", "안정화 최신 버전"),
                    InfoLine("설치 상태", _installState),
                    InfoLine("캐시/백업", StorageSummary()),
                    open,
                    save,
                    close
                }
            }
        };
        dialog.Show(this);
    }

    private void ShowDeveloperSettingsDialog()
    {
        var dialog = new Window
        {
            Title = "개발자 설정",
            Width = 620,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("#0B111A")
        };
        var close = DialogButton("닫기");
        close.Click += (_, _) => dialog.Close();
        var save = DialogButton("로그 저장");
        save.Click += (_, _) => SaveLog();
        var open = DialogButton("설치 폴더 열기");
        open.Click += (_, _) => OpenInstallFolder();
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    T("개발자 설정", 24, true),
                    KeyValue("설정 파일", GetConfigPathSafe()),
                    KeyValue("프로젝트", _selectedProject.ProjectId),
                    KeyValue("환경", _config.Environment),
                    KeyValue("채널", _config.Channel),
                    KeyValue("플랫폼", _config.TargetPlatform),
                    KeyValue("캐시/백업", StorageSummary()),
                    open,
                    save,
                    close
                }
            }
        };
        dialog.Show(this);
    }

    private Button DialogButton(string text)
    {
        return new Button
        {
            Content = text,
            Height = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = B(IsDeveloper ? "#1F2937" : "#FFFFFF"),
            Foreground = Fg()
        };
    }

    private Control InfoLine(string key, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        grid.Children.Add(new TextBlock { Text = key, FontSize = 13, Foreground = B("#6B7280") });
        var valueBlock = new TextBlock { Text = value, FontSize = 13, Foreground = B("#111827"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private void OpenInstallFolder()
    {
        var path = Path.GetFullPath(_selectedProject.InstallPath ?? _config.InstallDir);
        Directory.CreateDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), forceGeneral: true);
        }
    }

    private void ClearCache()
    {
        try
        {
            if (Directory.Exists(_config.StagingDir)) Directory.Delete(_config.StagingDir, recursive: true);
            Directory.CreateDirectory(_config.StagingDir);
            AppendLog("캐시를 정리했습니다.", forceGeneral: true);
        }
        catch (Exception ex)
        {
            AppendLog("캐시 정리에 실패했습니다: " + FriendlyError(ex), forceGeneral: true);
        }
    }

    private void SaveLog()
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, _logBox?.Text ?? string.Empty);
            AppendLog("로그 저장 완료: " + path, forceGeneral: true);
        }
        catch (Exception ex)
        {
            AppendLog("로그 저장 실패: " + FriendlyError(ex), forceGeneral: true);
        }
    }

    private void ClearLog()
    {
        if (_logBox is not null) _logBox.Text = string.Empty;
    }

    private void UpdateStorageText()
    {
        AppendLog("캐시/백업 용량: " + StorageSummary(), forceGeneral: true);
    }

    private string StorageSummary()
    {
        var cache = DirectorySizeSafe(_config.StagingDir);
        var backup = DirectorySizeSafe(_config.BackupDir);
        return $"캐시 {FormatBytes(cache)} / 백업 {FormatBytes(backup)}";
    }

    private static long DirectorySizeSafe(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private string GetConfigPathSafe()
    {
        try
        {
            if (_configPathBox is not null && !string.IsNullOrWhiteSpace(_configPathBox.Text)) return _configPathBox.Text.Trim();
        }
        catch
        {
        }
        return "launcher.config.json";
    }

    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private TextBlock T(string text, double size, bool bold) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Fg() };
    private TextBlock Muted(string text, double size) => new() { Text = text, FontSize = size, Foreground = B(IsDeveloper ? "#94A3B8" : "#6B7280") };
    private IBrush Fg() => B(IsDeveloper ? "#E5E7EB" : "#111827");

    private IBrush StatusBrush(string? status)
    {
        var normalized = status ?? string.Empty;
        if (normalized.Contains("오류", StringComparison.OrdinalIgnoreCase)) return B("#DC2626");
        if (normalized.Contains("업데이트", StringComparison.OrdinalIgnoreCase)) return B("#F97316");
        if (normalized.Contains("설치 필요", StringComparison.OrdinalIgnoreCase)) return B("#7C3AED");
        if (normalized.Contains("설치", StringComparison.OrdinalIgnoreCase) || normalized.Contains("최신", StringComparison.OrdinalIgnoreCase)) return B("#16A34A");
        return B(IsDeveloper ? "#60A5FA" : "#2563EB");
    }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Border Pill(string text, string background, string foreground)
    {
        return new Border
        {
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(14),
            Background = B(background),
            Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = B(foreground), FontSize = 13 }
        };
    }

    private static void Add(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private void AppendLog(string message, bool forceGeneral = false)
    {
        if (_logBox is null) return;
        if (!IsDeveloper && !forceGeneral && _logBox.Text?.Contains(message, StringComparison.OrdinalIgnoreCase) == true) return;
        _logBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
    }
}
