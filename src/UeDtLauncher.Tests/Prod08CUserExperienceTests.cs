using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class Prod08CUserExperienceTests
{
    [AvaloniaFact]
    public async Task FirstLaunchShowsThreeStepsAndSavesPreferences()
    {
        using var fixture = new Fixture();
        var window = fixture.Show(new(1.25, true));
        try
        {
            var guide = Assert.IsType<Window>(await window.ShowFirstRunGuideAsync(fixture.PreferencesPath));
            Assert.Contains("DT Launcher", guide.Title);
            foreach (var text in new[] { "설치", "업데이트", "지원 정보 복사", "설치 위치" })
                Assert.Contains(text, Text(guide));
            Click(Find<Button>(guide, "guide-start"));
            Assert.False(guide.IsVisible);
            var preferences = LauncherUiPreferences.Load(fixture.PreferencesPath);
            Assert.True(preferences.HasSeenFirstRunGuide);
            Assert.Equal(1.25, preferences.TextScale);
            Assert.True(preferences.HighContrast);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SecondLaunchSuppressesGuide()
    {
        using var fixture = new Fixture();
        await new LauncherUiPreferences { HasSeenFirstRunGuide = true }.SaveAsync(fixture.PreferencesPath);
        var window = fixture.Show(LauncherUiPreferences.Load(fixture.PreferencesPath));
        try { Assert.Null(await window.ShowFirstRunGuideAsync(fixture.PreferencesPath)); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DamagedPreferencesShowGuideOnceAgain()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.PreferencesPath, "{ broken");
        var window = fixture.Show(LauncherUiPreferences.Load(fixture.PreferencesPath), managed: true);
        try
        {
            var guide = Assert.IsType<Window>(await window.ShowFirstRunGuideAsync(fixture.PreferencesPath));
            Assert.DoesNotContain("설치 위치", Text(guide));
            guide.Close();
            Assert.True(LauncherUiPreferences.Load(fixture.PreferencesPath).HasSeenFirstRunGuide);
        }
        finally { window.Close(); }
        var second = fixture.Show(LauncherUiPreferences.Load(fixture.PreferencesPath));
        try { Assert.Null(await second.ShowFirstRunGuideAsync(fixture.PreferencesPath)); }
        finally { second.Close(); }
    }

    [AvaloniaFact]
    public async Task ReplayOpensFromDisplayTabWithoutChangingTheSeenRecord()
    {
        using var fixture = new Fixture();
        await new LauncherUiPreferences { HasSeenFirstRunGuide = true }.SaveAsync(fixture.PreferencesPath);
        var before = await File.ReadAllTextAsync(fixture.PreferencesPath);
        var window = fixture.Show(LauncherUiPreferences.Load(fixture.PreferencesPath));
        var dialog = window.ShowEnterpriseSettings(false);
        try
        {
            Click(Find<Button>(dialog, "settings-first-run-guide"));
            var guide = Assert.Single(dialog.OwnedWindows);
            Assert.True(guide.IsVisible);
            Click(Find<Button>(guide, "guide-start"));
            Assert.Equal(before, await File.ReadAllTextAsync(fixture.PreferencesPath));
            Assert.True(dialog.IsVisible);
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsTabsExposeExistingFunctionsAndHideUnavailableData(bool managed)
    {
        using var fixture = new Fixture();
        var window = fixture.Show(new(), managed);
        var presentation = window.GetType().GetField("_presentation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window);
        Assert.IsType<LauncherOperationPresentation>(presentation).SupportId = "DT-FIXTURE";
        var dialog = window.ShowEnterpriseSettings(false);
        try
        {
            var tabs = Find<TabControl>(dialog, "settings-tabs");
            Assert.Equal(new[] { "일반", "정보", "화면", "문제 해결" }, tabs.Items.Cast<TabItem>().Select(t => t.Header));
            var allText = new List<string> { Text(window) };
            for (var tab = 0; tab < tabs.ItemCount; tab++)
            {
                tabs.SelectedIndex = tab;
                Dispatcher.UIThread.RunJobs();
                allText.Add(Text(dialog));
                if (tab == 1)
                {
                    AssertName<Button>(dialog, "settings-copy-support-info", "지원 정보 복사");
                    AssertName<Button>(dialog, "copy-support-id", "지원 ID 복사");
                }
                if (tab == 2)
                {
                    AssertName<ComboBox>(dialog, "text-scale", "글자 크기");
                    AssertName<CheckBox>(dialog, "high-contrast", "앱 고대비 사용");
                    AssertName<Button>(dialog, "settings-first-run-guide", "처음 안내 다시 보기");
                }
                if (tab == 3)
                {
                    AssertName<Button>(dialog, "settings-readiness", "연결·준비 상태 점검");
                    AssertName<Button>(dialog, "settings-reload", "설정 다시 읽기");
                    AssertName<Button>(dialog, "settings-log-folder", "로그 폴더 열기");
                    AssertName<Button>(dialog, "cache-clear", "임시 파일 정리");
                    AssertName<Button>(dialog, "backup-cleanup", "백업 정리");
                    Assert.Equal(!managed, Find<Button>(dialog, "cache-clear").IsEnabled);
                }
            }
            AssertName<Button>(dialog, "settings-export", "지원 로그 ZIP 저장");
            var tree = string.Join("\n", allText);
            foreach (var unavailable in new[] { "공개일", "패키지 크기", "업데이트 크기", "미승격", "회수 원인", "설치 목록", "사용량", "서비스 버전", "fixture-secret", "managed.example" })
                Assert.DoesNotContain(unavailable, tree);
            if (managed) Assert.DoesNotContain("서버 주소", tree);
            else Assert.Contains("https://portable.example:8443", tree);
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task OpeningMissingPortableLogFolderDoesNotCreateIt()
    {
        using var fixture = new Fixture();
        var window = fixture.Show(new());
        var dialog = window.ShowEnterpriseSettings(false);
        try
        {
            Find<TabControl>(dialog, "settings-tabs").SelectedIndex = 3;
            Click(Find<Button>(dialog, "settings-log-folder"));
            var feedback = Find<TextBlock>(dialog, "settings-feedback");
            await WaitFor(() => !string.IsNullOrEmpty(feedback.Text));
            Assert.Equal("아직 저장된 로그 폴더가 없습니다.", feedback.Text);
            Assert.False(Directory.Exists(fixture.LogPath));
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SupportCopyContainsOnlyTheFourFields()
    {
        using var fixture = new Fixture();
        var window = fixture.Show(new(), managed: true);
        var dialog = window.ShowEnterpriseSettings(false);
        try
        {
            Find<TabControl>(dialog, "settings-tabs").SelectedIndex = 1;
            Click(Find<Button>(dialog, "settings-copy-support-info"));
            await WaitFor(() => Find<TextBlock>(dialog, "settings-feedback").Text == "지원 정보를 복사했습니다.");
            var clipboard = Assert.IsAssignableFrom<IClipboard>(dialog.Clipboard);
            var copied = Assert.IsType<string>(await clipboard.TryGetTextAsync());
            Assert.Equal(4, copied.Split('\n').Length);
            foreach (var label in new[] { "런처 버전", "PC 이름", "지원 ID", "시각" })
                Assert.Contains(label + ":", copied);
            Assert.Contains(Environment.MachineName, copied);
            var malicious = MainWindow.SupportInfo(fixture.Root + "\nAuthorization: Bearer fixture-secret", DateTimeOffset.Now);
            foreach (var blocked in new[] { fixture.Root, "fixture-secret", "Authorization", "managed.example" })
            {
                Assert.DoesNotContain(blocked, copied);
                Assert.DoesNotContain(blocked, malicious);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) Assert.DoesNotContain(home, copied, StringComparison.OrdinalIgnoreCase);
        }
        finally { dialog.Close(); window.Close(); }
    }

    [Fact]
    public void HeadingsListsAndLineBreaksAreFormatted()
    {
        Assert.Equal(new[]
        {
            new LauncherReleaseNotes.Section("제목\n", true),
            new LauncherReleaseNotes.Section("• 첫째\n• 둘째\n\n본문", false)
        }, LauncherReleaseNotes.Format("## 제목\r\n- 첫째\r* 둘째\n\n본문"));
    }

    [Fact]
    public void ScriptLinksAndImagesRemainInertText()
    {
        const string value = "<script>alert(1)</script>\n[링크](https://example.test)\n![이미지](https://example.test/photo.png)";
        Assert.Equal(new(value, false), Assert.Single(LauncherReleaseNotes.Format(value)));
    }

    [Fact]
    public void LongNoteKeepsItsEntireContent()
    {
        var value = new string('a', 64 * 1024 - 4) + "\n끝";
        Assert.Equal(64 * 1024, System.Text.Encoding.UTF8.GetByteCount(value));
        Assert.Equal(value, Assert.Single(LauncherReleaseNotes.Format(value)).Text);
    }

    [Fact]
    public void BlankNoteShowsTheEmptyMessage() =>
        Assert.Equal("변경 내역이 등록되지 않았습니다", Assert.Single(LauncherReleaseNotes.Format(" \r\n")).Text);

    [Fact]
    public void MalformedMarkersRemainLiteral()
    {
        const string value = "##누락\n*\n####### 긴 표시\n-   ";
        Assert.Equal(new(value, false), Assert.Single(LauncherReleaseNotes.Format(value)));
    }

    [AvaloniaFact]
    public void OlderReleaseNotesCanExpandAndStayOnTheAuthorizedTrack()
    {
        using var fixture = new Fixture();
        var window = fixture.Show(new(), catalog: true);
        try
        {
            Find<Expander>(window, "release-help").IsExpanded = true;
            var current = Find<ScrollViewer>(window, "current-release-note");
            Assert.Equal("지금\n• 변경", Notes(current));
            Assert.True(current.Focusable);
            Assert.NotEqual(double.PositiveInfinity, current.MaxHeight);
            Find<Expander>(window, "previous-release-notes").IsExpanded = true;
            var older = Find<Expander>(window, "previous-release-1.0.0");
            Assert.Null(older.Content);
            older.IsExpanded = true;
            Assert.Equal("이전 버전\n[링크](https://example.test)", Notes(Find<ScrollViewer>(window, "previous-release-note-1.0.0")));
            foreach (var excluded in new[] { "0.8.0", "0.9.0", "0.7.0", "3.0.0" })
                Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Expander>(),
                    expander => Id(expander) == "previous-release-" + excluded);
        }
        finally { window.Close(); }
    }

    private static string Notes(ScrollViewer notes) =>
        string.Concat(Assert.IsType<TextBlock>(notes.Content).Inlines!.OfType<Run>().Select(run => run.Text));

    private static string Text(Control control) =>
        string.Join("\n", control.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completed()) await Task.Delay(10, timeout.Token);
    }

    private static void AssertName<T>(Control root, string id, string name) where T : Control =>
        Assert.Equal(name, AutomationProperties.GetName(Find<T>(root, id)));

    private static string? Id(Control control) => AutomationProperties.GetAutomationId(control);
    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => Id(c) == id);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-prod08c-ui-" + Guid.NewGuid().ToString("N"));
        public string PreferencesPath => Path.Combine(Root, "ui-preferences.json");
        public string LogPath => Path.Combine(Root, "logs");

        public Fixture() => Directory.CreateDirectory(Root);

        public MainWindow Show(LauncherUiPreferences preferences, bool managed = false, bool catalog = false)
        {
            var model = new LauncherDashboardViewModel(LauncherEdition.General)
            {
                Config = new LauncherConfig
                {
                    DeploymentMode = managed ? "managed-agent" : "portable",
                    ProjectId = "demo",
                    DistributionServerUrl = managed ? "https://managed.example/" :
                        catalog ? "https://portable.example/" : null,
                    ManifestUrl = "https://user:fixture-secret@portable.example:8443/manifest?token=fixture-secret",
                    InstallDir = Path.Combine(Root, "app"),
                    LogDir = LogPath,
                    Projects = [new() { ProjectId = "demo", DisplayName = "제철소 DT" }]
                }
            };
            if (catalog)
                model.Catalog = new CatalogSnapshot
                {
                    Projects = [new() { ProjectId = "demo", DisplayName = "제철소 DT" }],
                    Releases =
                    [
                        Release("2.0.0", "# 지금\n- 변경", latest: true),
                        Release("1.0.0", "# 이전 버전\n[링크](https://example.test)"),
                        Release("3.0.0", "미추천 새 버전"),
                        Release("0.9.0", "다른 프로젝트", project: "other"),
                        Release("0.8.0", "다른 트랙", channel: "beta"),
                        Release("0.7.0", "다른 플랫폼", platform: OperatingSystem.IsWindows() ? "linux-x64" : "windows-x64")
                    ]
                };
            var window = new MainWindow(new(Path.Combine(Root, "launcher.json"), LauncherConfigSource.Explicit, false), model, preferences, false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return window;
        }

        private static CatalogReleaseOption Release(string version, string notes, bool latest = false, string project = "demo", string channel = "stable", string? platform = null) =>
            new()
            {
                ProjectId = project, Version = version, Notes = notes, IsLatest = latest, Environment = "prod",
                Channel = channel, Platform = platform ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64")
            };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
