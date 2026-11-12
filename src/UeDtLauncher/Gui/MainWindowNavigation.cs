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
        title.Children.Add(At(Track(SmallButton("↻", async (_, _) => await RefreshSelectionStatusAsync(true))), 1));
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


}
