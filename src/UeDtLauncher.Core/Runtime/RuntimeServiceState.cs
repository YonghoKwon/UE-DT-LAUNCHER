namespace UeDtLauncher;

public static class RuntimeServiceState
{
    private sealed record ActiveSelection(string InstallationId, string Version);
    private static string Root(LauncherConfig config)
    {
        var parts = new[] { config.ProjectId ?? "default", config.Environment, config.Channel, config.TargetPlatform };
        foreach (var part in parts) ReleaseSidecar.Segment(part);
        return SafePath.ResolveInside(Path.GetFullPath(config.StateRootDir), "service/" + string.Join('/', parts));
    }
    public static IDisposable Lock(LauncherConfig config) => SingleInstanceLock.Acquire(Path.Combine(Root(config), "service.lock"));
    public static void RequireSelection(LauncherConfig config, string version)
    {
        var path = Path.Combine(Root(config), "active.json");
        if (File.Exists(Path.Combine(Root(config), "failure.json"))) throw Blocked("service-recovery-required");
        if (File.Exists(path))
        {
            var active = JsonFiles.ReadAsync<ActiveSelection>(path).GetAwaiter().GetResult();
            if (active.InstallationId != RuntimeStore.InstallationId(config) || active.Version != version) throw Blocked("service-transition-required");
            return;
        }
        if (config.SelectedRelease is not null)
        {
            var track = SafePath.ResolveInside(config.StateRootDir, string.Join('/', config.SelectedRelease.ProjectId, config.SelectedRelease.Environment, config.SelectedRelease.Channel));
            if (Directory.Exists(track) && Directory.EnumerateFiles(track, "installed-manifest.json", SearchOption.AllDirectories)
                .Any(p => !SafePath.FileSystemComparer.Equals(Path.GetFullPath(p), Path.GetFullPath(config.InstalledManifestPath)))) throw Blocked("service-selection-required");
        }
        if (File.Exists(config.InstalledManifestPath) && JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult().Version != version)
            throw Blocked("service-transition-required");
        JsonFiles.WriteAsync(path, new ActiveSelection(RuntimeStore.InstallationId(config), version)).GetAwaiter().GetResult();
    }
    public static void ConfirmSelection(LauncherConfig config, RuntimeIdentity actor, string version)
    {
        if (config.IsManagedDeployment && !actor.Administrator) throw new UnauthorizedAccessException("Service selection requires a local administrator.");
        using var gate = Lock(config);
        RuntimeStore.RequireQuiescent(config);
        var failure = Path.Combine(Root(config), "failure.json");
        if (File.Exists(failure)) File.Move(failure, failure + ".acknowledged-" + Guid.NewGuid().ToString("N"));
        JsonFiles.WriteAsync(Path.Combine(Root(config), "active.json"), new ActiveSelection(RuntimeStore.InstallationId(config), version)).GetAwaiter().GetResult();
    }
    public static void RecordFailure(LauncherConfig config, string? backup) => JsonFiles.WriteAsync(Path.Combine(Root(config), "failure.json"),
        new { state="manual-recovery-required", installationId=RuntimeStore.InstallationId(config), backup, atUtc=DateTimeOffset.UtcNow }).GetAwaiter().GetResult();
    private static RuntimeBlockedException Blocked(string code) => new(new(RuntimeState.Unknown, code, "무인 서비스의 버전 전환/복구는 관리자 확인이 필요합니다. 자동 종료하거나 다른 버전을 시작하지 않습니다."));
}
