using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> RunRuntimeCommandAsync(string[] args)
    {
        var action = args.FirstOrDefault() ?? "inspect";
        if (action is not ("inspect" or "recover")) throw new ArgumentException("runtime supports inspect or recover.");
        var configPath = Required(args, "--config");
        var display = await JsonFiles.ReadAsync<LauncherConfig>(configPath);
        var config=display.IsManagedDeployment?ManagedClientContext.Create(display):display;
        if(!config.IsManagedDeployment){LauncherPaths.ResolveInPlace(config,configPath);LauncherConfigValidator.Validate(config);}
        if (config.IsManagedDeployment || !string.IsNullOrWhiteSpace(config.DistributionServerUrl))
        {
            var version = Get(args, "--version") ?? (config.VersionPolicy == "exact" ? config.RequestedVersion : null)
                ?? throw new ArgumentException("Runtime inspection/recovery requires an exact --version.");
            var selection=new ReleaseSelection(config.ProjectId!, config.Environment, config.Channel, config.TargetPlatform, version);
            if(config.IsManagedDeployment)ManagedClientContext.Bind(config,selection);else VersionedReleasePaths.Bind(config,selection);
        }
        var confirm = Has(args, "--confirm-stopped");
        var serviceVersion = Has(args, "--service-selection") ? Get(args, "--version") ?? config.SelectedRelease?.Version ?? config.RequestedVersion
            ?? throw new ArgumentException("Service selection requires --version.") : null;
        if (serviceVersion is not null) ReleaseSidecar.Segment(serviceVersion);
        if (action == "recover" && confirm == Has(args, "--dry-run")) throw new ArgumentException("Choose --dry-run or explicit --confirm-stopped after closing every application process.");
        if (serviceVersion is not null && action != "recover") throw new ArgumentException("Service selection requires recovery.");
        RuntimeRecoveryRequest.Validate(config, confirm, serviceVersion);
        RuntimeObservation observation;
        if (config.IsManagedDeployment)
        {
            var response = await new ManagedAgentClient().SendRuntimeAsync("runtime-" + action, config, confirm: confirm, serviceVersion: serviceVersion);
            if (!response.Success || response.Runtime is null) throw new InvalidOperationException(response.Message);
            observation = response.Runtime;
        }
        else
        {
            observation = serviceVersion is not null ? RuntimeServiceState.ConfirmSelection(config, RuntimeIdentities.Current(), serviceVersion)
                : action == "recover" ? RuntimeStore.Recover(config, RuntimeIdentities.Current(), confirm) : RuntimeStore.Observe(config);
        }
        Console.WriteLine(JsonSerializer.Serialize(observation, JsonFiles.Options)); return 0;
    }

    private static async Task<int> ImportInstallAsync(string[] args)
    {
        var plan = await LegacyInstallImport.RunAsync(Required(args, "--config"), Required(args, "--destination-root"), Has(args, "--apply"));
        Console.WriteLine(JsonSerializer.Serialize(plan, JsonFiles.Options)); return 0;
    }

    private static async Task<int> RunLauncherAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var repair = Has(args, "--repair");
        var noLaunch = Has(args, "--no-launch");
        using var cancel = new ConsoleCancellation();

        var display = await JsonFiles.ReadAsync<LauncherConfig>(configPath,cancel.Token);
        var config = display.IsManagedDeployment?ManagedClientContext.Create(display):await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken: cancel.Token);
        var portableOperations = new OperationRegistry(Path.Combine(config.StateRootDir, "operations"));
        if (Get(args, "--resume-id") is { } resumeId)
        {
            if (config.IsManagedDeployment) throw new ArgumentException("Use operation resume for managed deployments.");
            var previous = portableOperations.Inspect(resumeId, RuntimeIdentities.Current());
            if (previous.Phase is not ("Interrupted" or "Cancelled" or "Failed") || previous.Selection is null || previous.ManifestSha256 is null)
                throw new InvalidOperationException("Operation cannot be resumed.");
            config.ProjectId = previous.Selection.ProjectId; config.Environment = previous.Selection.Environment; config.Channel = previous.Selection.Channel;
            config.TargetPlatform = previous.Selection.Platform; config.VersionPolicy = "exact"; config.RequestedVersion = previous.Selection.Version;
            config.ExpectedResumeManifestSha256 = previous.ManifestSha256; config.RepairMode = previous.Command == "repair";
        }
        if (repair) config.RepairMode = true;
        if (noLaunch) config.LaunchAfterUpdate = false;

        if (config.IsManagedDeployment)
        {
            ReleaseSelection? selection = null;
            if (string.IsNullOrWhiteSpace(display.ManifestUrl) || !string.IsNullOrWhiteSpace(display.DistributionServerUrl) || !string.IsNullOrWhiteSpace(display.CatalogUrl) || display.VersionPolicy=="exact")
            {
                var catalogResponse = await new ManagedAgentClient().SendStreamingAsync("catalog", config.ProjectId, _ => { });
                if (!catalogResponse.Success || catalogResponse.Catalog is null) throw new InvalidOperationException("업데이트 서비스에서 배포 목록을 가져오지 못했습니다.");
                var release = CatalogResolver.SelectRelease(catalogResponse.Catalog, config);
                selection = new ReleaseSelection(config.ProjectId!, release.Environment, release.Channel, release.Platform, release.Version);
                ManagedClientContext.Bind(config,selection);
            }
            var operationId = Get(args, "--operation-id") ?? Guid.NewGuid().ToString("N");
            Console.WriteLine("Operation ID: " + operationId);
            var managedResponse = await new ManagedAgentClient().SendCancellableAsync(repair ? "repair" : "update", config.ProjectId,
                progress => Console.WriteLine($"[{progress.Stage}] {progress.Message}"), selection: selection,token:cancel.Token,operationId: operationId);
            PrintAgentResponse(managedResponse);
            if (!managedResponse.Success) return 1;
            if (selection is not null && managedResponse.SelectedRelease != selection) throw new InvalidDataException("Agent release mismatch.");
            if (!noLaunch && !cancel.Token.IsCancellationRequested) _ = await ManagedAppLauncher.LaunchAsync(config,cancel.Token);
            return 0;
        }

        var fileLogger = new FileLogger(config.LogDir);
        var reporter = new ConsoleProgressReporter();
        // Route both catalog resolution and the engine through the reporter so the CLI shows a single
        // in-place progress bar (interactive) or plain lines (redirected/service); engine console echo off.
        try
        {
            await ResolveCatalogForCliAsync(config, (stage, message, percent) => reporter.Report(new LauncherProgress(stage, message, percent)));
            var id = Get(args, "--operation-id") ?? Guid.NewGuid().ToString("N");
            Console.WriteLine("Operation ID: " + id);
            using var operation = portableOperations.Begin(id, RuntimeIdentities.Current(), config.SelectedRelease, cancel.Token, config.RepairMode ? "repair" : "update");
            LauncherEngine? running = null;
            using var engine = new LauncherEngine(config, progress =>
            {
                if (running?.ManifestSha256 is { } digest) operation.Bind(config.SelectedRelease, digest);
                if (progress.Stage == "Download") operation.Phase("Downloading");
                else if (progress.Stage == "Apply") operation.Phase("Applying");
                reporter.Report(progress);
            }, fileLogger, echoToConsole: false);
            running = engine;
            try { await engine.RunAsync(operation.Token); operation.Finish(true); }
            catch (OperationCanceledException) { operation.Finish(engine.InstallationCommitted); throw; }
            catch { operation.Finish(engine.InstallationCommitted, failed: true); throw; }
        }
        finally
        {
            reporter.Finish(); // close an open in-place bar line even if the run threw mid-download
        }
        return 0;
    }

    private static async Task<int> RunOperationAsync(string[] args)
    {
        var action = args.FirstOrDefault() ?? "status";
        var id = Required(args, "--id");
        if (Get(args, "--config") is { } path)
        {
            var display=await JsonFiles.ReadAsync<LauncherConfig>(path);
            var config=display.IsManagedDeployment?ManagedClientContext.Create(display):await LauncherPaths.LoadResolvedAsync(path,readOnly:true);
            if (!config.IsManagedDeployment)
            {
                var registry = new OperationRegistry(Path.Combine(config.StateRootDir, "operations"));
                var peer = RuntimeIdentities.Current();
                if (action == "resume") return await RunLauncherAsync(["--config", path, "--resume-id", id, "--no-launch"]);
                var status = action switch { "status" => registry.Inspect(id, peer), "cancel" => registry.Cancel(id, peer),
                    "discard" => registry.Discard(id, peer), _ => throw new ArgumentException("Unknown operation action.") };
                Console.WriteLine(JsonSerializer.Serialize(status, JsonFiles.Options)); return 0;
            }
        }
        var response = await new ManagedAgentClient(Get(args, "--endpoint")).SendOperationAsync(action, id);
        Console.WriteLine(JsonSerializer.Serialize(response, JsonFiles.Options)); return response.Success ? 0 : 1;
    }
    private static async Task<int> RunScheduledCheckAsync(string[] args)
    {
        using var cancellation=new ConsoleCancellation();
        var result=await ScheduledChecks.RunOnceAsync(Required(args,"--config"),cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(result,JsonFiles.Options));return result.Status=="failed"?1:0;
    }

    private static async Task<int> RunServiceAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var once = Has(args, "--once");
        var managedConfig = await LauncherPaths.LoadResolvedAsync(configPath);
        if (managedConfig.IsManagedDeployment)
        {
            if (!once) throw new InvalidOperationException("Managed deployment service loop is owned by UeDtLauncher.Agent. Use service --once for a manual cycle.");
            var managedResponse = await new ManagedAgentClient().SendAsync("service-run", managedConfig.ProjectId);
            PrintAgentResponse(managedResponse);
            return managedResponse.Success ? 0 : 1;
        }
        int? interval = null;
        var intervalArg = Get(args, "--interval");
        if (!string.IsNullOrWhiteSpace(intervalArg))
        {
            if (!int.TryParse(intervalArg, out var parsed) || parsed <= 0) throw new ArgumentException("--interval must be a positive number of seconds.");
            interval = parsed;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // shut the loop down cleanly instead of killing the process
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // systemd stop sends SIGTERM; the source may already be disposed on normal exit.
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        return await ServiceRunner.RunAsync(configPath, interval, once, cts.Token);
    }

    private static async Task<int> RollbackAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var config = await LauncherPaths.LoadResolvedAsync(configPath);
        if (config.IsManagedDeployment)
        {
            var managedResponse = await new ManagedAgentClient().SendAsync("rollback", config.ProjectId);
            PrintAgentResponse(managedResponse);
            return managedResponse.Success ? 0 : 1;
        }
        var backups = BackupManager.List(config.BackupDir);

        if (Has(args, "--list"))
        {
            if (backups.Count == 0)
            {
                Console.WriteLine("No backups found in: " + config.BackupDir);
                return 0;
            }

            Console.WriteLine("Available backups (newest first):");
            foreach (var (backupRoot, info) in backups)
            {
                var name = Path.GetFileName(backupRoot);
                Console.WriteLine($"  {name}  previous: {info?.PreviousVersion ?? "?"}  updated to: {info?.NewVersion ?? "?"}  created: {info?.CreatedAtUtc ?? "?"}");
            }

            return 0;
        }

        var requested = Get(args, "--backup");
        var selected = string.IsNullOrWhiteSpace(requested)
            ? backups.FirstOrDefault()
            : backups.FirstOrDefault(backup => string.Equals(Path.GetFileName(backup.BackupRoot), requested, StringComparison.Ordinal));

        if (selected.BackupRoot is null)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(requested)
                ? "No backups found in: " + config.BackupDir
                : $"Backup not found: {requested}. Use 'rollback --list' to see available backups.");
            return 1;
        }

        var fileLogger = new FileLogger(config.LogDir);
        Console.WriteLine($"Rolling back using backup {Path.GetFileName(selected.BackupRoot)}...");
        await BackupManager.RestoreAsync(selected.BackupRoot, config.InstallDir, config.InstalledManifestPath, config.InstallStatePath, message =>
        {
            Console.WriteLine("[Rollback] " + message);
            fileLogger.Log("Rollback", message);
        });
        Console.WriteLine("Rollback completed.");
        return 0;
    }

    private static async Task ResolveCatalogForCliAsync(LauncherConfig config, Action<string, string, double?>? log = null)
    {
        log ??= (stage, message, percent) =>
            Console.WriteLine(percent.HasValue ? $"[{stage}] {message} ({percent:0}%)" : $"[{stage}] {message}");
        using var httpClient = SecureHttpClientFactory.Create(config);
        await CatalogResolver.ResolveAsync(config, httpClient, log);
    }

}
