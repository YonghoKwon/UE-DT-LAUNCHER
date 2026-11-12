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


}
