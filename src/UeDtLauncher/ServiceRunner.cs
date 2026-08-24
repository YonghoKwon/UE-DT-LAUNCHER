using System.Diagnostics;

namespace UeDtLauncher;

/// <summary>
/// Headless update loop for unattended machines (e.g. pixel streaming servers):
/// periodically checks the catalog/manifest, stops the managed app when a new version
/// is available, applies the update, and restarts the app.
/// </summary>
public static class ServiceRunner
{
    public static async Task<int> RunAsync(string configPath, int? intervalSecondsOverride, bool once, CancellationToken cancellationToken)
    {
        var bootstrap = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
        var logger = new FileLogger(bootstrap.LogDir);
        var intervalSeconds = Math.Max(15, intervalSecondsOverride ?? bootstrap.ServiceMode?.IntervalSeconds ?? 300);
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var onceExitCode = 0;

        LogBoth(logger, "Service", once
            ? "Service mode: single check (--once)."
            : $"Service mode started. Checking for updates every {intervalSeconds}s. Press Ctrl+C to stop.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(configPath, logger, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogBoth(logger, "Service", "Check failed: " + ex.GetBaseException().Message);
                onceExitCode = 1;
            }

            if (once) break;

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        LogBoth(logger, "Service", "Service mode stopped.");
        return once ? onceExitCode : 0;
    }

    private static async Task TickAsync(string configPath, FileLogger logger, CancellationToken cancellationToken)
    {
        // Re-read the config each tick so server-side changes (channel, version policy) apply without restart.
        var config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
        var service = config.ServiceMode ?? new ServiceModeConfig();
        ValidateServiceConfig(service);
        config.RepairMode = false;

        using var http = SecureHttpClientFactory.Create(config);
        await CatalogResolver.ResolveAsync(config, http, (stage, message, _) => logger.Log(stage, message), cancellationToken);

        using var engine = new LauncherEngine(config, progress: null, logger);
        using var prepared = await engine.PrepareAsync(cancellationToken);
        var remoteVersion = prepared.RemoteManifest.Version;
        var state = prepared.LocalState;
        var appRunning = TryGetRunningApp(config, service, out var appProcess);
        var updateAvailable = IsUpdateAvailable(state, remoteVersion)
                              || prepared.HasLiveChanges
                              || prepared.PackagePreparationRequested;

        if (!updateAvailable && (appRunning || !service.AutoRestartApp))
        {
            logger.Log("Service", $"Up to date ({remoteVersion}); app running: {appRunning}.");
            return;
        }

        var stoppedPreviousApp = false;
        string? committedBackupRoot = null;
        if (prepared.HasLiveChanges && appRunning && appProcess is not null)
        {
            LogBoth(logger, "Service", $"Prepared update: {state?.Version ?? "(none)"} -> {remoteVersion}. Stopping app (pid {appProcess.Id})...");
            StopApp(appProcess, logger);
            stoppedPreviousApp = true;
        }
        else if (!updateAvailable && !appRunning)
        {
            LogBoth(logger, "Service", "App is not running. Relaunching...");
        }

        try
        {
            committedBackupRoot = await engine.CommitPreparedAsync(prepared, cancellationToken);
            var mustStartApp = service.AutoRestartApp && (!appRunning || prepared.HasLiveChanges);
            if (mustStartApp)
            {
                engine.LaunchPrepared(prepared);
                await WaitForHealthyAppAsync(config, service, logger, cancellationToken);
            }

            LogBoth(logger, "Service", updateAvailable
                ? $"Update to {remoteVersion} completed." + (mustStartApp ? " App restarted." : string.Empty)
                : "App relaunched.");
        }
        catch (ServiceHealthCheckException) when (!service.RollbackOnHealthCheckFailure)
        {
            throw;
        }
        catch
        {
            await RestorePreviousServiceAsync(
                config,
                service,
                engine,
                prepared,
                committedBackupRoot,
                stoppedPreviousApp,
                logger);
            throw;
        }
    }

