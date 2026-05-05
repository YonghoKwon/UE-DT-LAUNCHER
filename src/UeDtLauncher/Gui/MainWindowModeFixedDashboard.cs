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
    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();
    private string _projectSearch = string.Empty;
    private string _installState = "확인 필요";
    private string _installStateDetail = "상태 확인을 눌러 설치 상태를 확인하세요.";
    private bool _isRunning;
    private bool _lastRepair;
    private bool _lastLaunch = true;

    private TextBox? _configPathBox;
    private TextBox? _logBox;
    private TextBlock? _statusText;
    private TextBlock? _installStateText;
    private TextBlock? _installStateDetailText;
    private TextBlock? _storageText;
    private ProgressBar? _progress;
    private Button? _retryButton;
    private StackPanel? _projectListPanel;

    private string LauncherBaseDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private string ConfigPath => ResolveLauncherPath(_configPathBox?.Text ?? "launcher.config.json");
    private bool IsDeveloper => string.Equals(_config.ClientProfile, "developer", StringComparison.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        LoadConfigForUi();
        BuildDashboard();
    }

    private void LoadConfigForUi()
    {
        var path = ConfigPath;
        _config = new LauncherConfig();

        if (File.Exists(path))
        {
            try
            {
                _config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), JsonFiles.Options) ?? new LauncherConfig();
            }
            catch (Exception ex)
            {
                _installState = "오류";
                _installStateDetail = $"설정 파일을 읽을 수 없습니다: {ex.GetBaseException().Message}";
            }
        }
        else
        {
            _installState = "오류";
            _installStateDetail = $"설정 파일이 없습니다: {path}";
        }

        if (_config.Projects.Count == 0)
        {
            _config.Projects.Add(new ProjectUiConfig
            {
                ProjectId = _config.ProjectId ?? "ue-dt-project",
                DisplayName = _config.ProjectId ?? "UE-DT 프로젝트",
                Description = IsDeveloper ? "개발자용 배포 프로젝트입니다." : "운영 안정화 배포 프로젝트입니다.",
                Status = IsDeveloper ? "개발 중" : "최신 버전",
                InstallPath = _config.InstallDir,
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
                              || project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || project.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                              || (project.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(project => project.IsPinned)
            .ThenBy(project => project.SortOrder)
            .ThenBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private void BuildDashboard()
    {
        Title = IsDeveloper ? "UE-DT Launcher - Developer" : "UE-DT Launcher";
        Width = IsDeveloper ? 1440 : 1280;
        Height = IsDeveloper ? 900 : 820;
        MinWidth = 1120;
        MinHeight = 740;
        Background = Brush(IsDeveloper ? "#0B111A" : "#F5F7FB");

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            ColumnDefinitions = new ColumnDefinitions("320,*"),
            Background = Brush(IsDeveloper ? "#0B111A" : "#F5F7FB")
        };

        root.Children.Add(BuildHeader());
        root.Children.Add(BuildSidebar());
        root.Children.Add(BuildMainArea());
        Content = root;
        UpdateStorageText();
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
            Background = Brush("#2563EB"),
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
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text(IsDeveloper ? "UE-DT Launcher" : "UE-DT 런처", 24, true),
                Muted(IsDeveloper ? $"개발자용 배포 콘솔 · config: {ConfigPath}" : "프로젝트 업데이트 및 실행", 12)
            }
        });
        header.Children.Add(brand);

        var pill = Pill(IsDeveloper ? "개발자" : "일반 사용자", IsDeveloper ? "#1D4ED8" : "#DBEAFE", IsDeveloper ? "#FFFFFF" : "#2563EB");
        Grid.SetColumn(pill, 2);
        header.Children.Add(pill);
        return header;
    }

    private Control BuildSidebar()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 12
        };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(Text("프로젝트", 18, true));
        if (IsDeveloper)
        {
            var reload = SmallButton("↻", (_, _) => { LoadConfigForUi(); BuildDashboard(); });
            Grid.SetColumn(reload, 1);
            titleRow.Children.Add(reload);
        }
        Grid.SetRow(titleRow, 0);
        grid.Children.Add(titleRow);

        var searchBox = new TextBox
        {
            Text = _projectSearch,
            Watermark = "프로젝트 검색",
            FontSize = 13,
            Background = Brush(IsDeveloper ? "#0F172A" : "#F9FAFB"),
            Foreground = Foreground()
        };
        searchBox.TextChanged += (_, _) =>
        {
            _projectSearch = searchBox.Text ?? string.Empty;
            RenderProjectList();
        };
        Grid.SetRow(searchBox, 1);
        grid.Children.Add(searchBox);

        _projectListPanel = new StackPanel { Spacing = 12 };
        RenderProjectList();
        var list = new ScrollViewer { Content = _projectListPanel };
        Grid.SetRow(list, 2);
        grid.Children.Add(list);

        var bottom = new StackPanel { Spacing = 8 };
        if (IsDeveloper)
        {
            _configPathBox = new TextBox
            {
                Text = "launcher.config.json",
                Watermark = "launcher.config.json",
                FontSize = 12,
                Background = Brush("#0F172A"),
                Foreground = Foreground()
            };
            bottom.Children.Add(_configPathBox);
            bottom.Children.Add(SecondaryButton("설정 파일 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }, 38));
        }
        else
        {
            _configPathBox = new TextBox { Text = "launcher.config.json", IsVisible = false };
            bottom.Children.Add(SecondaryButton("설치 폴더 열기", (_, _) => OpenInstallFolder(), 42));
            bottom.Children.Add(SecondaryButton("설정", (_, _) => ShowGeneralSettingsDialog(), 42));
        }
        Grid.SetRow(bottom, 3);
        grid.Children.Add(bottom);

        var card = Card(grid, 14);
        card.Margin = new Thickness(14, 8, 8, 14);
        Grid.SetRow(card, 1);
        return card;
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
            _projectListPanel.Children.Add(Card(Muted("검색 결과가 없습니다.", 13), 14));
        }
    }

    private Control ProjectCard(ProjectUiConfig project)
    {
        var selected = string.Equals(project.ProjectId, _selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Padding = new Thickness(10),
            MinHeight = 88,
            CornerRadius = new CornerRadius(16),
            Background = Brush(IsDeveloper ? selected ? "#1E293B" : "#151E2A" : selected ? "#EFF6FF" : "#FFFFFF"),
            BorderBrush = Brush(selected ? "#2563EB" : IsDeveloper ? "#253142" : "#E5E7EB"),
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

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = 12 };
        row.Children.Add(ImageBox(project.ThumbnailPath, 90, 60, 10, project.DisplayName, thumbnail: true));
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(project.DisplayName, 14, true));
        info.Children.Add(Label(project.Status ?? ModeStatus(), 12, StatusBrush(project.Status)));
        info.Children.Add(Label(project.Technology ?? _config.TargetPlatform, 11, MutedBrush()));
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        card.Child = row;
        return card;
    }

    private Control BuildMainArea()
    {
        var body = new ScrollViewer
        {
            Margin = new Thickness(8, 8, 14, 14),
            Content = IsDeveloper ? DeveloperBody() : GeneralBody()
        };
        Grid.SetRow(body, 1);
        Grid.SetColumn(body, 1);
        return body;
    }

    private Control GeneralBody()
    {
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(Hero(320));
        body.Children.Add(GeneralInfo());
        body.Children.Add(GeneralActions());
        body.Children.Add(StatusPanel(false));
        return body;
    }

    private Control DeveloperBody()
    {
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Hero(250));
        body.Children.Add(DeveloperControls());
        var lower = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        lower.Children.Add(ReleaseInfo());
        var status = StatusPanel(true);
        Grid.SetColumn(status, 1);
        lower.Children.Add(status);
        body.Children.Add(lower);
        return body;
    }

    private Control Hero(double height)
    {
        var hero = new Grid { Height = height };
        hero.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            Background = Brush(IsDeveloper ? "#111827" : "#E5E7EB"),
            Child = ImageBox(_selectedProject.HeroPath, 1000, height, 0, _selectedProject.DisplayName, false)
        });
        hero.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(IsDeveloper ? "#D90B111A" : "#B0000000"), 0),
                    new GradientStop(Color.Parse("#44000000"), 0.55),
                    new GradientStop(Color.Parse("#00000000"), 1)
                }
            }
        });
        hero.Children.Add(new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(34),
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                Label(_selectedProject.DisplayName, IsDeveloper ? 30 : 38, Brushes.White, true),
                Label(IsDeveloper ? $"개발자 모드 · {_config.Environment} · {_config.Channel} · {_config.TargetPlatform}" : "안정화 최신 버전", 16, Brush("#93C5FD")),
                Label(_selectedProject.Description ?? "프로젝트를 최신 상태로 유지합니다.", 13, Brush("#D1D5DB"))
            }
        });
        return hero;
    }

    private Control GeneralInfo()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        grid.Children.Add(StatusTile());
        var install = InfoTile("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir, "프로젝트 파일 위치");
        Grid.SetColumn(install, 1);
        grid.Children.Add(install);
        var profile = InfoTile("사용자 유형", "일반 사용자", "개발 빌드는 표시되지 않습니다.");
        Grid.SetColumn(profile, 2);
        grid.Children.Add(profile);
        return grid;
    }

    private Control StatusTile()
    {
        var panel = new StackPanel { Spacing = 6, MinHeight = 92 };
        panel.Children.Add(Muted("설치 상태", 13));
        _installStateText = Label(_installState, 17, StatusBrush(_installState), true);
        _installStateDetailText = Label(_installStateDetail, 12, MutedBrush());
        panel.Children.Add(_installStateText);
        panel.Children.Add(_installStateDetailText);
        return Card(panel, 18);
    }

    private Control GeneralActions()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2.2*,*"),
            RowDefinitions = new RowDefinitions("*,*"),
            ColumnSpacing = 14,
            RowSpacing = 12,
            MinHeight = 132
        };
        var run = PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(false, true), 132);
        Grid.SetRowSpan(run, 2);
        grid.Children.Add(run);
        var status = SecondaryButton("상태 확인", (_, _) => _ = RefreshInstallStatusAsync(), 60);
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);
        var settings = SecondaryButton("설정", (_, _) => ShowGeneralSettingsDialog(), 60);
        Grid.SetColumn(settings, 1);
        Grid.SetRow(settings, 1);
        grid.Children.Add(settings);
        return Card(grid, 14);
    }

    private Control DeveloperControls()
    {
        var panel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,Auto,Auto"), ColumnSpacing = 10 };
        row1.Children.Add(ComboTile("환경", _config.Environment, new[] { "prod", "dev" }, value => _config.Environment = value));
        Add(row1, ComboTile("채널", _config.Channel, new[] { "stable", "beta", "dev" }, value => _config.Channel = value), 1);
        Add(row1, ComboTile("플랫폼", _config.TargetPlatform, new[] { "windows-x64", "linux-x64" }, value => _config.TargetPlatform = value), 2);
        Add(row1, ComboTile("버전 정책", _config.VersionPolicy, new[] { "latest", "exact" }, value => _config.VersionPolicy = value), 3);
        Add(row1, PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(false, true), 54), 4);
        Add(row1, SecondaryButton("업데이트", (_, _) => _ = RunAsync(false, false), 54), 5);
        panel.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("상태 확인", (_, _) => _ = RefreshInstallStatusAsync(), 46));
        Add(row2, SecondaryButton("검증/복구", (_, _) => _ = RunAsync(true, false), 46), 1);
        Add(row2, SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 46), 2);
        Add(row2, SecondaryButton("캐시 정리", (_, _) => ClearCache(), 46), 3);
        Add(row2, SecondaryButton("로그 저장", (_, _) => SaveLog(), 46), 4);
        panel.Children.Add(row2);

        var row3 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row3.Children.Add(SecondaryButton("설정 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }, 42));
        Add(row3, SecondaryButton("로그 지우기", (_, _) => ClearLog(), 42), 1);
        Add(row3, SecondaryButton("다시 시도", (_, _) => _ = RunAsync(_lastRepair, _lastLaunch), 42), 2);
        Add(row3, SecondaryButton("설정 팝업", (_, _) => ShowDeveloperSettingsDialog(), 42), 3);
        Add(row3, SecondaryButton("폴더 크기 새로고침", (_, _) => UpdateStorageText(), 42), 4);
        panel.Children.Add(row3);
        return Card(panel, 16);
    }

    private Control ComboTile(string label, string selected, IEnumerable<string> values, Action<string> apply)
    {
        var combo = new ComboBox
        {
            ItemsSource = values.ToList(),
            SelectedItem = selected,
            MinHeight = 34,
            Background = Brush(IsDeveloper ? "#0F172A" : "#FFFFFF"),
            Foreground = Foreground()
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string value) return;
            apply(value);
            _installState = "확인 필요";
            _installStateDetail = "환경 설정이 변경되었습니다. 상태 확인을 다시 실행하세요.";
            BuildDashboard();
        };
        return Card(new StackPanel { Spacing = 6, Children = { Muted(label, 12), combo } }, 10);
    }

    private Control ReleaseInfo()
    {
        _storageText = Label(StorageSummary(), 13, Foreground());
        return Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("릴리스 정보", 18, true),
                KeyValue("프로젝트", _selectedProject.ProjectId),
                KeyValue("프로필", _config.ClientProfile),
                KeyValue("환경", _config.Environment),
                KeyValue("채널", _config.Channel),
                KeyValue("플랫폼", _config.TargetPlatform),
                KeyValue("설치 경로", _selectedProject.InstallPath ?? _config.InstallDir),
                KeyValue("설정 파일", ConfigPath),
                KeyValue("캐시/백업", StorageSummary())
            }
        }, 18);
    }

    private Control StatusPanel(bool developerLog)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        _statusText = Text("준비 완료", 18, true);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_statusText);
        _retryButton = SecondaryButton("다시 시도", (_, _) => _ = RunAsync(_lastRepair, _lastLaunch), 34);
        _retryButton.IsVisible = false;
        Grid.SetColumn(_retryButton, 1);
        header.Children.Add(_retryButton);
        var save = SecondaryButton("로그 저장", (_, _) => SaveLog(), 34);
        save.IsVisible = IsDeveloper;
        Grid.SetColumn(save, 2);
        header.Children.Add(save);
        panel.Children.Add(header);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 10 };
        panel.Children.Add(_progress);
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = developerLog ? 260 : 72,
            Text = IsDeveloper ? $"config: {ConfigPath}{Environment.NewLine}profile: {_config.ClientProfile}" : "업데이트 상태가 여기에 표시됩니다.",
            Background = Brush(IsDeveloper ? "#0B1220" : "#FFFFFF"),
            Foreground = Foreground()
        };
        panel.Children.Add(_logBox);
        return Card(panel, 20);
    }

    private Control InfoTile(string title, string value, string caption)
    {
        return Card(new StackPanel
        {
            Spacing = 6,
            MinHeight = 92,
            Children = { Muted(title, 13), Text(value, 17, true), Muted(caption, 12) }
        }, 18);
    }

    private Control KeyValue(string key, string? value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        grid.Children.Add(Muted(key, 13));
        var valueBlock = Label(value ?? "-", 13, Foreground());
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
            Background = Brush(IsDeveloper ? "#111827" : "#FFFFFF"),
            BorderBrush = Brush(IsDeveloper ? "#243244" : "#E5E7EB"),
            BorderThickness = new Thickness(1),
            Child = child
        };
    }

    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height);
        button.Background = Brush("#2563EB");
        button.Foreground = Brushes.White;
        button.FontSize = height >= 100 ? 22 : 16;
        button.FontWeight = FontWeight.SemiBold;
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height);
        button.Background = Brush(IsDeveloper ? "#1F2937" : "#FFFFFF");
        button.Foreground = Foreground();
        button.BorderBrush = Brush(IsDeveloper ? "#374151" : "#D1D5DB");
        button.BorderThickness = new Thickness(1);
        return button;
    }

    private Button SmallButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        return SecondaryButton(text, handler, 34);
    }

    private Button BaseButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = new Button
        {
            Content = text,
            Height = height,
            MinWidth = 110,
            Padding = new Thickness(14, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 14
        };
        button.Click += handler;
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
                Foreground = IsDeveloper ? Brushes.White : Brush("#1E40AF"),
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
        if (!string.IsNullOrWhiteSpace(path)) return Path.IsPathRooted(path) ? path : Path.Combine(LauncherBaseDir, path);
        var baseDir = Path.Combine(LauncherBaseDir, _config.ProjectAssetsDir, _selectedProject.ProjectId);
        var preferred = Path.Combine(baseDir, thumbnail ? "thumbnail.png" : "hero.png");
        var fallback = Path.Combine(baseDir, thumbnail ? "hero.png" : "thumbnail.png");
        return File.Exists(preferred) ? preferred : File.Exists(fallback) ? fallback : null;
    }

    private async Task<LauncherConfig> LoadRunConfigAsync(bool repair, bool launch)
    {
        var config = await JsonFiles.ReadAsync<LauncherConfig>(ConfigPath);
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
        if (_isRunning) return;
        _isRunning = true;
        _lastRepair = repair;
        _lastLaunch = launch;
        if (_retryButton is not null) _retryButton.IsVisible = false;
        try
        {
            if (_progress is not null) _progress.Value = 0;
            if (_statusText is not null) _statusText.Text = launch ? "실행 준비 중..." : "업데이트 확인 중...";
            var config = await LoadRunConfigAsync(repair, launch);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(config, httpClient, UiProgress);
            await new LauncherEngine(config, progress => Dispatcher.UIThread.Post(() => UiProgress(progress.Stage, progress.Message, progress.Percent))).RunAsync();
            if (_progress is not null) _progress.Value = 100;
            if (_statusText is not null) _statusText.Text = launch ? "실행되었습니다." : "최신 상태입니다.";
            _installState = "최신 상태";
            _installStateDetail = "현재 설치된 파일이 최신 배포 정보와 일치합니다.";
            UpdateInstallStateTile();
        }
        catch (Exception ex)
        {
            MarkError(ex);
        }
        finally
        {
            _isRunning = false;
            UpdateStorageText();
        }
    }

    private async Task RefreshInstallStatusAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            if (_statusText is not null) _statusText.Text = "설치 상태 확인 중...";
            if (_progress is not null) _progress.Value = 5;
            var config = await LoadRunConfigAsync(false, false);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(config, httpClient, UiProgress);
            var manifestJson = await httpClient.GetStringAsync(config.ManifestUrl);
            await ManifestSignatureVerifier.VerifyIfConfiguredAsync(manifestJson, config, httpClient);
            var manifest = JsonSerializer.Deserialize<LauncherManifest>(manifestJson, JsonFiles.Options) ?? throw new InvalidOperationException("manifest.json을 읽을 수 없습니다.");
            var missing = 0;
            var changed = 0;
            foreach (var file in manifest.Files)
            {
                var installed = SafePath.ResolveInside(config.InstallDir, file.Path);
                if (!File.Exists(installed)) { missing++; continue; }
                if (!await Hashing.Sha256MatchesAsync(installed, file.Sha256)) changed++;
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
            if (_progress is not null) _progress.Value = 100;
            if (_statusText is not null) _statusText.Text = _installState;
            UpdateInstallStateTile();
        }
        catch (Exception ex)
        {
            MarkError(ex, "상태 확인 실패");
        }
        finally
        {
            _isRunning = false;
            UpdateStorageText();
        }
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusText is not null) _statusText.Text = IsDeveloper ? $"{stage}: {message}" : FriendlyProgress(stage, message);
            if (percent.HasValue && _progress is not null) _progress.Value = percent.Value;
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

    private void MarkError(Exception ex, string status = "작업 실패")
    {
        if (_statusText is not null) _statusText.Text = status;
        _installState = "오류";
        _installStateDetail = FriendlyError(ex);
        UpdateInstallStateTile();
        AppendLog("오류: " + FriendlyError(ex), true);
        if (IsDeveloper) AppendLog(ex.ToString(), true);
        if (_retryButton is not null) _retryButton.IsVisible = true;
    }

    private string FriendlyError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)) return "업데이트 서버에 연결할 수 없습니다. 네트워크와 서버 주소를 확인하세요.";
        if (message.Contains("No allowed release", StringComparison.OrdinalIgnoreCase)) return "현재 사용자 권한으로 받을 수 있는 배포 버전이 없습니다.";
        return IsDeveloper ? message : "작업 중 문제가 발생했습니다. 잠시 후 다시 시도하거나 관리자에게 문의하세요.";
    }

    private void UpdateInstallStateTile()
    {
        if (_installStateText is not null)
        {
            _installStateText.Text = _installState;
            _installStateText.Foreground = StatusBrush(_installState);
        }
        if (_installStateDetailText is not null) _installStateDetailText.Text = _installStateDetail;
    }

    private void ShowGeneralSettingsDialog()
    {
        var dialog = new Window { Title = "설정", Width = 520, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F5F7FB") };
        var open = DialogButton("설치 폴더 열기"); open.Click += (_, _) => OpenInstallFolder();
        var save = DialogButton("문제 보고용 로그 저장"); save.Click += (_, _) => SaveLog();
        var close = DialogButton("닫기"); close.Click += (_, _) => dialog.Close();
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "런처 설정", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brush("#111827") },
                    InfoLine("프로젝트", _selectedProject.DisplayName),
                    InfoLine("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir),
                    InfoLine("설정 파일", ConfigPath),
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
        var dialog = new Window { Title = "개발자 설정", Width = 620, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#0B111A") };
        var open = DialogButton("설치 폴더 열기"); open.Click += (_, _) => OpenInstallFolder();
        var save = DialogButton("로그 저장"); save.Click += (_, _) => SaveLog();
        var close = DialogButton("닫기"); close.Click += (_, _) => dialog.Close();
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Text("개발자 설정", 24, true),
                    KeyValue("설정 파일", ConfigPath),
                    KeyValue("프로필", _config.ClientProfile),
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

    private Control InfoLine(string key, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        row.Children.Add(new TextBlock { Text = key, FontSize = 13, Foreground = Brush("#6B7280") });
        var valueBlock = new TextBlock { Text = value, FontSize = 13, Foreground = Brush("#111827"), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);
        return row;
    }

    private Button DialogButton(string text)
    {
        return SecondaryButton(text, (_, _) => { }, 40);
    }

    private void OpenInstallFolder()
    {
        var path = ResolveLauncherPath(_selectedProject.InstallPath ?? _config.InstallDir);
        Directory.CreateDirectory(path);
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), true); }
    }

    private void ClearCache()
    {
        try
        {
            var staging = ResolveLauncherPath(_config.StagingDir);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            UpdateStorageText();
            AppendLog("캐시를 정리했습니다.", true);
        }
        catch (Exception ex) { AppendLog("캐시 정리에 실패했습니다: " + FriendlyError(ex), true); }
    }

    private void SaveLog()
    {
        try
        {
            var dir = Path.Combine(LauncherBaseDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, _logBox?.Text ?? string.Empty);
            AppendLog("로그 저장 완료: " + path, true);
        }
        catch (Exception ex) { AppendLog("로그 저장 실패: " + FriendlyError(ex), true); }
    }

    private void ClearLog()
    {
        if (_logBox is not null) _logBox.Text = string.Empty;
    }

    private void UpdateStorageText()
    {
        if (_storageText is not null) _storageText.Text = StorageSummary();
    }

    private string StorageSummary()
    {
        var cache = DirectorySizeSafe(ResolveLauncherPath(_config.StagingDir));
        var backup = DirectorySizeSafe(ResolveLauncherPath(_config.BackupDir));
        return $"캐시 {FormatBytes(cache)} / 백업 {FormatBytes(backup)}";
    }

    private static long DirectorySizeSafe(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0; }
        catch { return 0; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private string ResolveLauncherPath(string path)
    {
        return Path.IsPathRooted(path) ? path : Path.Combine(LauncherBaseDir, path);
    }

    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private TextBlock Text(string text, double size, bool bold) => Label(text, size, Foreground(), bold);
    private TextBlock Muted(string text, double size) => Label(text, size, MutedBrush());
    private TextBlock Label(string text, double size, IBrush color, bool bold = false) => new() { Text = text, FontSize = size, Foreground = color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private IBrush Foreground() => Brush(IsDeveloper ? "#E5E7EB" : "#111827");
    private IBrush MutedBrush() => Brush(IsDeveloper ? "#94A3B8" : "#6B7280");
    private IBrush StatusBrush(string? status)
    {
        var value = status ?? string.Empty;
        if (value.Contains("오류", StringComparison.OrdinalIgnoreCase)) return Brush("#DC2626");
        if (value.Contains("업데이트", StringComparison.OrdinalIgnoreCase)) return Brush("#F97316");
        if (value.Contains("설치 필요", StringComparison.OrdinalIgnoreCase)) return Brush("#7C3AED");
        if (value.Contains("최신", StringComparison.OrdinalIgnoreCase) || value.Contains("설치", StringComparison.OrdinalIgnoreCase)) return Brush("#16A34A");
        return Brush(IsDeveloper ? "#60A5FA" : "#2563EB");
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Border Pill(string text, string background, string foreground) => new()
    {
        Padding = new Thickness(14, 7),
        CornerRadius = new CornerRadius(14),
        Background = Brush(background),
        Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Brush(foreground), FontSize = 13 }
    };
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column); grid.Children.Add(control); }
    private void AppendLog(string message, bool forceGeneral = false)
    {
        if (_logBox is null) return;
        if (!IsDeveloper && !forceGeneral && _logBox.Text?.Contains(message, StringComparison.OrdinalIgnoreCase) == true) return;
        _logBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
    }
}
