using System.Diagnostics;
using System.Text.Json;

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
        return 0;
    }

    private static async Task TickAsync(string configPath, FileLogger logger, CancellationToken cancellationToken)
    {
        // Re-read the config each tick so server-side changes (channel, version policy) apply without restart.
        var config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
        var service = config.ServiceMode ?? new ServiceModeConfig();
        config.RepairMode = false;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
        await CatalogResolver.ResolveAsync(config, http, (stage, message, _) => logger.Log(stage, message), cancellationToken);

        // Version probe only; the engine re-downloads and verifies (incl. signatures) before applying.
        var manifestJson = await http.GetStringAsync(config.ManifestUrl, cancellationToken);
        var remote = JsonSerializer.Deserialize<LauncherManifest>(manifestJson, JsonFiles.Options)
            ?? throw new InvalidOperationException("Remote manifest JSON was empty or invalid.");

        InstallState? state = null;
        if (File.Exists(config.InstallStatePath))
        {
            try
            {
                state = await JsonFiles.ReadAsync<InstallState>(config.InstallStatePath, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.Log("Service", "Install state unreadable, treating as not installed: " + ex.Message);
            }
        }

        var appRunning = TryGetRunningApp(config, service, out var appProcess);
        var updateAvailable = IsUpdateAvailable(state, remote.Version);

        if (!updateAvailable && (appRunning || !service.AutoRestartApp))
        {
            logger.Log("Service", $"Up to date ({remote.Version}); app running: {appRunning}.");
            return;
        }

        if (updateAvailable && appRunning && appProcess is not null)
        {
            LogBoth(logger, "Service", $"Update available: {state?.Version ?? "(none)"} -> {remote.Version}. Stopping app (pid {appProcess.Id})...");
            StopApp(appProcess, logger);
        }
        else if (!updateAvailable)
        {
            LogBoth(logger, "Service", "App is not running. Relaunching...");
        }

        config.LaunchAfterUpdate = service.AutoRestartApp;
        using var engine = new LauncherEngine(config, progress: null, logger);
        await engine.RunAsync(cancellationToken);
        LogBoth(logger, "Service", updateAvailable
            ? $"Update to {remote.Version} completed." + (service.AutoRestartApp ? " App restarted." : string.Empty)
            : "App relaunched.");
    }

    internal static bool IsUpdateAvailable(InstallState? state, string remoteVersion) =>
        state is null || !string.Equals(state.Version, remoteVersion, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRunningApp(LauncherConfig config, ServiceModeConfig service, out Process? process)
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

    private static void StopApp(Process process, FileLogger logger)
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

    private static void LogBoth(FileLogger logger, string stage, string message)
    {
        Console.WriteLine($"[{stage}] {message}");
        logger.Log(stage, message);
    }
}
