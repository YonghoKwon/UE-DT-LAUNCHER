using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public enum RuntimeState { Quiescent, LaunchPending, Running, Unknown }
public sealed record RuntimeObservation(RuntimeState State, string Code, string Message, string? AttemptId = null);
public sealed record RuntimeLaunchTicket(string AttemptId, string Token, string InstallationId, string HostExecutable, bool RequiresRuntimeData = false);
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
    public RuntimeDataPlan? RuntimeData { get; set; }
    public string? Origin { get; set; }
    public int? PayloadPid { get; set; }
    public RuntimeIdentity? PayloadIdentity { get; set; }
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
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        RequireFields(document.RootElement, "schemaVersion", "installationId", "state", "origin");
        if (document.RootElement.TryGetProperty("runtimeData", out var data) && data.ValueKind != JsonValueKind.Null)
        {
            RequireFields(data, "adapter", "policy", "releaseId", "attemptId", "owner", "protectedDirectories", "installationId");
            if (!data.TryGetProperty("rootDirectory", out var root) || root.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Runtime root selection must be explicit.");
        }
        var value = document.RootElement.Deserialize<RuntimeRecord>(JsonFiles.Options) ?? throw new InvalidDataException("Invalid runtime record.");
        if (value.SchemaVersion != 1 || value.InstallationId != InstallationId(config) || !Enum.IsDefined(value.State)) throw new InvalidDataException("Runtime record identity mismatch.");
        ValidateRecord(value);
        if (value.RuntimeData is { } plan) RuntimeDataPolicy.ValidateBinding(config, plan);
        return value;
    }
    internal static void RequireFields(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("State must be an object.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate state field.");
            if (property.Value.ValueKind == JsonValueKind.Object) RequireFields(property.Value);
        }
        foreach (var name in required)
            if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException("Required state field is absent.");
    }
    private static bool ValidIdentity(RuntimeIdentity? identity) => identity is { Pid: > 0 } &&
        !string.IsNullOrWhiteSpace(identity.CreationId) && !string.IsNullOrWhiteSpace(identity.Owner) &&
        !string.IsNullOrWhiteSpace(identity.Session) && !string.IsNullOrWhiteSpace(identity.Executable) && Path.IsPathFullyQualified(identity.Executable);
    private static bool HashValue(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidateRecord(RuntimeRecord value)
    {
        if (value.RuntimeData is { } plan)
        {
            RuntimeDataPolicy.ValidatePlan(plan);
            if (plan.AttemptId != value.AttemptId || plan.Owner != value.Requester?.Owner) throw new InvalidDataException("Runtime data identity mismatch.");
        }
        if (value.State == RuntimeState.Unknown) return;
        if (value.State == RuntimeState.Quiescent && value.Origin == "new-install" && value.AttemptId is null && value.Host is null) return;
        if (value.State == RuntimeState.Quiescent && value.Origin == "operator-confirmed" && ValidIdentity(value.Requester) && value.AttemptId is null && value.Host is null) return;
        if (!Guid.TryParseExact(value.AttemptId, "N", out _) || !HashValue(value.TokenHash) || !HashValue(value.ManifestHash) ||
            !ValidIdentity(value.Requester) || string.IsNullOrWhiteSpace(value.EntryPoint) || !Path.IsPathFullyQualified(value.EntryPoint) ||
            value.Arguments is null || value.Arguments.Any(a => a is null || a.Contains('\0')) ||
            (value.Host is not null && (!ValidIdentity(value.Host) || value.Host.Owner != value.Requester!.Owner)))
            throw new InvalidDataException("Incomplete runtime identity.");
        if (value.State == RuntimeState.LaunchPending && value.Origin == "supervised") return;
        if (value.Host is null || (value.PayloadIdentity is not null && !ValidIdentity(value.PayloadIdentity))) throw new InvalidDataException("Missing runtime host.");
        if (value.State == RuntimeState.Running && value.Origin == "supervised") return;
        if (value.State == RuntimeState.Quiescent && value.Origin == "supervisor-completed") return;
        throw new InvalidDataException("Inconsistent runtime state.");
    }
    internal static void Write(LauncherConfig config, RuntimeRecord record) => RuntimeStatePersistence.Write(RecordPath(config), record);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or RuntimeDataException)
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
        using var lane = RuntimeServiceState.Lock(config);
        RuntimeServiceState.RequireLaunch(config, false);
        return BeginUnderServiceLock(config, requester, hostExecutable);
    }
    internal static RuntimeLaunchTicket BeginUnderServiceLock(LauncherConfig config, RuntimeIdentity requester, string hostExecutable)
    {
        using var lease = InstallationMutationLease.Acquire(config);
        if (File.Exists(UpdateTransactionManager.JournalPath(config)))
            throw new RuntimeBlockedException(new(RuntimeState.Unknown, "installation-recovery-required", "중단된 설치를 먼저 복구한 뒤 실행해 주세요."));
        var manifest = JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult();
        LauncherEngine.ValidateManifest(manifest, config);
        var entry = SafePath.ResolveInsideChecked(config.InstallDir, manifest.EntryPoint);
        if (!File.Exists(entry)) throw new FileNotFoundException("Installed entry point is missing.", entry);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var attempt = Guid.NewGuid().ToString("N");
        var runtimeData = RuntimeDataPolicy.Plan(config, requester, attempt);
        Write(config, new()
        {
            InstallationId = InstallationId(config), State = RuntimeState.LaunchPending, AttemptId = attempt,
            TokenHash = Hash(token), Requester = requester, ManifestHash = Hashing.Sha256FileAsync(config.InstalledManifestPath).GetAwaiter().GetResult(),
            EntryPoint = entry, Arguments = config.LaunchArguments ?? [], RuntimeData = runtimeData, Origin = "supervised"
        });
        return new(attempt, token, InstallationId(config), Path.GetFullPath(hostExecutable), runtimeData is not null);
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
        return new(record.EntryPoint!, Path.GetDirectoryName(record.EntryPoint!)!, record.Arguments, record.RuntimeData);
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
        if (!completed && payloadPid.HasValue)
        {
            try
            {
                var payload = RuntimeIdentities.Read(payloadPid.Value);
                if (payload.Owner == peer.Owner) record.PayloadIdentity = payload;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* fast root exit; family remains supervised */ }
        }
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
        ValidateRecoveryOwner(config, actor);
        if (!confirm) return Observe(config);
        using var lane = RuntimeServiceState.Lock(config);
        using var gate = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        return RecoverUnderLock(config, actor);
    }
    internal static void ValidateRecoveryOwner(LauncherConfig config, RuntimeIdentity actor)
    {
        if (config.IsManagedDeployment && !actor.Administrator) throw new UnauthorizedAccessException("Managed runtime recovery requires a local administrator.");
        if (!config.IsManagedDeployment && !actor.Administrator && RuntimeIdentities.DirectoryOwner(config.InstallDir) != actor.Owner)
            throw new UnauthorizedAccessException("Portable runtime recovery requires the installation owner.");
    }
    internal static RuntimeObservation RecoverUnderLock(LauncherConfig config, RuntimeIdentity actor)
    {
        ValidateRecoveryTarget(config, actor);
        var path = RecordPath(config);
        if (File.Exists(path)) File.Copy(path, path + ".before-recovery-" + Guid.NewGuid().ToString("N"), false);
        Write(config, new() { InstallationId = InstallationId(config), State = RuntimeState.Quiescent, Origin = "operator-confirmed", Requester = actor });
        return Observe(config);
    }
    internal static void ValidateRecoveryTarget(LauncherConfig config, RuntimeIdentity actor)
    {
        ValidateRecoveryOwner(config, actor);
        RuntimeRecord? record = null;
        try { record = Read(config); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        if (record?.State != RuntimeState.Quiescent && record?.Host is not null && RuntimeIdentities.StillMatches(record.Host)) throw new RuntimeBlockedException(Observe(config));
        if (record?.PayloadIdentity is not null && RuntimeIdentities.StillMatches(record.PayloadIdentity))
            throw new RuntimeBlockedException(new(RuntimeState.Running,"payload-still-running","실행 중—프로그램을 종료한 뒤 다시 시도해 주세요."));
        if (!config.IsManagedDeployment && record?.Requester is not null && record.Requester.Owner != actor.Owner) throw new UnauthorizedAccessException("Runtime belongs to another owner.");
        // Explicit operator maintenance acknowledgement, NOT OS-proven family termination.
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
        // Fail before creating the lock or runtime record when mutable data overlaps an install.
        RuntimeDataPolicy.ValidateConfiguration(config);
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
