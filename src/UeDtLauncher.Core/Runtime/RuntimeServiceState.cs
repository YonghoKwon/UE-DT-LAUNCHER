using System.Text.Json;

namespace UeDtLauncher;

public enum ServiceRuntimePhase { Ready, StartupHealthPending, NeedsReview }
public sealed record ServiceRuntimeSnapshot(int SchemaVersion, string InstallationId, string Version,
    ReleaseSelection? Release, ServiceRuntimePhase Phase, string Generation, string? Backup = null,
    string? Failure = null, RuntimeIdentity? AcknowledgedBy = null);

public static class RuntimeServiceState
{
    private sealed record LegacySelection(string InstallationId, string Version);
    private static string Root(LauncherConfig config)
    {
        var parts = new[] { config.ProjectId ?? "default", config.Environment, config.Channel, config.TargetPlatform };
        foreach (var part in parts) ReleaseSidecar.Segment(part);
        return SafePath.ResolveInside(Path.GetFullPath(config.StateRootDir), "service/" + string.Join('/', parts));
    }
    internal static string SnapshotPath(LauncherConfig config) => Path.Combine(Root(config), "service-state.json");
    public static IDisposable Lock(LauncherConfig config) => SingleInstanceLock.Acquire(Path.Combine(Root(config), "service.lock"));
    private static bool LegacyExists(LauncherConfig config) => File.Exists(Path.Combine(Root(config),"active.json")) || File.Exists(Path.Combine(Root(config),"failure.json"));
    internal static ServiceRuntimeSnapshot? Read(LauncherConfig config)
    {
        var path=SnapshotPath(config);
        if (!File.Exists(path)) return null;
        try
        {
            if(new FileInfo(path).Length>64*1024) throw new InvalidDataException();
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            RuntimeStore.RequireFields(json.RootElement,"schemaVersion","installationId","version","phase","generation");
            var value=json.RootElement.Deserialize<ServiceRuntimeSnapshot>(JsonFiles.Options)!;
            if(value.SchemaVersion!=2 || !Enum.IsDefined(value.Phase) || !Guid.TryParseExact(value.Generation,"N",out _) ||
                value.InstallationId is not { Length:64 }) throw new InvalidDataException();
            ReleaseSidecar.Segment(value.Version);
            if(value.Release is not null) { value.Release.Validate(); if(value.Release.Version!=value.Version) throw new InvalidDataException(); }
            return value;
        }
        catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException or ArgumentException)
        { throw Blocked("service-state-invalid"); }
    }
    private static void Write(LauncherConfig config, ServiceRuntimeSnapshot value) => RuntimeStatePersistence.Write(SnapshotPath(config), value);
    private static ServiceRuntimeSnapshot New(LauncherConfig config,string version,ServiceRuntimePhase phase) =>
        new(2,RuntimeStore.InstallationId(config),version,config.SelectedRelease,phase,Guid.NewGuid().ToString("N"));
    internal static void RequireLaunch(LauncherConfig config, bool serviceStart)
    {
        var snapshot=Read(config);
        if(snapshot is null) { if(LegacyExists(config)) throw Blocked("legacy-service-review"); return; }
        if(snapshot.InstallationId!=RuntimeStore.InstallationId(config) ||
            snapshot.Release!=config.SelectedRelease ||
            snapshot.Phase!=(serviceStart ? ServiceRuntimePhase.StartupHealthPending : ServiceRuntimePhase.Ready))
            throw Blocked("service-recovery-required");
        if(File.Exists(config.InstalledManifestPath) && JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult().Version!=snapshot.Version)
            throw Blocked("service-transition-required");
    }
    // Caller owns the lane lock before acquiring installation locks.
    public static void RequireSelection(LauncherConfig config,string version)
    {
        var value=Read(config);
        if(value is not null)
        {
            if(value.Phase!=ServiceRuntimePhase.Ready) throw Blocked("service-recovery-required");
            if(value.InstallationId!=RuntimeStore.InstallationId(config) || value.Version!=version || value.Release!=config.SelectedRelease)
                throw Blocked("service-transition-required");
            return;
        }
        if(LegacyExists(config)) throw Blocked("legacy-service-review");
        if(config.SelectedRelease is not null)
        {
            var track=SafePath.ResolveInside(config.StateRootDir,string.Join('/',config.SelectedRelease.ProjectId,config.SelectedRelease.Environment,config.SelectedRelease.Channel));
            if(Directory.Exists(track) && Directory.EnumerateFiles(track,"installed-manifest.json",SearchOption.AllDirectories)
                .Any(p=>!SafePath.FileSystemComparer.Equals(Path.GetFullPath(p),Path.GetFullPath(config.InstalledManifestPath))))
                throw Blocked("service-selection-required");
        }
        if(File.Exists(config.InstalledManifestPath) && JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult().Version!=version)
            throw Blocked("service-transition-required");
        Write(config,New(config,version,ServiceRuntimePhase.Ready));
    }
    internal static void Starting(LauncherConfig config,string? backup)
    {
        var value=Read(config) ?? throw Blocked("service-state-missing");
        Write(config,value with { Phase=ServiceRuntimePhase.StartupHealthPending,Backup=backup,Generation=Guid.NewGuid().ToString("N"),Failure=null });
    }
    internal static void Healthy(LauncherConfig config)
    {
        var value=Read(config) ?? throw Blocked("service-state-missing");
        if(value.Phase!=ServiceRuntimePhase.StartupHealthPending) throw Blocked("service-state-invalid");
        Write(config,value with { Phase=ServiceRuntimePhase.Ready,Generation=Guid.NewGuid().ToString("N") });
    }
    public static void RecordFailure(LauncherConfig config,string? backup)
    {
        var value=Read(config) ?? throw Blocked("service-state-missing");
        Write(config,value with { Phase=ServiceRuntimePhase.NeedsReview,Backup=backup,Failure="manual-recovery-required",Generation=Guid.NewGuid().ToString("N") });
    }
    private static LauncherConfig PreviousConfig(LauncherConfig target,string version,string id,ReleaseSelection? selection)
    {
        var previous=JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(target,JsonFiles.Options),JsonFiles.Options)!;
        previous.VersionedInstallRoot=target.VersionedInstallRoot;
        previous.SelectedRelease=target.SelectedRelease;
        if(target.SelectedRelease is not null)
        {
            selection ??= target.SelectedRelease with { Version=version };
            if(selection.ProjectId!=target.ProjectId || selection.Environment!=target.Environment ||
                selection.Channel!=target.Channel || selection.Platform!=target.TargetPlatform) throw Blocked("service-selection-invalid");
            VersionedReleasePaths.Bind(previous,selection);
        }
        if(RuntimeStore.InstallationId(previous)!=id) throw Blocked("service-previous-installation-unresolved");
        return previous;
    }
    public static RuntimeObservation ConfirmSelection(LauncherConfig config,RuntimeIdentity actor,string version)
    {
        RuntimeRecoveryRequest.Validate(config,true,version);
        RuntimeStore.ValidateRecoveryOwner(config,actor);
        using var lane=Lock(config);
        var current=Read(config);
        string previousVersion=current?.Version ?? version;
        LauncherConfig? previous=null;
        if(current is not null) previous=PreviousConfig(config,current.Version,current.InstallationId,current.Release);
        else if(LegacyExists(config))
        {
            var path=Path.Combine(Root(config),"active.json");
            if(!File.Exists(path)) throw Blocked("legacy-service-unresolved");
            using var json=JsonDocument.Parse(File.ReadAllText(path));
            RuntimeStore.RequireFields(json.RootElement,"installationId","version");
            var active=json.RootElement.Deserialize<LegacySelection>(JsonFiles.Options)!;
            previousVersion=active.Version;
            previous=PreviousConfig(config,active.Version,active.InstallationId,null);
        }
        var configs=new[] { previous,config }.OfType<LauncherConfig>().GroupBy(RuntimeStore.InstallationId)
            .Select(g=>g.First()).OrderBy(RuntimeStore.InstallationId,StringComparer.Ordinal).ToArray();
        var locks=new List<IDisposable>();
        try
        {
            foreach(var item in configs) locks.Add(SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(item)));
            if(previous is not null && RuntimeStore.InstallationId(previous)!=RuntimeStore.InstallationId(config))
                RuntimeStore.RequireQuiescent(previous);
            RuntimeStore.ValidateRecoveryTarget(config,actor);
            // Persist the barrier before either runtime acknowledgement or service selection can change.
            if(current is not null) File.Copy(SnapshotPath(config),SnapshotPath(config)+".before-recovery-"+Guid.NewGuid().ToString("N"),false);
            var barrier=current ?? New(previous ?? config,previousVersion,ServiceRuntimePhase.NeedsReview);
            Write(config,barrier with { Phase=ServiceRuntimePhase.NeedsReview,Failure="maintenance-in-progress",Generation=Guid.NewGuid().ToString("N") });
            var result=RuntimeStore.RecoverUnderLock(config,actor);
            Write(config,New(config,version,ServiceRuntimePhase.Ready) with { AcknowledgedBy=actor,Backup=current?.Backup });
            return result;
        }
        finally { for(var i=locks.Count-1;i>=0;i--) locks[i].Dispose(); }
    }
    private static RuntimeBlockedException Blocked(string code) => new(new(RuntimeState.Unknown,code,"무인 서비스의 버전 전환/복구는 관리자 확인이 필요합니다. 자동 종료하거나 다른 버전을 시작하지 않습니다."));
}
