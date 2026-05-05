using System.Text.Json;
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
    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();
    private TextBox _configPathBox = null!;
    private TextBox _logBox = null!;
    private TextBlock _statusText = null!;
    private TextBlock _titleText = null!;
    private TextBlock _versionText = null!;
    private TextBlock _installPathText = null!;
    private ProgressBar _progress = null!;
    private StackPanel _developerPanel = null!;
    private StackPanel _projectList = null!;
    private Border _heroHost = null!;

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
                var json = File.ReadAllText(path);
                _config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonFiles.Options) ?? new LauncherConfig();
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
                Description = IsDeveloper ? "Unreal 패키지 개발/테스트 빌드" : "Unreal 패키지 애플리케이션",
                Status = IsDeveloper ? "개발 중" : "최신 버전",
                InstallPath = _config.InstallDir,
                EngineVersion = "Unreal",
                Technology = _config.TargetPlatform
            });
        }

        _selectedProject = _config.Projects.First();
    }

    private void BuildDashboard()
    {
        Width = IsDeveloper ? 1440 : 1280;
        Height = IsDeveloper ? 900 : 820;
        Background = Brush(IsDeveloper ? "#0B111A" : "#F5F7FB");

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("320,*"),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = Brush(IsDeveloper ? "#0B111A" : "#F5F7FB")
        };

        root.Children.Add(BuildHeader());
        root.Children.Add(BuildSidebar());
        root.Children.Add(BuildMainContent());
        Content = root;
    }

    private Control BuildHeader()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Height = 66,
            Margin = new Thickness(18, 10, 18, 0)
        };
        Grid.SetColumnSpan(header, 2);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = Brush("#2563EB"),
            Child = new TextBlock
            {
                Text = "U",
                FontSize = 23,
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
                new TextBlock { Text = IsDeveloper ? "UE-DT Launcher" : "UE-DT 런처", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = ForegroundBrush() },
                new TextBlock { Text = IsDeveloper ? "Developer distribution console" : "프로젝트 업데이트 및 실행", FontSize = 12, Foreground = MutedBrush() }
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
        var sidebar = new Border
        {
            GridRow = 1,
            Width = 320,
            Margin = new Thickness(14, 8, 8, 14),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(18),
            Background = Brush(IsDeveloper ? "#111827" : "#FFFFFF"),
            BorderBrush = Brush(IsDeveloper ? "#253142" : "#E5E7EB"),
            BorderThickness = new Thickness(1)
        };

        var panel = new DockPanel();
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 0, 2, 14) };
        title.Children.Add(new TextBlock { Text = "프로젝트", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush(), VerticalAlignment = VerticalAlignment.Center });
        var addButton = SmallButton("+", null);
        Grid.SetColumn(addButton, 1);
        title.Children.Add(addButton);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        _projectList = new StackPanel { Spacing = 12 };
        foreach (var project in _config.Projects)
        {
            _projectList.Children.Add(ProjectCard(project));
        }
        panel.Children.Add(new ScrollViewer { Content = _projectList });

        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        _configPathBox = new TextBox
        {
            Text = GetConfigPathSafe(),
            Watermark = "launcher.config.json",
            FontSize = 12,
            Background = Brush(IsDeveloper ? "#0F172A" : "#F9FAFB"),
            Foreground = ForegroundBrush()
        };
        footer.Children.Add(_configPathBox);
        footer.Children.Add(SmallButton("설정 파일 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }));
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        sidebar.Child = panel;
        return sidebar;
    }

    private Control ProjectCard(ProjectUiConfig project)
    {
        var isSelected = string.Equals(project.ProjectId, _selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(14),
            Background = Brush(IsDeveloper ? (isSelected ? "#1E293B" : "#151E2A") : "#FFFFFF"),
            BorderBrush = Brush(isSelected ? "#2563EB" : IsDeveloper ? "#253142" : "#E5E7EB"),
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        card.PointerPressed += (_, _) => { _selectedProject = project; BuildDashboard(); };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 12 };
        grid.Children.Add(ImageBox(project.ThumbnailPath, 96, 64, 10, project.DisplayName));

        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = project.DisplayName, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush() });
        text.Children.Add(new TextBlock { Text = project.Status ?? StatusTextForConfig(), FontSize = 12, Foreground = AccentBrush() });
        text.Children.Add(new TextBlock { Text = _config.TargetPlatform, FontSize = 11, Foreground = MutedBrush() });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        card.Child = grid;
        return card;
    }

    private Control BuildMainContent()
    {
        var main = new ScrollViewer
        {
            GridColumn = 1,
            GridRow = 1,
            Margin = new Thickness(8, 8, 14, 14),
            Content = IsDeveloper ? BuildDeveloperContent() : BuildGeneralContent()
        };
        return main;
    }

    private Control BuildGeneralContent()
    {
        var panel = new StackPanel { Spacing = 20 };
        panel.Children.Add(BuildHero(generalMode: true));
        panel.Children.Add(BuildInfoStrip());
        panel.Children.Add(BuildGeneralActions());
        panel.Children.Add(BuildStatusCard(showLog: false));
        return panel;
    }

    private Control BuildDeveloperContent()
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(BuildHero(generalMode: false));
        panel.Children.Add(BuildDeveloperControls());
        panel.Children.Add(BuildDeveloperLowerArea());
        return panel;
    }

    private Control BuildHero(bool generalMode)
    {
        _heroHost = new Border
        {
            Height = generalMode ? 320 : 260,
            CornerRadius = new CornerRadius(22),
            ClipToBounds = true,
            Background = Brush(IsDeveloper ? "#111827" : "#E5E7EB"),
            Child = ImageBox(_selectedProject.HeroPath, 1000, generalMode ? 320 : 260, 0, _selectedProject.DisplayName)
        };

        var overlay = new Grid();
        overlay.Children.Add(_heroHost);
        overlay.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#AA000000"), 0),
                    new GradientStop(Color.Parse("#22000000"), 0.65),
                    new GradientStop(Color.Parse("#00000000"), 1)
                }
            },
            CornerRadius = new CornerRadius(22)
        });

        var heroText = new StackPanel { Spacing = 10, Margin = new Thickness(34), VerticalAlignment = VerticalAlignment.Bottom };
        _titleText = new TextBlock { Text = _selectedProject.DisplayName, FontSize = generalMode ? 36 : 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        _versionText = new TextBlock { Text = generalMode ? $"안정 버전 · {_config.Channel}" : $"{_config.Environment} / {_config.Channel} / {_config.TargetPlatform}", FontSize = 16, Foreground = Brush("#93C5FD") };
        heroText.Children.Add(_titleText);
        heroText.Children.Add(_versionText);
        heroText.Children.Add(new TextBlock { Text = _selectedProject.Description ?? "프로젝트를 최신 상태로 유지합니다.", FontSize = 13, Foreground = Brush("#D1D5DB") });
        overlay.Children.Add(heroText);
        return overlay;
    }

    private Control BuildInfoStrip()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        grid.Children.Add(InfoTile("최신 업데이트", DateTime.Now.ToString("yyyy. MM. dd."), "버전 정책: 최신 안정화"));
        var install = InfoTile("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir, "프로젝트 파일 위치");
        Grid.SetColumn(install, 1);
        grid.Children.Add(install);
        var size = InfoTile("프로필", "일반 사용자", "운영 안정화 버전만 허용");
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);
        return grid;
    }

    private Control BuildGeneralActions()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*"), ColumnSpacing = 16 };
        grid.Children.Add(PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true)));
        var setting = SecondaryButton("⚙ 설정", (_, _) => ToggleLogPanel());
        Grid.SetColumn(setting, 1);
        grid.Children.Add(setting);
        return grid;
    }

    private Control BuildDeveloperControls()
    {
        _developerPanel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,Auto,Auto"), ColumnSpacing = 10 };
        row1.Children.Add(SelectorTile("환경", _config.Environment));
        var channel = SelectorTile("채널", _config.Channel); Grid.SetColumn(channel, 1); row1.Children.Add(channel);
        var platform = SelectorTile("플랫폼", _config.TargetPlatform); Grid.SetColumn(platform, 2); row1.Children.Add(platform);
        var version = SelectorTile("버전 정책", _config.VersionPolicy); Grid.SetColumn(version, 3); row1.Children.Add(version);
        var run = PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true)); Grid.SetColumn(run, 4); row1.Children.Add(run);
        var update = SecondaryButton("⇩ 업데이트", (_, _) => _ = RunAsync(repair: false, launch: false)); Grid.SetColumn(update, 5); row1.Children.Add(update);
        _developerPanel.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("복구", (_, _) => _ = RunAsync(repair: true, launch: false)));
        var log = SecondaryButton("로그 보기", (_, _) => ToggleLogPanel()); Grid.SetColumn(log, 1); row2.Children.Add(log);
        var manifest = SecondaryButton("매니페스트 검증", (_, _) => AppendLog("매니페스트 검증은 업데이트 실행 시 자동 수행됩니다.")); Grid.SetColumn(manifest, 2); row2.Children.Add(manifest);
        var cache = SecondaryButton("캐시 정리", (_, _) => AppendLog("캐시 정리 기능은 다음 단계에서 실제 삭제 동작으로 확장 예정입니다.")); Grid.SetColumn(cache, 3); row2.Children.Add(cache);
        var settings = SecondaryButton("설정", (_, _) => AppendLog("설정 파일: " + GetConfigPathSafe())); Grid.SetColumn(settings, 4); row2.Children.Add(settings);
        _developerPanel.Children.Add(row2);
        return PanelCard(_developerPanel, 16);
    }

    private Control BuildDeveloperLowerArea()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        var release = PanelCard(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                SectionTitle("릴리스 정보"),
                KeyValue("프로젝트", _selectedProject.ProjectId),
                KeyValue("환경", _config.Environment),
                KeyValue("채널", _config.Channel),
                KeyValue("플랫폼", _config.TargetPlatform),
                KeyValue("설치 경로", _selectedProject.InstallPath ?? _config.InstallDir),
                KeyValue("카탈로그", string.IsNullOrWhiteSpace(_config.CatalogUrl) ? "직접 manifest" : "사용 중")
            }
        }, 16);
        grid.Children.Add(release);

        var logPanel = BuildStatusCard(showLog: true);
        Grid.SetColumn(logPanel, 1);
        grid.Children.Add(logPanel);
        return grid;
    }

    private Control BuildStatusCard(bool showLog)
    {
        var panel = new StackPanel { Spacing = 10 };
        _statusText = new TextBlock { Text = "준비 완료", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush() };
        panel.Children.Add(_statusText);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 10 };
        panel.Children.Add(_progress);
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = showLog ? 260 : 92,
            Text = showLog ? "실행 로그가 여기에 표시됩니다." : "상태 메시지가 여기에 표시됩니다.",
            Background = Brush(IsDeveloper ? "#0B1220" : "#FFFFFF"),
            Foreground = ForegroundBrush()
        };
        panel.Children.Add(_logBox);
        return PanelCard(panel, 20);
    }

    private Control InfoTile(string title, string value, string caption)
    {
        return PanelCard(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontSize = 13, Foreground = MutedBrush() },
                new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush() },
                new TextBlock { Text = caption, FontSize = 12, Foreground = MutedBrush() }
            }
        }, 18);
    }

    private Control SelectorTile(string label, string value)
    {
        return PanelCard(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontSize = 12, Foreground = MutedBrush() },
                new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush() }
            }
        }, 12);
    }

    private Control KeyValue(string key, string? value)
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*"),
            Children =
            {
                new TextBlock { Text = key, Foreground = MutedBrush(), FontSize = 13 },
                new TextBlock { Text = value ?? "-", Foreground = ForegroundBrush(), FontSize = 13, [Grid.ColumnProperty] = 1 }
            }
        };
    }

    private TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = ForegroundBrush(), Margin = new Thickness(0, 0, 0, 4) };

    private Control PanelCard(Control child, double padding)
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

    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = text,
            Height = 58,
            Padding = new Thickness(26, 0),
            Background = Brush("#2563EB"),
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += handler;
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = text,
            Height = 50,
            Padding = new Thickness(18, 0),
            Background = Brush(IsDeveloper ? "#1F2937" : "#FFFFFF"),
            Foreground = ForegroundBrush(),
            BorderBrush = Brush(IsDeveloper ? "#374151" : "#D1D5DB"),
            BorderThickness = new Thickness(1),
            FontSize = 15,
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

    private Control ImageBox(string? path, double width, double height, double radius, string label)
    {
        var fullPath = ResolveAssetPath(path);
        if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
        {
            return new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                ClipToBounds = true,
                Child = new Image
                {
                    Source = new Bitmap(fullPath),
                    Stretch = Stretch.UniformToFill
                }
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

    private string? ResolveAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, _config.ProjectAssetsDir, _selectedProject.ProjectId);
            var hero = Path.Combine(baseDir, "hero.png");
            var thumb = Path.Combine(baseDir, "thumbnail.png");
            return File.Exists(hero) ? hero : File.Exists(thumb) ? thumb : null;
        }

        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    private async Task RunAsync(bool repair, bool launch)
    {
        if (_isRunning)
        {
            AppendLog("이미 작업이 실행 중입니다.");
            return;
        }

        _isRunning = true;
        try
        {
            _progress.Value = 0;
            _statusText.Text = launch ? "업데이트 확인 후 실행합니다..." : "업데이트를 확인합니다...";
            var config = await JsonFiles.ReadAsync<LauncherConfig>(GetConfigPathSafe());
            config.ProjectId = _selectedProject.ProjectId;
            config.RepairMode = repair;
            config.LaunchAfterUpdate = launch;

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(config, httpClient, UiProgress);
            await new LauncherEngine(config, progress => Dispatcher.UIThread.Post(() => UiProgress(progress.Stage, progress.Message, progress.Percent))).RunAsync();
            _progress.Value = 100;
            _statusText.Text = launch ? "실행 준비가 완료되었습니다." : "업데이트가 완료되었습니다.";
            AppendLog("완료");
        }
        catch (Exception ex)
        {
            _statusText.Text = "작업 실패";
            AppendLog("오류: " + FriendlyError(ex));
            if (IsDeveloper) AppendLog(ex.ToString());
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _statusText.Text = $"{stage}: {message}";
            if (percent.HasValue) _progress.Value = percent.Value;
            AppendLog($"[{stage}] {message}");
        });
    }

    private string FriendlyError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return "업데이트 서버에 연결할 수 없습니다. 네트워크와 서버 주소를 확인하세요.";
        if (message.Contains("No allowed release", StringComparison.OrdinalIgnoreCase))
            return "현재 사용자 권한으로 받을 수 있는 배포 버전이 없습니다.";
        return message;
    }

    private void ToggleLogPanel()
    {
        AppendLog("현재 설정 파일: " + GetConfigPathSafe());
        AppendLog("프로젝트 이미지 위치: " + Path.Combine(AppContext.BaseDirectory, _config.ProjectAssetsDir, _selectedProject.ProjectId));
    }

    private async void OnSampleConfigClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = GetConfigPathSafe();
            await JsonFiles.WriteAsync(path, _config);
            AppendLog($"샘플 설정 저장: {path}");
        }
        catch (Exception ex)
        {
            AppendLog("오류: " + ex.Message);
        }
    }

    private string GetConfigPathSafe()
    {
        try
        {
            if (_configPathBox is not null && !string.IsNullOrWhiteSpace(_configPathBox.Text)) return _configPathBox.Text.Trim();
        }
        catch { }
        return "launcher.config.json";
    }

    private string StatusTextForConfig() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private IBrush ForegroundBrush() => Brush(IsDeveloper ? "#E5E7EB" : "#111827");
    private IBrush MutedBrush() => Brush(IsDeveloper ? "#94A3B8" : "#6B7280");
    private IBrush AccentBrush() => Brush(IsDeveloper ? "#60A5FA" : "#2563EB");
    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Border Pill(string text, string background, string foreground)
    {
        return new Border
        {
            Padding = new Thickness(14, 7),
            CornerRadius = new CornerRadius(14),
            Background = Brush(background),
            Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Brush(foreground), FontSize = 13 }
        };
    }

    private void AppendLog(string message)
    {
        if (_logBox is null) return;
        _logBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
    }
}
