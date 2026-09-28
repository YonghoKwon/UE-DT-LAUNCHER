namespace UeDtLauncher;

public static class RuntimeRecoveryRequest
{
    public static void Validate(LauncherConfig config, bool confirm, string? serviceVersion)
    {
        if (serviceVersion is null) return;
        if (!confirm) throw new InvalidOperationException("Service selection requires explicit stopped confirmation.");
        ReleaseSidecar.Segment(serviceVersion);
        var expected = config.SelectedRelease?.Version;
        if (expected is null && File.Exists(config.InstalledManifestPath))
            expected = JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult().Version;
        if (expected != serviceVersion) throw new InvalidOperationException("Service version must match the exact installed release selection.");
    }
}
