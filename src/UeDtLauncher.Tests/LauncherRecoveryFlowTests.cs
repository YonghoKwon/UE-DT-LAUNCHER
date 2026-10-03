using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class LauncherRecoveryFlowTests
{
    [AvaloniaTheory]
    [InlineData("service-unavailable")]
    [InlineData("server-unavailable")]
    public async Task FirstTroubleshootAfterInitialConnectionFailureChecksWithoutInstalling(string previousError)
    {
        using var fixture = new RecoveryFixture();
        var backend = new RecordingBackend(new(false, null, "1.0.0", true, 1, 0, false));
        var model = fixture.Model(catalogAvailable: false);
        var loads = 0;
        var window = fixture.Window(model, backend, (_, _, _) =>
        {
            loads++;
            return Task.FromResult(fixture.Catalog);
        });
        try
        {
            var presentation = Presentation(window);
            presentation.ErrorCode = previousError;
            presentation.SupportId = "previous-failed-request";
            model.GeneralState = GeneralLauncherState.RecoverableError;
            window.Show();
            await InvokeAsync(window, "TroubleshootAsync");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, loads);
            Assert.Equal(1, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(fixture.Selection, backend.LastContext?.Selection);
            Assert.Equal(GeneralLauncherState.NotInstalled, model.GeneralState);
            Assert.Equal(PrimaryActionKind.InstallAndLaunch, model.PrimaryAction);
            Assert.Null(presentation.ErrorCode);
            Assert.Null(presentation.SupportId);
            Assert.Contains("설치/업데이트가 필요", presentation.Title);
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "app")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "state")));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCatalogRetryDoesNotCheckOrInstallAndKeepsItsNewError(bool primaryAction)
    {
        using var fixture = new RecoveryFixture();
        var backend = new RecordingBackend(new(false, null, "1.0.0", true, 1, 0, false));
        var model = fixture.Model(catalogAvailable: false);
        var window = fixture.Window(model, backend, (_, _, _) =>
            Task.FromException<CatalogSnapshot>(new HttpRequestException("isolated server is unavailable")));
        try
        {
            Presentation(window).ErrorCode = "service-unavailable";
            model.GeneralState = GeneralLauncherState.RecoverableError;
            window.Show();
            await InvokeAsync(window, primaryAction ? "ExecutePrimaryActionAsync" : "TroubleshootAsync");

            Assert.Equal(0, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(GeneralLauncherState.RecoverableError, model.GeneralState);
            Assert.Equal("server-unavailable", Presentation(window).ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(Presentation(window).SupportId));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "app")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "state")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RestoreOfFirstInstallBackupDisplaysNotInstalledInsteadOfStaleWorking()
    {
        using var fixture = new RecoveryFixture();
        var (config, preview) = await fixture.PrepareBackupAsync(restoringUninstalled: true);
        var backend = new RecordingBackend(new(false, null, "1.0.0", true, 1, 0, true));
        var model = fixture.Model();
        model.ProjectStatus = new(true, "1.0.0", "1.0.0", false, 0, 0, true);
        model.GeneralState = GeneralLauncherState.Working;
        var window = fixture.Window(model, backend);
        try
        {
            window.Show();
            Assert.True(preview.RestoresUninstalledState);
            await InvokeAsync(window, "RestoreUiPreviewAsync", fixture.Context, config, preview);

            Assert.False(File.Exists(config.InstalledManifestPath));
            Assert.False(File.Exists(Path.Combine(config.InstallDir, "data")));
            Assert.Equal(1, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(GeneralLauncherState.NotInstalled, model.GeneralState);
            Assert.Equal(PrimaryActionKind.InstallAndLaunch, model.PrimaryAction);
            Assert.False(model.ProjectStatus!.IsInstalled);
            Assert.Null(model.ProjectStatus.InstalledVersion);
            Assert.Equal("백업 복원 완료", Presentation(window).Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PortableRestoreRechecksAndReplacesStaleWorkingStateWithReady()
    {
        using var fixture = new RecoveryFixture();
        var (config, preview) = await fixture.PrepareBackupAsync();
        var backend = new RecordingBackend(new(true, "1.0.0", "1.0.0", false, 0, 0, true));
        var model = fixture.Model();
        model.ProjectStatus = new(true, "1.0.0", "1.0.0", true, 0, 1, true);
        model.GeneralState = GeneralLauncherState.Working;
        var window = fixture.Window(model, backend);
        try
        {
            window.Show();
            await InvokeAsync(window, "RestoreUiPreviewAsync", fixture.Context, config, preview);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("good", File.ReadAllText(Path.Combine(config.InstallDir, "data")));
            Assert.Equal(1, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(fixture.Selection, backend.LastContext?.Selection);
            Assert.Equal(GeneralLauncherState.Ready, model.GeneralState);
            Assert.Equal(PrimaryActionKind.Launch, model.PrimaryAction);
            Assert.False(model.ProjectStatus!.UpdateRequired);
            Assert.Equal(0, model.ProjectStatus.ChangedFiles);
            Assert.Equal("백업 복원 완료", Presentation(window).Title);
            Assert.Equal(100, Presentation(window).Percent);
            Assert.Null(Presentation(window).ErrorCode);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PostRestoreCheckFailureOffersReadOnlyRetryWithoutRepeatingRestore()
    {
        using var fixture = new RecoveryFixture();
        var (config, preview) = await fixture.PrepareBackupAsync();
        var backend = new RecordingBackend(new(true, "1.0.0", "1.0.0", false, 0, 0, true))
        { CheckError = new HttpRequestException("isolated server is unavailable") };
        var model = fixture.Model();
        model.GeneralState = GeneralLauncherState.Working;
        var window = fixture.Window(model, backend);
        try
        {
            window.Show();
            await InvokeAsync(window, "RestoreUiPreviewAsync", fixture.Context, config, preview);

            Assert.Equal("good", File.ReadAllText(Path.Combine(config.InstallDir, "data")));
            Assert.Equal(GeneralLauncherState.RecoverableError, model.GeneralState);
            Assert.Equal(PrimaryActionKind.RetryCheck, model.PrimaryAction);
            Assert.Equal("백업 복원 완료 · 상태 재확인 필요", Presentation(window).Title);
            Assert.Equal("server-unavailable", Presentation(window).ErrorCode);
            Assert.Equal(LauncherUiOperation.Check, Presentation(window).Retry?.Operation);
            Assert.Equal(fixture.Selection, Presentation(window).Retry?.Selection);

            // The restore already succeeded. Retrying its failed verification must only read.
            var before = Snapshot(fixture.Root);
            backend.CheckError = null;
            await InvokeAsync(window, "RetryCurrentAsync");
            Assert.Equal(2, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(before, Snapshot(fixture.Root));
            Assert.Equal(GeneralLauncherState.Ready, model.GeneralState);
            Assert.Null(Presentation(window).ErrorCode);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(RuntimeState.Running)]
    [InlineData(RuntimeState.LaunchPending)]
    [InlineData(RuntimeState.Unknown)]
    public async Task RuntimeObservedAfterRestoreMustNotBecomeReady(RuntimeState runtime)
    {
        using var fixture = new RecoveryFixture();
        var (config, preview) = await fixture.PrepareBackupAsync();
        var backend = new RecordingBackend(new(true, "1.0.0", "1.0.0", false, 0, 0, true))
        { Runtime = new(runtime, "runtime-test-observation", "Execution state needs checking") };
        var model = fixture.Model();
        model.GeneralState = GeneralLauncherState.Working;
        var window = fixture.Window(model, backend);
        try
        {
            window.Show();
            await InvokeAsync(window, "RestoreUiPreviewAsync", fixture.Context, config, preview);

            Assert.Equal("good", File.ReadAllText(Path.Combine(config.InstallDir, "data")));
            Assert.Equal(1, backend.Checks);
            Assert.Equal(0, backend.Executions);
            Assert.Equal(GeneralLauncherState.RuntimeBlocked, model.GeneralState);
            Assert.Equal(PrimaryActionKind.Disabled, model.PrimaryAction);
            if(runtime==RuntimeState.Running)
            {Assert.Null(Presentation(window).ErrorCode);Assert.Null(Presentation(window).SupportId);Assert.Equal("프로그램 실행 중",Presentation(window).Title);}
            else
            {Assert.NotNull(Presentation(window).ErrorCode);Assert.Equal(LauncherUiOperation.Check, Presentation(window).Retry?.Operation);}
        }
        finally { window.Close(); }
    }

    private static LauncherOperationPresentation Presentation(MainWindow window) =>
        (LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static Task InvokeAsync(MainWindow window, string method, params object[] args) =>
        (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;

    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class RecordingBackend(ManagedProjectStatus status) : ILauncherUiBackend
    {
        public int Checks { get; private set; }
        public int Executions { get; private set; }
        public Exception? CheckError { get; set; }
        public RuntimeObservation Runtime { get; init; } = new(RuntimeState.Quiescent, "stopped", "stopped");
        public LauncherUiOperationContext? LastContext { get; private set; }

        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context, LauncherConfig config, Action<LauncherProgress> progress,CancellationToken token=default)
        {
            Checks++;
            LastContext = context;
            if (CheckError is not null) return Task.FromException<LauncherUiOperationResult>(CheckError);
            context.Pin(config);
            VersionedReleasePaths.Bind(config, context.Selection!);
            return Task.FromResult(new LauncherUiOperationResult(config, context.Selection, status, Runtime));
        }

        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context, LauncherConfig config, bool repair, bool launch, Action<LauncherProgress> progress, FileLogger? logger,CancellationToken token=default)
        {
            Executions++;
            throw new InvalidOperationException("These recovery checks must not install, launch or repair.");
        }
    }

    private sealed class RecoveryFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-recovery-flow-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Root, "launcher.config.json");
        public ReleaseSelection Selection { get; } = new("demo", "prod", "stable", OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64", "1.0.0");
        public LauncherUiOperationContext Context => new(false, Selection.ProjectId, Selection.Environment, Selection.Channel, Selection.Platform, "exact", Selection.Version, Selection);
        public CatalogSnapshot Catalog => new()
        {
            Status = "카탈로그 확인 완료",
            Projects = [new() { ProjectId = "demo", DisplayName = "Isolated recovery test", ReleaseCount = 1 }],
            Releases = [new() { ProjectId = "demo", Version = Selection.Version, Environment = "prod", Channel = "stable", Platform = Selection.Platform, IsLatest = true }]
        };

        public RecoveryFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(), JsonFiles.Options));
        }

        private LauncherConfig Config() => new()
        {
            ProjectId = "demo", DeploymentMode = "portable", ClientProfile = "general", VersionPolicy = "exact", RequestedVersion = Selection.Version,
            DistributionServerUrl = "https://fixture.invalid", TargetPlatform = Selection.Platform,
            InstallDir = Path.Combine(Root, "app"), StateRootDir = Path.Combine(Root, "state"), LogDir = Path.Combine(Root, "logs"),
            Projects = [new() { ProjectId = "demo", DisplayName = "Isolated recovery test" }]
        };

        public LauncherDashboardViewModel Model(bool catalogAvailable = true) => new()
        { Config = Config(), Catalog = catalogAvailable ? Catalog : new(), AgentState = "로컬 모드" };

        public MainWindow Window(LauncherDashboardViewModel model, RecordingBackend backend,
            Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>>? catalogLoader = null) =>
            new(new(ConfigPath, LauncherConfigSource.Explicit, false), model, new(), false, backend, catalogLoader);

        public Task<(LauncherConfig Config, RollbackPreview Preview)> PrepareBackupAsync(bool restoringUninstalled = false) => Task.Run(async () =>
        {
            var config = await LauncherPaths.LoadResolvedAsync(ConfigPath);
            Context.Pin(config);
            VersionedReleasePaths.Bind(config, Selection);
            Directory.CreateDirectory(config.InstallDir);
            RuntimeTestSupport.Stopped(config);
            var payload = Path.Combine(config.InstallDir, "data");
            await File.WriteAllTextAsync(payload, "good");
            var manifest = new LauncherManifest
            { AppId = "demo", Version = Selection.Version, Files = [new() { Path = "data", Size = 4, Sha256 = await Hashing.Sha256FileAsync(payload) }] };
            if (!restoringUninstalled) await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
            var backup = BackupManager.CreateBackupRoot(config.BackupDir);
            if (!restoringUninstalled) File.Copy(payload, Path.Combine(backup, "data"));
            await BackupManager.WriteBackupMetadataAsync(backup, new BackupInfo
            { PreviousVersion = restoringUninstalled ? null : Selection.Version, NewVersion = Selection.Version, AddedPaths = restoringUninstalled ? ["data"] : [] }, config.InstalledManifestPath, config.InstallStatePath);
            if (restoringUninstalled) await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
            var preview = (await RollbackPreviewService.ReadAsync(config))!;
            Assert.True(preview.CanRestore);
            await File.WriteAllTextAsync(payload, "damaged");
            return (config, preview);
        });

        public void Dispose() => Directory.Delete(Root, true);
    }
}
