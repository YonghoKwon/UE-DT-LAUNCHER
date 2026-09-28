namespace UeDtLauncher;

/// <summary>Read/check loop with explicit quiescence. No guessed process termination or automatic version handoff.</summary>
public static class ServiceRunner
{
    public static async Task<int> RunAsync(string configPath, int? intervalSecondsOverride, bool once, CancellationToken cancellationToken)
    {
        var bootstrap = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
        var logger = new FileLogger(bootstrap.LogDir);
        var interval = TimeSpan.FromSeconds(Math.Max(15, intervalSecondsOverride ?? bootstrap.ServiceMode?.IntervalSeconds ?? 300));
        var exit = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await TickAsync(configPath, logger, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.Log("Service", DiagnosticRedactor.Redact(ex.Message)); Console.WriteLine("[Service] " + DiagnosticRedactor.Redact(ex.Message)); exit = 1; }
            if (once) break;
            try { await Task.Delay(interval, cancellationToken); } catch (OperationCanceledException) { break; }
        }
        return once ? exit : 0;
    }

    private static async Task TickAsync(string path, FileLogger logger, CancellationToken token)
    {
        var config = await LauncherPaths.LoadResolvedAsync(path, token);
        var service = config.ServiceMode ?? new ServiceModeConfig();
        ValidateServiceConfig(service);
        using var http = SecureHttpClientFactory.Create(config);
        await CatalogResolver.ResolveAsync(config, http, cancellationToken: token);
        var remote = await ManifestDownloader.DownloadAsync(config, http, cancellationToken: token);
        using var serviceGate = RuntimeServiceState.Lock(config);
        RuntimeStore.RequireQuiescent(config);
        RuntimeServiceState.RequireSelection(config, remote.Manifest.Version);
        config.LaunchAfterUpdate = false; config.RepairMode = false;
        string? backup;
        using (var engine = new LauncherEngine(config, null, logger))
        using (var prepared = await engine.PrepareAsync(token))
        {
            if (prepared.RemoteManifest.Version != remote.Manifest.Version) throw new InvalidDataException("Service release changed during preparation.");
            backup = await engine.CommitPreparedAsync(prepared, token);
        }
        if (!service.AutoRestartApp) return;
        try
        {
            // This helper runs as the same service account and owns service runtime state.
            // Do not self-call IPC while the Agent is servicing service-run.
            _ = await RuntimeLauncher.LaunchServiceAsync(config, token);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(service.StartupGraceSeconds, 0, 300)), token);
            if (RuntimeStore.Observe(config).State != RuntimeState.Running) throw new ServiceHealthCheckException("Application did not remain running.");
            if (!string.IsNullOrWhiteSpace(service.HealthCheckUrl))
            {
                using var health = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(service.HealthCheckTimeoutSeconds, 1, 900)) };
                using var response = await health.GetAsync(service.HealthCheckUrl, token);
                if (!response.IsSuccessStatusCode) throw new ServiceHealthCheckException("Health check failed; manual recovery is required.");
            }
            logger.Log("Service", "Supervised service application started. Active runtimes block installation changes.");
        }
        catch
        {
            RuntimeServiceState.RecordFailure(config, backup);
            // Never restore files or restart another app while runtime status might be active/unknown.
            throw;
        }
    }

    internal static bool IsUpdateAvailable(InstallState? state, string remoteVersion) =>
        state is null || !string.Equals(state.Version, remoteVersion, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateServiceConfig(ServiceModeConfig service)
    {
        if (string.IsNullOrWhiteSpace(service.HealthCheckUrl)) return;
        if (!Uri.TryCreate(service.HealthCheckUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("serviceMode.healthCheckUrl must be an absolute HTTP(S) URL.");
    }
}
internal sealed class ServiceHealthCheckException : Exception
{
    internal ServiceHealthCheckException(string message, Exception? innerException = null) : base(message, innerException) { }
}
