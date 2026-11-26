using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class LauncherTrayTests
{
    [AvaloniaFact]
    public async Task TenCloseAndOpenCyclesKeepTheSameTimerUntilExplicitExit()
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        var timer = Field(window, "_agentStatusTimer");
        var cache = (Dictionary<string, Bitmap>)Field(window, "_projectVisualCache");
        cache.Add("fixture", new WriteableBitmap(new(1, 1), new(96, 96)));
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();
        await window.StartupCompletion;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, closed);
            Assert.True(window.StatusTimerRunning);
            Assert.Same(timer, Field(window, "_agentStatusTimer"));
            Assert.Single(cache);
            window.OpenFromTray();
            Assert.True(window.IsVisible);
        }
        window.Close();
        await window.ExitFromTrayAsync();
        Assert.Equal(1, closed);
        Assert.False(window.StatusTimerRunning);
        Assert.Empty(cache);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstCloseAfterMissingOrCorruptPreferencesShowsOneNotice(bool corrupt)
    {
        using var fixture = new Fixture(noticeSeen: false);
        if (corrupt) File.WriteAllText(fixture.PreferencesPath, "{broken json");
        var window = fixture.CreateWindow(LauncherUiPreferences.Load(fixture.PreferencesPath), startServices: false);
        try
        {
            window.Show();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.True(Assert.IsType<Window>(Field(window, "_trayNotice")).IsVisible);
            await window.TrayNoticeSaved;
            Assert.True(LauncherUiPreferences.Load(fixture.PreferencesPath).HasSeenTrayNotice);
            window.OpenFromTray();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Null(FieldOrNull(window, "_trayNotice"));
        }
        finally { await window.ExitFromTrayAsync(); }
    }

    [AvaloniaFact]
    public async Task TrayOptOutClosesWindowAndStopsPolling()
    {
        using var fixture = new Fixture(keepInTray: false);
        var window = fixture.Window;
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();
        await window.StartupCompletion;
        window.Close();
        Assert.Equal(1, closed);
        Assert.False(window.StatusTimerRunning);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerAndManualPollOnlyCheckAllowedRecommendations(bool managed)
    {
        using var fixture = new Fixture(managed: managed);
        var window = fixture.CreateWindow(new() { TrayCheckMinutes = 15 }, startServices: false);
        try
        {
            var model = (LauncherDashboardViewModel)Field(window, "_viewModel");
            await window.CheckTrayUpdatesAsync();
            Assert.Equal(["demo", "second"], fixture.Backend.Checks.Select(check => check.ProjectId));
            Assert.All(fixture.Backend.Checks, check => Assert.Equal("2.0.0", check.Selection?.Version));
            Assert.Equal(0, fixture.Backend.Executions);
            Assert.True(window.TrayStatus.HasUpdate);
            Assert.Equal(["업데이트 있음", "최신 상태", "실행 버전 지정 대기"], window.TrayStatus.Projects.Select(row => row.Status));
            Assert.Equal("1.0.0", model.Config.RequestedVersion);
            Assert.Equal("demo", model.Config.ProjectId);
            Assert.Null(model.ProjectStatus);
            if (!managed)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
                await window.PollStatusAsync();
                Assert.Equal(2, fixture.Backend.Checks.Count);
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                await window.PollStatusAsync();
                Assert.Equal(4, fixture.Backend.Checks.Count);
                Assert.Equal(0, fixture.Backend.Executions);
            }
        }
        finally { await window.ExitFromTrayAsync(); }
    }

    [AvaloniaFact]
    public async Task StandaloneCatalogPinsTheRecommendedVersionWithoutMigratingLegacyFiles()
    {
        using var directory = new TempDirectory();
        var configPath = Path.Combine(directory.Path, "config.json");
        var legacy = Path.Combine(directory.Path, "installed-manifest.json");
        const string originalManifest = """{"version":"1.0.0"}""";
        File.WriteAllText(legacy, originalManifest);
        var platform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
        var configuration = new LauncherConfig
        {
            ProjectId = "demo", TargetPlatform = platform, CatalogUrl = "https://fixture.invalid/catalog.json",
            ManifestUrl = "https://fixture.invalid/manifest.json", StateRootDir = ".state",
            InstallDir = "apps", LogDir = "logs", InstalledManifestPath = "installed-manifest.json"
        };
        await JsonFiles.WriteAsync(configPath, configuration);
        var backend = new Backend();
        var window = new MainWindow(new(configPath, LauncherConfigSource.Explicit, true), null, new(), false, backend,
            preferencesPath: Path.Combine(directory.Path, "ui-preferences.json"));
        window.EnableTray((_, _, _) => Task.FromResult(new CatalogSnapshot
        {
            SelectionPolicy = CatalogRecommendation.ExplicitPolicy,
            Projects = [new() { ProjectId = "demo", DisplayName = "demo" }],
            Releases = [new() { ProjectId = "demo", Version = "2.0.0", Environment = "prod", Channel = "stable", Platform = platform, IsLatest = true }]
        }));
        try
        {
            await window.CheckTrayUpdatesAsync();
            var check = Assert.Single(backend.Checks);
            Assert.Null(check.Selection);
            Assert.Equal("exact", check.VersionPolicy);
            Assert.Equal("2.0.0", check.RequestedVersion);
            Assert.True(window.TrayStatus.HasUpdate);
            Assert.Equal(originalManifest, File.ReadAllText(legacy));
            Assert.False(Directory.Exists(Path.Combine(directory.Path, ".state")));
            Assert.Equal(0, backend.Executions);
        }
        finally { await window.ExitFromTrayAsync(); }
    }

    [AvaloniaFact]
    public async Task SettingsApplyChangesThePortableTimerAndPersistsUserPreference()
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        window.Show();
        await window.StartupCompletion;
        await window.CheckTrayUpdatesAsync();
        Assert.Equal(TimeSpan.FromMinutes(30), window.StatusTimerInterval);
        var dialog = window.ShowEnterpriseSettings(true);
        Find<CheckBox>(dialog, "keep-in-tray").IsChecked = false;
        Find<ComboBox>(dialog, "tray-check-interval").SelectedIndex = 2;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => closed.TrySetResult();
        Find<Button>(dialog, "dialog-confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMinutes(60), window.StatusTimerInterval);
        var preferences = LauncherUiPreferences.Load(fixture.PreferencesPath);
        Assert.False(preferences.KeepInTray);
        Assert.Equal(60, preferences.TrayCheckMinutes);
    }

    [Fact]
    public async Task SecondInstanceSignalsExistingOwnerAndEditionsUseSeparateLocks()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "ui-preferences.json");
        using (var owner = Assert.IsType<LauncherGuiInstance>(LauncherGuiInstance.AcquireOrActivate(path, LauncherEdition.General)))
        {
            var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Activate = () => activation.TrySetResult();
            owner.StartListening();
            Assert.Null(LauncherGuiInstance.AcquireOrActivate(path, LauncherEdition.General));
            await activation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var developer = Assert.IsType<LauncherGuiInstance>(LauncherGuiInstance.AcquireOrActivate(path, LauncherEdition.Developer));
        }
        using var restarted = Assert.IsType<LauncherGuiInstance>(LauncherGuiInstance.AcquireOrActivate(path, LauncherEdition.General));
    }

    private static T Find<T>(Window window, string id) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);

    private static object Field(MainWindow window, string name) => FieldOrNull(window, name)!;
    private static object? FieldOrNull(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);

    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("uedt-tray-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly LauncherConfig _config;
        private MainWindow? _window;
        private readonly LauncherUiPreferences _preferences;
        internal readonly ManualClock Clock = new();
        internal readonly Backend Backend = new();
        private string ConfigPath => Path.Combine(_directory.Path, "config.json");
        internal string PreferencesPath => Path.Combine(_directory.Path, "ui-preferences.json");
        internal MainWindow Window => _window ??= CreateWindow(_preferences, startServices: true);

        internal Fixture(bool managed = false, bool noticeSeen = true, bool keepInTray = true)
        {
            _preferences = new() { HasSeenTrayNotice = noticeSeen, KeepInTray = keepInTray };
            _config = new()
            {
                DeploymentMode = managed ? "managed-agent" : "portable", ProjectId = "demo",
                DistributionServerUrl = "https://fixture.invalid", Environment = "prod", Channel = "stable",
                VersionPolicy = "exact", RequestedVersion = "1.0.0",
                Projects =
                [
                    new() { ProjectId = "demo", DisplayName = "first" },
                    new() { ProjectId = "hidden", DisplayName = "hidden", VisibleToProfiles = ["general"] }
                ]
            };
            File.WriteAllText(ConfigPath, "{}");
        }

        internal MainWindow CreateWindow(LauncherUiPreferences preferences, bool startServices)
        {
            var catalog = new CatalogSnapshot
            {
                SelectionPolicy = CatalogRecommendation.ExplicitPolicy,
                Projects = new[] { "demo", "second", "waiting", "hidden", "other-track" }.Select(id =>
                    new CatalogProjectOption { ProjectId = id, DisplayName = id }).ToList(),
                Releases = [Release("demo"), Release("second"), Release("waiting", latest: false), Release("hidden"), Release("other-track", environment: "dev")]
            };
            var model = new LauncherDashboardViewModel(LauncherEdition.Developer) { Config = _config };
            var window = new MainWindow(new(ConfigPath, LauncherConfigSource.Explicit, true), model, preferences,
                startServices, Backend, catalogLoader: (_, _, _) => Task.FromResult(new CatalogSnapshot()),
                runtimeConfigLoader: _ => Task.FromResult(new LauncherConfig { LogDir = Path.Combine(_directory.Path, "logs") }),
                preferencesPath: PreferencesPath);
            window.EnableTray((_, _, _) => Task.FromResult(catalog), Clock);
            return window;
        }

        private static CatalogReleaseOption Release(string project, bool latest = true, string environment = "prod") => new()
        {
            ProjectId = project, Environment = environment, Channel = "stable", Version = "2.0.0",
            Platform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64", IsLatest = latest
        };

        public void Dispose()
        {
            _window?.ExitFromTrayAsync().GetAwaiter().GetResult();
            _directory.Dispose();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan time) => _timestamp += time.Ticks;
    }

    private sealed class Backend : ILauncherUiBackend
    {
        internal readonly List<LauncherUiOperationContext> Checks = [];
        internal int Executions;

        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context, LauncherConfig config,
            Action<LauncherProgress> progress, CancellationToken token = default)
        {
            Checks.Add(context);
            var selection = context.Selection;
            config.SelectedRelease = selection;
            var version = selection?.Version ?? context.RequestedVersion;
            var previous = context.ProjectId == "demo" && selection is not null
                ? new PreviousInstallation(selection with { Version = "1.0.0" }, "") : null;
            var installedVersion = previous is not null ? null : context.ProjectId == "demo" ? "1.0.0" : version;
            return Task.FromResult(new LauncherUiOperationResult(config, selection,
                new(previous is null, installedVersion, version, previous is not null || installedVersion != version, 0, 0, false, previous), null));
        }

        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context, LauncherConfig config,
            bool repair, bool launch, Action<LauncherProgress> progress, FileLogger? logger, CancellationToken token = default)
        {
            Executions++;
            throw new InvalidOperationException("Tray polling attempted a mutation.");
        }
    }
}
