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
    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();
    private TextBox _configPathBox = null!;
    private TextBox _logBox = null!;
    private TextBlock _statusText = null!;
    private ProgressBar _progress = null!;

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
                Technology = _config.TargetPlatform
            });
        }

        _selectedProject = _config.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, _config.ProjectId, StringComparison.OrdinalIgnoreCase))
                           ?? _config.Projects.First();
    }

    private void BuildDashboard()
    {
        Title = IsDeveloper ? "UE-DT Launcher - Developer" : "UE-DT Launcher";
        Width = IsDeveloper ? 1440 : 1280;
        Height = IsDeveloper ? 900 : 820;
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
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(T("프로젝트", 18, true));
        var reloadButton = SmallButton("↻", (_, _) => { LoadConfigForUi(); BuildDashboard(); });
        Grid.SetColumn(reloadButton, 1);
        title.Children.Add(reloadButton);
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);

        var list = new StackPanel { Spacing = 12 };
        foreach (var project in _config.Projects)
        {
            list.Children.Add(ProjectCard(project));
        }
        panel.Children.Add(new ScrollViewer { Content = list });

        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        _configPathBox = new TextBox
        {
            Text = GetConfigPathSafe(),
            Watermark = "launcher.config.json",
            FontSize = 12,
            Background = B(IsDeveloper ? "#0F172A" : "#F9FAFB"),
            Foreground = Fg()
        };
        bottom.Children.Add(_configPathBox);
        bottom.Children.Add(SecondaryButton("설정 파일 다시 읽기", (_, _) => { LoadConfigForUi(); BuildDashboard(); }, 38));
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        return sidebar;
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
        card.PointerPressed += (_, _) => { _selectedProject = project; _config.ProjectId = project.ProjectId; BuildDashboard(); };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*"), ColumnSpacing = 12 };
        row.Children.Add(ImageBox(project.ThumbnailPath, 96, 64, 10, project.DisplayName, thumbnail: true));
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(T(project.DisplayName, 14, true));
        info.Children.Add(new TextBlock { Text = project.Status ?? ModeStatus(), FontSize = 12, Foreground = Accent() });
        info.Children.Add(Muted(_config.TargetPlatform, 11));
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
        var body = new StackPanel { Spacing = 20 };
        body.Children.Add(Hero(height: 320));
        body.Children.Add(GeneralInfo());
        body.Children.Add(GeneralActions());
        body.Children.Add(StatusPanel(showDeveloperLog: false));
        return body;
    }

    private Control DeveloperBody()
    {
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Hero(height: 260));
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
                new TextBlock { Text = IsDeveloper ? $"{_config.Environment} · {_config.Channel} · {_config.TargetPlatform}" : $"안정 버전 · {_config.Channel}", FontSize = 16, Foreground = B("#93C5FD") },
                new TextBlock { Text = _selectedProject.Description ?? "프로젝트를 최신 상태로 유지합니다.", FontSize = 13, Foreground = B("#D1D5DB") }
            }
        });
        return overlay;
    }

    private Control GeneralInfo()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        grid.Children.Add(InfoTile("최신 업데이트", DateTime.Now.ToString("yyyy. MM. dd."), "최신 안정화 버전만 제공"));
        var install = InfoTile("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir, "프로젝트 파일 위치");
        Grid.SetColumn(install, 1);
        grid.Children.Add(install);
        var profile = InfoTile("사용자 유형", "일반 사용자", "운영/안정 버전 전용");
        Grid.SetColumn(profile, 2);
        grid.Children.Add(profile);
        return grid;
    }

    private Control GeneralActions()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*"), ColumnSpacing = 16 };
        grid.Children.Add(PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true), 64));
        var settings = SecondaryButton("⚙ 설정", (_, _) => ShowInfo(), 64);
        Grid.SetColumn(settings, 1);
        grid.Children.Add(settings);
        return grid;
    }

    private Control DeveloperControls()
    {
        var panel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,Auto,Auto"), ColumnSpacing = 10 };
        row1.Children.Add(SelectorTile("환경", _config.Environment));
        Add(row1, SelectorTile("채널", _config.Channel), 1);
        Add(row1, SelectorTile("플랫폼", _config.TargetPlatform), 2);
        Add(row1, SelectorTile("버전 정책", _config.VersionPolicy), 3);
        Add(row1, PrimaryButton("▶ 실행", (_, _) => _ = RunAsync(repair: false, launch: true), 54), 4);
        Add(row1, SecondaryButton("⇩ 업데이트", (_, _) => _ = RunAsync(repair: false, launch: false), 54), 5);
        panel.Children.Add(row1);

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("복구", (_, _) => _ = RunAsync(repair: true, launch: false), 46));
        Add(row2, SecondaryButton("로그 보기", (_, _) => ShowInfo(), 46), 1);
        Add(row2, SecondaryButton("매니페스트 검증", (_, _) => AppendLog("매니페스트 검증은 업데이트 실행 시 자동 수행됩니다."), 46), 2);
        Add(row2, SecondaryButton("캐시 정리", (_, _) => AppendLog("캐시 정리 기능은 다음 단계에서 실제 삭제 동작으로 확장 예정입니다."), 46), 3);
        Add(row2, SecondaryButton("설정", (_, _) => AppendLog("설정 파일: " + GetConfigPathSafe()), 46), 4);
        panel.Children.Add(row2);
        return Card(panel, 16);
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
                KeyValue("설치 경로", _selectedProject.InstallPath ?? _config.InstallDir),
                KeyValue("카탈로그", string.IsNullOrWhiteSpace(_config.CatalogUrl) ? "직접 manifest" : "사용 중")
            }
        }, 18);
    }

    private Control StatusPanel(bool showDeveloperLog)
    {
        var panel = new StackPanel { Spacing = 10 };
        _statusText = T("준비 완료", 18, true);
        panel.Children.Add(_statusText);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 10 };
        panel.Children.Add(_progress);
        _logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = showDeveloperLog ? 270 : 92,
            Text = showDeveloperLog ? "실행 로그가 여기에 표시됩니다." : "상태 메시지가 여기에 표시됩니다.",
            Background = B(IsDeveloper ? "#0B1220" : "#FFFFFF"),
            Foreground = Fg()
        };
        panel.Children.Add(_logBox);
        return Card(panel, 20);
    }

    private Control InfoTile(string title, string value, string caption)
    {
        return Card(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Muted(title, 13),
                T(value, 17, true),
                Muted(caption, 12)
            }
        }, 18);
    }

    private Control SelectorTile(string label, string value)
    {
        return Card(new StackPanel
        {
            Spacing = 4,
            Children = { Muted(label, 12), T(value, 15, true) }
        }, 12);
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
            Padding = new Thickness(18, 0),
            Background = B(IsDeveloper ? "#1F2937" : "#FFFFFF"),
            Foreground = Fg(),
            BorderBrush = B(IsDeveloper ? "#374151" : "#D1D5DB"),
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
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        }

        var baseDir = Path.Combine(AppContext.BaseDirectory, _config.ProjectAssetsDir, _selectedProject.ProjectId);
        var preferred = Path.Combine(baseDir, thumbnail ? "thumbnail.png" : "hero.png");
        var fallback = Path.Combine(baseDir, thumbnail ? "hero.png" : "thumbnail.png");
        return File.Exists(preferred) ? preferred : File.Exists(fallback) ? fallback : null;
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

    private void ShowInfo()
    {
        AppendLog("설정 파일: " + GetConfigPathSafe());
        AppendLog("프로젝트 이미지 위치: " + Path.Combine(AppContext.BaseDirectory, _config.ProjectAssetsDir, _selectedProject.ProjectId));
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

    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private TextBlock T(string text, double size, bool bold) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Fg() };
    private TextBlock Muted(string text, double size) => new() { Text = text, FontSize = size, Foreground = B(IsDeveloper ? "#94A3B8" : "#6B7280") };
    private IBrush Fg() => B(IsDeveloper ? "#E5E7EB" : "#111827");
    private IBrush Accent() => B(IsDeveloper ? "#60A5FA" : "#2563EB");
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

    private void AppendLog(string message)
    {
        if (_logBox is null) return;
        _logBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        _logBox.CaretIndex = _logBox.Text?.Length ?? 0;
    }
}