    internal static bool IsUpdateAvailable(InstallState? state, string remoteVersion) =>
        state is null || !string.Equals(state.Version, remoteVersion, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateServiceConfig(ServiceModeConfig service)
    {
        if (string.IsNullOrWhiteSpace(service.HealthCheckUrl)) return;
        if (!Uri.TryCreate(service.HealthCheckUrl, UriKind.Absolute, out var healthUri)
            || healthUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("serviceMode.healthCheckUrl must be an absolute HTTP(S) URL.");
        }
    }

    internal static bool TryGetRunningApp(LauncherConfig config, ServiceModeConfig service, out Process? process)
    {
        process = null;

        if (File.Exists(config.AppPidPath))
        {
            try
            {
                var info = JsonFiles.ReadAsync<AppPidInfo>(config.AppPidPath).GetAwaiter().GetResult();
                var candidate = Process.GetProcessById(info.Pid);
                var expectedName = Path.GetFileNameWithoutExtension(info.EntryPoint);
                // Guard against pid reuse: only accept the pid when the process name still matches.
                if (!candidate.HasExited && (string.IsNullOrEmpty(expectedName) || candidate.ProcessName.Contains(expectedName, StringComparison.OrdinalIgnoreCase) || expectedName.Contains(candidate.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    process = candidate;
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // No process with that pid.
            }
            catch (Exception)
            {
                // Unreadable pid file; fall through to the name-based lookup.
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ProcessName))
        {
            var byName = Process.GetProcessesByName(service.ProcessName).FirstOrDefault(candidate => !candidate.HasExited);
            if (byName is not null)
            {
                process = byName;
                return true;
            }
        }

        return false;
    }

    internal static void StopApp(Process process, FileLogger logger)
    {
        try
        {
            if (process.CloseMainWindow() && process.WaitForExit(10_000))
            {
                logger.Log("Service", "App exited gracefully.");
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return; // already exited
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return; // exited between the checks
        }

        if (!process.WaitForExit(15_000))
        {
            throw new InvalidOperationException("App process did not stop; skipping this update cycle.");
        }

        logger.Log("Service", "App process terminated.");
    }

    private static async Task WaitForHealthyAppAsync(
        LauncherConfig config,
        ServiceModeConfig service,
        FileLogger logger,
        CancellationToken cancellationToken)
    {
        var grace = TimeSpan.FromSeconds(Math.Clamp(service.StartupGraceSeconds, 0, 300));
        if (grace > TimeSpan.Zero) await Task.Delay(grace, cancellationToken);

        if (string.IsNullOrWhiteSpace(service.HealthCheckUrl))
        {
            if (!TryGetRunningApp(config, service, out _))
                throw new ServiceHealthCheckException("The application exited during its startup grace period.");
            logger.Log("Service", "Application process remained healthy through the startup grace period.");
            return;
        }

        var healthUri = new Uri(service.HealthCheckUrl, UriKind.Absolute);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(service.HealthCheckTimeoutSeconds, 1, 900));
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Min(10, timeout.TotalSeconds)) };
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetRunningApp(config, service, out _))
                throw new ServiceHealthCheckException("The application exited before its health check passed.");

            try
            {
                using var response = await healthClient.GetAsync(healthUri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    logger.Log("Service", $"Health check passed: {healthUri}");
                    return;
                }
                lastError = new HttpRequestException($"Health endpoint returned {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new ServiceHealthCheckException(
            $"Health check did not pass within {timeout.TotalSeconds:0} seconds.",
            lastError);
    }

    private static async Task RestorePreviousServiceAsync(
        LauncherConfig config,
        ServiceModeConfig service,
        LauncherEngine engine,
        PreparedLauncherUpdate prepared,
        string? committedBackupRoot,
        bool stoppedPreviousApp,
        FileLogger logger)
    {
        if (committedBackupRoot is not null)
        {
            if (TryGetRunningApp(config, service, out var newProcess) && newProcess is not null)
            {
                StopApp(newProcess, logger);
            }

            LogBoth(logger, "Service", "Post-commit failure detected. Restoring the previous installation...");
            await BackupManager.RestoreAsync(
                committedBackupRoot,
                config.InstallDir,
                config.InstalledManifestPath,
                config.InstallStatePath,
                message => logger.Log("Rollback", message),
                CancellationToken.None);
        }

        if ((stoppedPreviousApp || committedBackupRoot is not null) && prepared.LocalManifest is not null)
        {
            LogBoth(logger, "Service", "Restarting the previous application after update failure...");
            engine.LaunchPrevious(prepared);
        }
    }

    private static void LogBoth(FileLogger logger, string stage, string message)
    {
        Console.WriteLine($"[{stage}] {message}");
        logger.Log(stage, message);
    }
}

internal sealed class ServiceHealthCheckException : Exception
{
    internal ServiceHealthCheckException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
