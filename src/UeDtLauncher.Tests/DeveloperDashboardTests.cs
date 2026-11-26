using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class DeveloperDashboardTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorizedTrackIsTheOnlyVersionListAndBadgesUseIsLatest(bool exactGrant)
    {
        using var fixture = new Fixture(exactGrant
            ? [Release("2.0.0", false, "이 버전의 첫 줄\r\n두 번째 줄"), Release("1.0.0", true)]
            : [Release("1.0.0", true)]);
        var window = fixture.Window;
        var rows = Find<ListBox>(window, "developer-version-list").Items.Cast<ListBoxItem>().ToArray();
        Assert.Equal(exactGrant ? ["developer-version-2.0.0", "developer-version-1.0.0"] : new[] { "developer-version-1.0.0" },
            rows.Select(Id));
        Assert.Contains("현재 추천", Labels(rows[^1]));
        Assert.Contains("현재 추천", Labels(Find<Border>(window, "release-info")));
        if (exactGrant)
        {
            Assert.Contains("추천 아님", Labels(rows[0]));
            Assert.Contains("이 버전의 첫 줄", Labels(rows[0]));
            Assert.DoesNotContain("두 번째 줄", Labels(rows[0]));
            Find<ListBox>(window, "developer-version-list").SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("exact", fixture.Model.Config.VersionPolicy);
            Assert.Equal("2.0.0", fixture.Model.Config.RequestedVersion);
            Assert.Null(fixture.Model.ProjectStatus);
            var panel = Labels(Find<Border>(window, "release-info"));
            Assert.Contains("2.0.0", panel);
            Assert.Contains("추천 아님", panel);
            Assert.Contains("1.0.0", panel);
        }
        var labels = string.Join("\n", Labels(window));
        foreach (var hidden in new[] { "승인됨·미승격", "공개일", "패키지 크기", "업데이트 크기", "회수 원인",
                     "설치 목록", "저장소 사용량", "서버 주소", "서비스 버전", "버전 정리",
                     "6.6.6", "7.7.7", "8.8.8", "9.9.9" })
            Assert.DoesNotContain(hidden, labels, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InstallPanelShowsOnlySelectedStatusAndFolderOpenDoesNotCreatePath(bool installed, bool hasBackup)
    {
        using var fixture = new Fixture([Release("1.0.0", true)], managed: false);
        var selection = new ReleaseSelection("demo", "prod", "stable", Platform, "1.0.0");
        var runtime = fixture.Model.Config;
        LauncherPaths.ResolveInPlace(runtime, Path.Combine(runtime.StateRootDir, "display.json"));
        VersionedReleasePaths.Bind(runtime, selection);
        SetField(fixture.Window, "_selectedRuntimeConfig", runtime);
        fixture.Model.ProjectStatus = new(installed, installed ? "1.0.0" : null, "1.0.0", false, 0, 0, hasBackup,
            new(new("demo", "prod", "stable", Platform, "0.9.0"), "2026-11-26T00:00:00Z"));
        Invoke(fixture.Window, "BuildEnterprise");
        Dispatcher.UIThread.RunJobs();
        var panel = Find<Border>(fixture.Window, "developer-installation");
        var labels = Labels(panel);
        Assert.Contains(installed ? "설치됨" : "미설치", labels);
        Assert.Equal(installed, labels.Contains("1.0.0"));
        Assert.Contains(hasBackup ? "있음" : "없음", labels);
        Assert.Contains("0.9.0 (기록)", labels);

        var open = Find<Button>(panel, "open-folder");
        Assert.Equal(runtime.InstallDir, Invoke(fixture.Window, "SelectedInstallDirectory"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Presentation(fixture.Window).Title != "설치 폴더를 확인할 수 없습니다. 상태를 먼저 확인해 주세요." && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal("설치 폴더를 확인할 수 없습니다. 상태를 먼저 확인해 주세요.", Presentation(fixture.Window).Title);
        Assert.False(Directory.Exists(runtime.InstallDir));
    }

    [AvaloniaFact]
    public async Task HistoryCopiesFilteredKoreanSummaryAndKeepsOriginalBytes()
    {
        using var fixture = new Fixture([Release("1.0.0", true)]);
        var window = fixture.Window;
        var history = Presentation(window);
        history.Begin(LauncherUiOperation.Update);
        history.Stage("Download");
        var raw = "[Download] 파일 C:\\fixture\\build\\game.exe\nAuthorization: Bearer fixture-secret";
        Invoke(window, "AppendLog", raw, true);
        history.Complete("완료 C:\\fixture\\private");
        history.Begin(LauncherUiOperation.Check);
        Invoke(window, "SetErrorDetails", LauncherUiError.From(new IOException("fixture-secret /var/private/path")));
        var bytes = Encoding.UTF8.GetBytes(history.LogText);
        var summary = Find<TextBox>(window, "developer-history-summary");
        Assert.Matches(@"\d{2}:\d{2}:\d{2}  ·  업데이트 / 파일 준비  ·  진행 중", summary.Text!);
        Assert.Contains("완료", summary.Text);
        Assert.Contains("오류", summary.Text);

        var original = Find<Expander>(window, "developer-history-original");
        Assert.False(original.IsExpanded);
        original.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(Find<TextBox>(window, "operation-log").Text!));
        Find<CheckBox>(window, "developer-history-errors-only").IsChecked = true;
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}  ·  상태 확인 / 확인  ·  오류$", summary.Text!);
        Find<Button>(window, "developer-history-copy").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var copied = await window.Clipboard!.TryGetTextAsync();
        Assert.Equal(summary.Text, copied);
        foreach (var hidden in new[] { "fixture-secret", "Authorization", "C:\\fixture", "/var/private" })
            Assert.DoesNotContain(hidden, copied!);
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(history.LogText));
    }

    [AvaloniaFact]
    public void At1366x768KeyboardScrollsTwentyVersionsAndBottomPanelsAreReachable()
    {
        using var fixture = new Fixture(Enumerable.Range(1, 20).Select(version =>
            Release($"1.0.{version}", version == 1)).ToArray(), width: 1366, height: 768);
        var window = fixture.Window;
        Assert.Equal(1366, window.ClientSize.Width);
        Assert.Equal(768, window.ClientSize.Height);
        var initialWorkspace = Find<ScrollViewer>(window, "workspace-scroll");
        Assert.Equal(0, initialWorkspace.Offset.Y);
        foreach (var panelId in new[] { "developer-release-filters", "release-info", "developer-installation" })
        {
            var panel = Find<Border>(window, panelId);
            var origin = panel.TranslatePoint(new Point(0, 0), initialWorkspace)!.Value;
            Assert.InRange(origin.Y, 0, initialWorkspace.Viewport.Height);
            Assert.InRange(origin.Y + panel.Bounds.Height, 0, initialWorkspace.Viewport.Height + 1);
        }
        var list = Find<ListBox>(window, "developer-version-list");
        Assert.Equal(20, list.Items.Count);
        var first = Assert.IsType<ListBoxItem>(list.SelectedItem);
        Assert.True(first.Focus(NavigationMethod.Tab));
        window.KeyPressQwerty(PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1.0.2", fixture.Model.Config.RequestedVersion);
        for (var index = 2; index < 20; index++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("1.0.20", fixture.Model.Config.RequestedVersion);
        list = Find<ListBox>(window, "developer-version-list");
        var listScroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(listScroll.Offset.Y > 0);

        var workspace = Find<ScrollViewer>(window, "workspace-scroll");
        Assert.True(workspace.Extent.Height > workspace.Viewport.Height);
        workspace.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        foreach (var panelId in new[] { "developer-history", "developer-build-info" })
        {
            var panel = Find<Border>(window, panelId);
            var origin = panel.TranslatePoint(new Point(0, 0), workspace)!.Value;
            Assert.InRange(origin.Y, -1, workspace.Viewport.Height);
            Assert.InRange(origin.Y + panel.Bounds.Height, 0, workspace.Viewport.Height + 1);
        }
    }

    private static string Platform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    private static CatalogReleaseOption Release(string version, bool latest, string? notes = null,
        string project = "demo", string channel = "stable", string environment = "prod", string? platform = null) =>
        new() { ProjectId = project, Environment = environment, Channel = channel, Platform = platform ?? Platform,
            Version = version, IsLatest = latest, Notes = notes };

    private static IEnumerable<string?> Labels(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text);

    private static string? Id(Control control) => AutomationProperties.GetAutomationId(control);

    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(control => Id(control) == id);

    private static LauncherOperationPresentation Presentation(MainWindow window) =>
        (LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uedt-developer-dashboard-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window { get; }
        public LauncherDashboardViewModel Model { get; }

        public Fixture(CatalogReleaseOption[] allowed, bool managed = true, double width = 1280, double height = 720)
        {
            var config = new LauncherConfig
            {
                ProjectId = "demo",
                DeploymentMode = managed ? "managed-agent" : "portable",
                DistributionServerUrl = managed ? null : "https://fixture.invalid",
                InstallDir = Path.Combine(_root, "apps"),
                StateRootDir = Path.Combine(_root, "state"),
                Projects = [new() { ProjectId = "demo", DisplayName = "시험 DT" }]
            };
            Model = new(LauncherEdition.Developer)
            {
                Config = config,
                Catalog = new()
                {
                    SelectionPolicy = CatalogRecommendation.ExplicitPolicy,
                    Releases = [.. allowed,
                        Release("6.6.6", false, project: "another"), Release("7.7.7", false, channel: "beta"),
                        Release("8.8.8", false, environment: "dev"), Release("9.9.9", false, platform: "other-platform")]
                }
            };
            Window = new(new(Path.Combine(_root, "display.json"), LauncherConfigSource.Explicit, false), Model, new(), false);
            Window.Width = width;
            Window.Height = height;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
