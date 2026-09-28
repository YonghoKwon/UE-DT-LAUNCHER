using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public enum RuntimeState { Quiescent, LaunchPending, Running, Unknown }
public sealed record RuntimeObservation(RuntimeState State, string Code, string Message, string? AttemptId = null);
public sealed record RuntimeLaunchTicket(string AttemptId, string Token, string InstallationId, string HostExecutable);
public sealed class RuntimeRecord
{
    public int SchemaVersion { get; set; } = 1;
    public string InstallationId { get; set; } = "";
    public RuntimeState State { get; set; }
    public string? AttemptId { get; set; }
    public string? TokenHash { get; set; }
    public RuntimeIdentity? Requester { get; set; }
    public RuntimeIdentity? Host { get; set; }
    public string? ManifestHash { get; set; }
    public string? EntryPoint { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? Origin { get; set; }
    public int? PayloadPid { get; set; }
}
public sealed class RuntimeBlockedException(RuntimeObservation observation) : InvalidOperationException(observation.Message)
{
    public RuntimeObservation Observation { get; } = observation;
}

public static class RuntimeStore
{
    public static string RecordPath(LauncherConfig config) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))!, "runtime-state.json");
    public static string InstallationId(LauncherConfig config)
    {
        var path = Path.GetFullPath(config.InstallDir).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    }
    internal static RuntimeRecord? Read(LauncherConfig config)
    {
        var path = RecordPath(config);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Runtime record exceeds limit.");
        var value = JsonSerializer.Deserialize<RuntimeRecord>(File.ReadAllText(path), JsonFiles.Options) ?? throw new InvalidDataException("Invalid runtime record.");
        if (value.SchemaVersion != 1 || value.InstallationId != InstallationId(config) || !Enum.IsDefined(value.State)) throw new InvalidDataException("Runtime record identity mismatch.");
        return value;
    }
    internal static void Write(LauncherConfig config, RuntimeRecord record) => JsonFiles.WriteAsync(RecordPath(config), record).GetAwaiter().GetResult();

    public static RuntimeObservation Observe(LauncherConfig config)
    {
        try
        {
            var record = Read(config);
            if (record is null)
            {
                var legacy = File.Exists(config.AppPidPath) || File.Exists(config.InstalledManifestPath) || File.Exists(config.InstallStatePath) ||
                    (Directory.Exists(config.InstallDir) && Directory.EnumerateFileSystemEntries(config.InstallDir).Any());
                return legacy ? Unknown("legacy-runtime") : new(RuntimeState.Quiescent, "new-install", "No prior installation runtime is recorded.");
            }
            if (record.State == RuntimeState.Quiescent) return new(record.State, "quiescent", "프로그램이 종료되어 변경할 수 있습니다.", record.AttemptId);
            if (record.State == RuntimeState.Running && record.Host is not null && RuntimeIdentities.StillMatches(record.Host))
                return new(RuntimeState.Running, "app-running", "실행 중—프로그램을 종료한 뒤 다시 시도해 주세요.", record.AttemptId);
            if (record.State == RuntimeState.LaunchPending) return new(record.State, "launch-pending", "프로그램 실행 확인 중입니다. 변경을 중단했습니다.", record.AttemptId);
            return Unknown("runtime-unknown", record.AttemptId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return Unknown("runtime-record-invalid"); }
    }
    private static RuntimeObservation Unknown(string code, string? attempt = null) => new(RuntimeState.Unknown, code, "실행 상태 확인이 필요합니다. 관리자 점검 전에는 파일을 변경하지 않습니다.", attempt);
    internal static void RequireQuiescent(LauncherConfig config)
    {
        var observation = Observe(config);
        if (observation.State != RuntimeState.Quiescent) throw new RuntimeBlockedException(observation);
    }
    internal static void Initialize(LauncherConfig config)
    {
        if (Read(config) is null) Write(config, new() { InstallationId = InstallationId(config), State = RuntimeState.Quiescent, Origin = "new-install" });
    }

    public static RuntimeLaunchTicket Begin(LauncherConfig config, RuntimeIdentity requester, string hostExecutable)
    {
        using var lease = InstallationMutationLease.Acquire(config);
        var manifest = JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult();
        LauncherEngine.ValidateManifest(manifest, config);
        var entry = SafePath.ResolveInsideChecked(config.InstallDir, manifest.EntryPoint);
        if (!File.Exists(entry)) throw new FileNotFoundException("Installed entry point is missing.", entry);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var attempt = Guid.NewGuid().ToString("N");
        Write(config, new()
        {
            InstallationId = InstallationId(config), State = RuntimeState.LaunchPending, AttemptId = attempt,
            TokenHash = Hash(token), Requester = requester, ManifestHash = Hashing.Sha256FileAsync(config.InstalledManifestPath).GetAwaiter().GetResult(),
            EntryPoint = entry, Arguments = config.LaunchArguments ?? [], Origin = "supervised"
        });
        return new(attempt, token, InstallationId(config), Path.GetFullPath(hostExecutable));
    }

    public static RuntimeHostRequest Attach(LauncherConfig config, RuntimeLaunchTicket ticket, RuntimeIdentity peer, string expectedHost)
    {
        using var gate = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        var record = Authenticate(config, ticket);
        if (record.State is not (RuntimeState.LaunchPending or RuntimeState.Running) || record.Requester?.Owner != peer.Owner ||
            !SafePath.FileSystemComparer.Equals(Path.GetFullPath(expectedHost), peer.Executable) || !RuntimeIdentities.StillMatches(peer))
            throw new UnauthorizedAccessException("Runtime host identity could not be verified.");
        if (record.Host is not null && record.Host != peer) throw new UnauthorizedAccessException("Runtime ticket already belongs to another host.");
        if (Hashing.Sha256FileAsync(config.InstalledManifestPath).GetAwaiter().GetResult() != record.ManifestHash)
            throw new InvalidDataException("Installed manifest changed during launch.");
        record.Host = peer; Write(config, record);
        return new(record.EntryPoint!, Path.GetDirectoryName(record.EntryPoint!)!, record.Arguments);
    }

    public static void Report(LauncherConfig config, RuntimeLaunchTicket ticket, RuntimeIdentity peer, bool completed, int? payloadPid = null)
    {
        using var gate = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        var record = Authenticate(config, ticket);
        if (record.Host != peer || !RuntimeIdentities.StillMatches(peer)) throw new UnauthorizedAccessException("Runtime report peer mismatch.");
        if (record.State == RuntimeState.Quiescent && completed) return; // lost ACK: idempotent only for this authenticated host/attempt
        if (record.State is not (RuntimeState.LaunchPending or RuntimeState.Running)) throw new InvalidOperationException("Runtime attempt cannot transition.");
        if (completed && record.State != RuntimeState.Running) throw new InvalidOperationException("Unstarted runtime cannot report completion.");
        record.State = completed ? RuntimeState.Quiescent : RuntimeState.Running; record.PayloadPid = payloadPid ?? record.PayloadPid;
        record.Origin = completed ? "supervisor-completed" : "supervised"; Write(config, record);
    }
    private static RuntimeRecord Authenticate(LauncherConfig config, RuntimeLaunchTicket ticket)
    {
        var record = Read(config) ?? throw new InvalidDataException("Runtime attempt is missing.");
        if (ticket.InstallationId != record.InstallationId || ticket.AttemptId != record.AttemptId || ticket.Token.Length != 64 ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(ticket.Token)), Encoding.ASCII.GetBytes(record.TokenHash ?? "")))
            throw new UnauthorizedAccessException("Invalid runtime ticket.");
        return record;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static RuntimeObservation Recover(LauncherConfig config, RuntimeIdentity actor, bool confirm)
    {
        using var gate = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        if (config.IsManagedDeployment && !actor.Administrator) throw new UnauthorizedAccessException("Managed runtime recovery requires a local administrator.");
        RuntimeRecord? record = null;
        try { record = Read(config); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        if (record?.Host is not null && RuntimeIdentities.StillMatches(record.Host)) throw new RuntimeBlockedException(Observe(config));
        if (!config.IsManagedDeployment && record?.Requester is not null && record.Requester.Owner != actor.Owner) throw new UnauthorizedAccessException("Runtime belongs to another owner.");
        if (!confirm) return Observe(config);
        // Explicit operator maintenance acknowledgement, NOT OS-proven family termination.
        var path = RecordPath(config);
        if (File.Exists(path)) File.Copy(path, path + ".before-recovery-" + Guid.NewGuid().ToString("N"), false);
        Write(config, new() { InstallationId = InstallationId(config), State = RuntimeState.Quiescent, Origin = "operator-confirmed", Requester = actor });
        return Observe(config);
    }
}

public sealed class InstallationMutationLease : IDisposable
{
    private readonly SingleInstanceLock gate;
    private readonly string installationId;
    private bool disposed;
    private InstallationMutationLease(SingleInstanceLock gate, string installationId) { this.gate = gate; this.installationId = installationId; }
    public static InstallationMutationLease Acquire(LauncherConfig config)
    {
        var gate = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        try { RuntimeStore.RequireQuiescent(config); RuntimeStore.Initialize(config); return new(gate, RuntimeStore.InstallationId(config)); }
        catch { gate.Dispose(); throw; }
    }
    internal void Validate(LauncherConfig config)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (installationId != RuntimeStore.InstallationId(config)) throw new InvalidOperationException("Mutation lease belongs to another installation.");
        RuntimeStore.RequireQuiescent(config);
    }
    public void Dispose() { if (!disposed) { disposed=true; gate.Dispose(); } }
}
