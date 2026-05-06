using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();
    private CatalogSnapshot _catalog = new();
    private string _search = string.Empty;
    private string _catalogState = "카탈로그 미확인";
    private string _installState = "확인 필요";
    private string _installDetail = "상태 확인을 눌러 설치 상태를 확인하세요.";
    private string _releaseNotes = "릴리스 노트가 없습니다.";
    private bool _running;
    private bool _lastRepair;
    private bool _lastLaunch = true;

    private TextBox? _configPathBox;
    private TextBox? _logBox;
    private TextBlock? _statusText;
    private TextBlock? _percentText;
    private TextBlock? _installStateText;
    private TextBlock? _installDetailText;
    private ProgressBar? _progress;
    private StackPanel? _projectList;

    private string BaseDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private string ConfigPath => ResolvePath(_configPathBox?.Text ?? "launcher.config.json");
    private bool IsDeveloper => string.Equals(_config.ClientProfile, "developer", StringComparison.OrdinalIgnoreCase);
    private string CurrentPlatform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        Build();
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
            _installDetail = $"설정 파일이 없습니다: {ConfigPath}";
        }

        _config.TargetPlatform = CurrentPlatform;
        if (!IsDeveloper)
        {
            _config.Environment = "prod";
            _config.Channel = "stable";
            _config.VersionPolicy = "latest";
            _config.RequestedVersion = null;
        }

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
        return _config.Projects
            .Where(p => p.VisibleToProfiles.Count == 0 || p.VisibleToProfiles.Any(profile => string.Equals(profile, _config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
            .Where(p => string.IsNullOrWhiteSpace(_search) || p.ProjectId.Contains(_search, StringComparison.OrdinalIgnoreCase) || p.DisplayName.Contains(_search, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => p.IsPinned)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    private void Build()
    {
        Title = IsDeveloper ? "UE-DT Launcher - Developer" : "UE-DT Launcher";
        Width = IsDeveloper ? 1480 : 1280;
        Height = IsDeveloper ? 920 : 830;
        MinWidth = 1160;
        MinHeight = 760;
        Background = B(IsDeveloper ? "#0B111A" : "#F5F7FB");

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), ColumnDefinitions = new ColumnDefinitions("360,*"), Background = Background };
        root.Children.Add(Header());
        root.Children.Add(Sidebar());
        root.Children.Add(MainArea());
        Content = root;
    }

    private Control Header()
    {
        var header = new Grid { Height = 72, Margin = new Thickness(18, 10, 18, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumnSpan(header, 2);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = B("#2563EB"), Child = new TextBlock { Text = "U", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                new StackPanel { Children = { Txt(IsDeveloper ? "UE-DT Launcher" : "UE-DT 런처", 24, true), Muted(IsDeveloper ? $"개발자용 배포 콘솔 · {CurrentPlatform}" : $"프로젝트 업데이트 및 실행 · {CurrentPlatform}", 12) } }
            }
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Pill(IsDeveloper ? "개발자" : "일반 사용자", IsDeveloper ? "#1D4ED8" : "#DBEAFE", IsDeveloper ? "#FFFFFF" : "#2563EB"));
        right.Children.Add(Pill(CurrentPlatform, IsDeveloper ? "#1E293B" : "#E0F2FE", IsDeveloper ? "#BFDBFE" : "#0369A1"));
        Grid.SetColumn(right, 2);
        header.Children.Add(right);
        return header;
    }

    private Control Sidebar()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(Txt("배포 선택", 18, true));
        title.Children.Add(At(SmallButton("↻", async (_, _) => await RefreshCatalog(true)), 1));
        grid.Children.Add(title);

        var search = new TextBox { Text = _search, Watermark = "프로젝트 검색", FontSize = 13, Background = B(IsDeveloper ? "#0F172A" : "#F9FAFB"), Foreground = Fg() };
        search.TextChanged += (_, _) => { _search = search.Text ?? string.Empty; RenderProjects(); };
        grid.Children.Add(AtRow(search, 1));
        grid.Children.Add(AtRow(Filters(), 2));

        _projectList = new StackPanel { Spacing = 12 };
        RenderProjects();
        grid.Children.Add(AtRow(new ScrollViewer { Content = _projectList }, 3));

        var bottom = new StackPanel { Spacing = 8 };
        if (IsDeveloper)
        {
            _configPathBox = new TextBox { Text = "launcher.config.json", Watermark = "launcher.config.json", FontSize = 12, Background = B("#0F172A"), Foreground = Fg() };
            bottom.Children.Add(_configPathBox);
            bottom.Children.Add(SecondaryButton("설정 다시 읽기", (_, _) => { LoadConfig(); Build(); }, 38));
        }
        else
        {
            _configPathBox = new TextBox { Text = "launcher.config.json", IsVisible = false };
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
            panel.Children.Add(ReadOnlyLine("가동/개발", "prod"));
            panel.Children.Add(ReadOnlyLine("채널", "stable"));
            panel.Children.Add(ReadOnlyLine("버전", "latest"));
        }
        panel.Children.Add(ReadOnlyLine("OS", CurrentPlatform));
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
        var card = Card(root, 12);
        card.MinHeight = 110;
        card.Background = B(IsDeveloper ? selected ? "#1E293B" : "#151E2A" : selected ? "#EFF6FF" : "#FFFFFF");
        card.BorderBrush = B(selected ? "#2563EB" : IsDeveloper ? "#253142" : "#E5E7EB");
        card.BorderThickness = new Thickness(selected ? 2 : 1);
        card.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        card.PointerPressed += (_, _) => { _selectedProject = p; _config.ProjectId = p.ProjectId; SelectionChanged(); Build(); };
        return card;
    }

    private Control MiniBadge(string text) => new Border { Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(7, 3), CornerRadius = new CornerRadius(9), Background = B(IsDeveloper ? "#0F172A" : "#DBEAFE"), Child = Label(text, 11, IsDeveloper ? B("#BFDBFE") : B("#1D4ED8"), true) };

    private Control MainArea()
    {
        var scroll = new ScrollViewer { Margin = new Thickness(8, 8, 14, 14), Content = IsDeveloper ? DeveloperBody() : GeneralBody() };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, 1);
        return scroll;
    }

    private Control GeneralBody()
    {
        return new StackPanel { Spacing = 18, Children = { Hero(320), GeneralInfo(), GeneralActions(), StatusPanel(false) } };
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
        hero.Children.Add(new Border { CornerRadius = new CornerRadius(24), ClipToBounds = true, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse(IsDeveloper ? "#1E3A8A" : "#2563EB"), 0), new GradientStop(Color.Parse(IsDeveloper ? "#0F172A" : "#60A5FA"), 1) } } });
        hero.Children.Add(new StackPanel { Spacing = 10, Margin = new Thickness(34), VerticalAlignment = VerticalAlignment.Bottom, Children = { Label(_selectedProject.DisplayName, IsDeveloper ? 30 : 38, Brushes.White, true), Label(IsDeveloper ? $"개발자 모드 · {_config.Environment} · {_config.Channel} · {CurrentPlatform}" : $"운영 안정화 · stable · {CurrentPlatform}", 16, B("#DBEAFE")), Label(_releaseNotes, 13, B("#E5E7EB")) } });
        return hero;
    }

    private Control GeneralInfo()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 12 };
        grid.Children.Add(StatusTile());
        grid.Children.Add(At(InfoTile("설치 위치", _selectedProject.InstallPath ?? _config.InstallDir, "프로젝트 파일 위치"), 1));
        grid.Children.Add(At(InfoTile("카탈로그", _catalogState, "실제 사용 가능한 배포 확인"), 2));
        return grid;
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2.2*,*"), RowDefinitions = new RowDefinitions("*,*"), ColumnSpacing = 14, RowSpacing = 12, MinHeight = 132 };
        var run = PrimaryButton("▶ 실행", async (_, _) => await RunAsync(false, true), 132);
        Grid.SetRowSpan(run, 2);
        grid.Children.Add(run);
        grid.Children.Add(At(SecondaryButton("상태 확인", async (_, _) => await RefreshInstallStatusAsync(), 60), 1));
        var folder = SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 60);
        Grid.SetColumn(folder, 1); Grid.SetRow(folder, 1); grid.Children.Add(folder);
        return Card(grid, 14);
    }

    private Control DeveloperActions()
    {
        var panel = new StackPanel { Spacing = 12 };
        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*"), ColumnSpacing = 10 };
        row1.Children.Add(PrimaryButton("▶ 실행", async (_, _) => await RunAsync(false, true), 50));
        Add(row1, SecondaryButton("업데이트", async (_, _) => await RunAsync(false, false), 50), 1);
        Add(row1, SecondaryButton("상태 확인", async (_, _) => await RefreshInstallStatusAsync(), 50), 2);
        Add(row1, SecondaryButton("검증/복구", async (_, _) => await RunAsync(true, false), 50), 3);
        Add(row1, SecondaryButton("설치 폴더", (_, _) => OpenInstallFolder(), 50), 4);
        Add(row1, SecondaryButton("캐시 정리", (_, _) => ClearCache(), 50), 5);
        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        row2.Children.Add(SecondaryButton("로그 ZIP", (_, _) => ExportLogsZip(), 42));
        Add(row2, SecondaryButton("로그 지우기", (_, _) => ClearLog(), 42), 1);
        Add(row2, SecondaryButton("다시 시도", async (_, _) => await RunAsync(_lastRepair, _lastLaunch), 42), 2);
        Add(row2, SecondaryButton("백업 정리", (_, _) => CleanupBackups(), 42), 3);
        Add(row2, SecondaryButton("설정 팝업", (_, _) => DeveloperSettings(), 42), 4);
        panel.Children.Add(row1); panel.Children.Add(row2);
        return Card(panel, 16);
    }

    private Control ReleaseInfo()
    {
        return Card(new StackPanel { Spacing = 8, Children = { Txt("선택된 배포 정보", 18, true), KeyValue("프로젝트", _selectedProject.ProjectId), KeyValue("프로필", _config.ClientProfile), KeyValue("가동/개발", _config.Environment), KeyValue("채널", _config.Channel), KeyValue("버전", _config.VersionPolicy == "exact" ? $"exact / {_config.RequestedVersion ?? "미입력"}" : "latest"), KeyValue("OS", CurrentPlatform), KeyValue("카탈로그", _catalogState), KeyValue("릴리스 노트", _releaseNotes), KeyValue("캐시/백업", StorageSummary()) } }, 18);
    }

    private Control StatusPanel(bool developerLog)
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        _statusText = Txt("준비 완료", 18, true);
        header.Children.Add(_statusText);
        _percentText = Label("0%", 18, IsDeveloper ? B("#BFDBFE") : B("#2563EB"), true);
        header.Children.Add(At(_percentText, 1));
        panel.Children.Add(header);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 12 };
        panel.Children.Add(_progress);
        _logBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = developerLog ? 260 : 72, Text = IsDeveloper ? $"config: {ConfigPath}{Environment.NewLine}profile: {_config.ClientProfile}{Environment.NewLine}platform: {CurrentPlatform}" : "업데이트 상태가 여기에 표시됩니다.", Background = B(IsDeveloper ? "#0B1220" : "#FFFFFF"), Foreground = Fg() };
        panel.Children.Add(_logBox);
        return Card(panel, 20);
    }

    private async Task RefreshCatalog(bool rebuild)
    {
        try
        {
            _catalogState = "카탈로그 확인 중...";
            if (rebuild) Build();
            _catalog = await CatalogSnapshotService.LoadAsync(_config, CurrentPlatform);
            _catalogState = _catalog.Status;
            MergeCatalogProjects();
            UpdateReleaseNotes();
        }
        catch (Exception ex)
        {
            _catalogState = "카탈로그 오류";
            AppendLog("카탈로그 확인 실패: " + FriendlyError(ex), true);
            if (!IsDeveloper) ErrorDialog("카탈로그 확인 실패", FriendlyError(ex));
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
        var c = await JsonFiles.ReadAsync<LauncherConfig>(ConfigPath);
        c.ProjectId = _selectedProject.ProjectId; c.Environment = _config.Environment; c.Channel = _config.Channel; c.TargetPlatform = CurrentPlatform; c.VersionPolicy = _config.VersionPolicy; c.RequestedVersion = _config.RequestedVersion; c.RepairMode = repair; c.LaunchAfterUpdate = launch;
        if (!string.IsNullOrWhiteSpace(_selectedProject.InstallPath)) c.InstallDir = _selectedProject.InstallPath;
        return c;
    }

    private async Task RunAsync(bool repair, bool launch)
    {
        if (_running) return;
        if (IsDeveloper && launch && !await ConfirmDevLaunch()) return;
        _running = true; _lastRepair = repair; _lastLaunch = launch; Progress(0);
        try
        {
            if (_statusText is not null) _statusText.Text = launch ? "실행 준비 중..." : "업데이트 확인 중...";
            var c = await RunConfig(repair, launch);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, c.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(c, http, UiProgress);
            await new LauncherEngine(c, p => Dispatcher.UIThread.Post(() => UiProgress(p.Stage, p.Message, p.Percent))).RunAsync();
            Progress(100); if (_statusText is not null) _statusText.Text = launch ? "실행되었습니다." : "최신 상태입니다."; _installState = "최신 상태"; _installDetail = "현재 설치된 파일이 최신 배포 정보와 일치합니다."; UpdateInstallTile();
        }
        catch (Exception ex) { MarkError(ex); }
        finally { _running = false; }
    }

    private async Task RefreshInstallStatusAsync()
    {
        if (_running) return;
        _running = true; Progress(0);
        try
        {
            if (_statusText is not null) _statusText.Text = "설치 상태 확인 중..."; Progress(5);
            var c = await RunConfig(false, false);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, c.HttpTimeoutSeconds)) };
            await CatalogResolver.ResolveAsync(c, http, UiProgress);
            var json = await http.GetStringAsync(c.ManifestUrl);
            await ManifestSignatureVerifier.VerifyIfConfiguredAsync(json, c, http);
            var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, JsonFiles.Options) ?? throw new InvalidOperationException("manifest.json을 읽을 수 없습니다.");
            var missing = 0; var changed = 0;
            foreach (var file in manifest.Files)
            {
                var installed = SafePath.ResolveInside(c.InstallDir, file.Path);
                if (!File.Exists(installed)) { missing++; continue; }
                if (!await Hashing.Sha256MatchesAsync(installed, file.Sha256)) changed++;
            }
            if (missing == manifest.Files.Count) { _installState = "설치 필요"; _installDetail = "아직 설치된 파일을 찾지 못했습니다."; }
            else if (missing > 0 || changed > 0) { _installState = "업데이트 가능"; _installDetail = $"누락 {missing}개, 변경 {changed}개 파일이 있습니다."; }
            else { _installState = "최신 상태"; _installDetail = $"{manifest.Version} 버전이 설치되어 있습니다."; }
            Progress(100); if (_statusText is not null) _statusText.Text = _installState; UpdateInstallTile();
        }
        catch (Exception ex) { MarkError(ex, "상태 확인 실패"); }
        finally { _running = false; }
    }

    private async Task<bool> ConfirmDevLaunch()
    {
        var dialog = new Window { Title = "개발자 배포 실행 확인", Width = 500, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = B("#0B111A") };
        var cancel = SecondaryButton("취소", (_, _) => dialog.Close(false), 42); var run = PrimaryButton("실행", (_, _) => dialog.Close(true), 42);
        dialog.Content = new Border { Padding = new Thickness(22), Child = new StackPanel { Spacing = 12, Children = { Txt("선택한 개발자 배포를 실행할까요?", 22, true), KeyValue("프로젝트", _selectedProject.ProjectId), KeyValue("가동/개발", _config.Environment), KeyValue("채널", _config.Channel), KeyValue("버전", _config.VersionPolicy == "exact" ? _config.RequestedVersion ?? "미입력" : "latest"), KeyValue("OS", CurrentPlatform), new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Children = { cancel, At(run, 1) } } } } };
        return await dialog.ShowDialog<bool>(this);
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        if (_statusText is not null) _statusText.Text = IsDeveloper ? $"{stage}: {message}" : FriendlyProgress(stage, message);
        if (percent.HasValue) Progress(percent.Value); else Progress(stage switch { "Catalog" => 10, "Manifest" => 20, "Plan" => 35, "Download" => Math.Max(_progress?.Value ?? 0, 45), "Apply" => 85, "Package" => 88, "Launch" => 95, _ => Math.Max(_progress?.Value ?? 0, 5) });
        AppendLog(IsDeveloper ? $"[{stage}] {message}" : FriendlyProgress(stage, message));
    }

    private void Progress(double v) { var c = Math.Clamp(v, 0, 100); if (_progress is not null) _progress.Value = c; if (_percentText is not null) _percentText.Text = $"{c:0}%"; }
    private string FriendlyProgress(string stage, string message) => stage switch { "Catalog" => "배포 정보를 확인하고 있습니다...", "Manifest" => "업데이트 정보를 확인하고 있습니다...", "Plan" => "필요한 파일을 확인하고 있습니다...", "Download" => "필요한 파일을 다운로드하고 있습니다...", "Apply" => "업데이트를 적용하고 있습니다...", "Package" => "패키지를 처리하고 있습니다...", "Launch" => "프로젝트를 실행하고 있습니다...", _ => message };
    private void MarkError(Exception ex, string status = "작업 실패") { if (_statusText is not null) _statusText.Text = status; _installState = "오류"; _installDetail = FriendlyError(ex); UpdateInstallTile(); AppendLog("오류: " + FriendlyError(ex), true); if (IsDeveloper) AppendLog(ex.ToString(), true); else ErrorDialog(status, FriendlyError(ex)); }
    private string FriendlyError(Exception ex) { var m = ex.GetBaseException().Message; if (m.Contains("requestedVersion is required", StringComparison.OrdinalIgnoreCase)) return "exact 버전을 사용하려면 요청 버전을 입력해야 합니다."; if (m.Contains("No allowed release", StringComparison.OrdinalIgnoreCase)) return "현재 사용자 권한으로 받을 수 있는 배포 버전이 없습니다."; if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase) || m.Contains("actively refused", StringComparison.OrdinalIgnoreCase)) return "업데이트 서버에 연결할 수 없습니다. 네트워크와 서버 주소를 확인하세요."; return IsDeveloper ? m : "작업 중 문제가 발생했습니다. 잠시 후 다시 시도하거나 관리자에게 문의하세요."; }
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
        d.Content = new Border { Padding = new Thickness(22), Child = new StackPanel { Spacing = 10, Children = { Txt(dev ? "개발자 설정" : "런처 설정", 24, true), KeyValue("설정 파일", ConfigPath), KeyValue("프로젝트", _selectedProject.ProjectId), KeyValue("가동/개발", _config.Environment), KeyValue("채널", _config.Channel), KeyValue("버전", _config.VersionPolicy == "exact" ? _config.RequestedVersion : _config.VersionPolicy), KeyValue("OS", CurrentPlatform), KeyValue("캐시/백업", StorageSummary()), SecondaryButton("설치 폴더 열기", (_, _) => OpenInstallFolder(), 40), SecondaryButton("로그 ZIP 저장", (_, _) => ExportLogsZip(), 40), SecondaryButton("설정 새로고침", (_, _) => { LoadConfig(); Build(); }, 40), SecondaryButton("닫기", (_, _) => d.Close(), 40) } } };
        d.Show(this);
    }

    private void OpenInstallFolder() { var path = ResolvePath(_selectedProject.InstallPath ?? _config.InstallDir); Directory.CreateDirectory(path); try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch (Exception ex) { AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), true); } }
    private void ClearCache() { try { var p = ResolvePath(_config.StagingDir); if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); AppendLog("캐시를 정리했습니다.", true); } catch (Exception ex) { AppendLog("캐시 정리 실패: " + FriendlyError(ex), true); } }
    private void CleanupBackups() { try { var p = ResolvePath(_config.BackupDir); if (Directory.Exists(p)) Directory.Delete(p, true); Directory.CreateDirectory(p); AppendLog("백업을 정리했습니다.", true); } catch (Exception ex) { AppendLog("백업 정리 실패: " + FriendlyError(ex), true); } }
    private void ClearLog() { if (_logBox is not null) _logBox.Text = string.Empty; }
    private string SaveLogFile() { var dir = Path.Combine(BaseDir, "logs"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log"); File.WriteAllText(path, _logBox?.Text ?? string.Empty); return path; }
    private void ExportLogsZip() { try { SaveLogFile(); var dir = Path.Combine(BaseDir, "logs"); var zip = Path.Combine(dir, $"launcher-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip"); ZipFile.CreateFromDirectory(dir, zip); AppendLog("로그 ZIP 저장 완료: " + zip, true); } catch (Exception ex) { AppendLog("로그 ZIP 저장 실패: " + FriendlyError(ex), true); } }
    private string StorageSummary() => $"캐시 {FormatBytes(DirSize(ResolvePath(_config.StagingDir)))} / 백업 {FormatBytes(DirSize(ResolvePath(_config.BackupDir)))}";
    private static long DirSize(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { return 0; } }
    private static string FormatBytes(long b) { string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; var i = 0; while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; } return $"{v:0.##} {u[i]}"; }
    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);

    private TextBlock Txt(string text, double size, bool bold) => Label(text, size, Fg(), bold);
    private TextBlock Muted(string text, double size) => Label(text, size, MutedBrush());
    private TextBlock Label(string text, double size, IBrush color, bool bold = false) => new() { Text = text ?? string.Empty, FontSize = size, Foreground = color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private IBrush Fg() => B(IsDeveloper ? "#E5E7EB" : "#111827");
    private IBrush MutedBrush() => B(IsDeveloper ? "#94A3B8" : "#6B7280");
    private IBrush StatusBrush(string? s) { var v = s ?? string.Empty; if (v.Contains("오류")) return B("#DC2626"); if (v.Contains("업데이트")) return B("#F97316"); if (v.Contains("설치 필요")) return B("#7C3AED"); if (v.Contains("최신") || v.Contains("설치")) return B("#16A34A"); return B(IsDeveloper ? "#60A5FA" : "#2563EB"); }
    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private Border Card(Control child, double padding) => new() { Padding = new Thickness(padding), CornerRadius = new CornerRadius(18), Background = B(IsDeveloper ? "#111827" : "#FFFFFF"), BorderBrush = B(IsDeveloper ? "#243244" : "#E5E7EB"), BorderThickness = new Thickness(1), Child = child };
    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height) { var b = BaseButton(text, handler, height, Brushes.White); b.Background = B("#2563EB"); b.BorderBrush = B("#2563EB"); b.BorderThickness = new Thickness(1); return b; }
    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height) { var b = BaseButton(text, handler, height, IsDeveloper ? B("#F8FAFC") : B("#111827")); b.Background = B(IsDeveloper ? "#1F2937" : "#FFFFFF"); b.BorderBrush = B(IsDeveloper ? "#475569" : "#D1D5DB"); b.BorderThickness = new Thickness(1); return b; }
    private Button SmallButton(string text, EventHandler<RoutedEventArgs> handler) => SecondaryButton(text, handler, 34);
    private Button BaseButton(string text, EventHandler<RoutedEventArgs> handler, double height, IBrush color) { var b = new Button { Content = new TextBlock { Text = text, Foreground = color, FontSize = height >= 100 ? 22 : 14, FontWeight = height >= 100 ? FontWeight.SemiBold : FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }, Height = height, MinWidth = 110, Padding = new Thickness(14, 0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }; b.Click += handler; return b; }
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Border Pill(string text, string bg, string fg) => new() { Padding = new Thickness(14, 7), CornerRadius = new CornerRadius(14), Background = B(bg), Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = B(fg), FontSize = 13, TextAlignment = TextAlignment.Center } };
    private static Control At(Control c, int col) { Grid.SetColumn(c, col); return c; }
    private static Control AtRow(Control c, int row) { Grid.SetRow(c, row); return c; }
    private static void Add(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }
    private void AppendLog(string msg, bool force = false) { if (_logBox is null) return; if (!IsDeveloper && !force && _logBox.Text?.Contains(msg, StringComparison.OrdinalIgnoreCase) == true) return; _logBox.Text += $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"; _logBox.CaretIndex = _logBox.Text?.Length ?? 0; }
}
