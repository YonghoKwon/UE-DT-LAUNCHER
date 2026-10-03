using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeDtLauncher;

public static class ManagedAgentProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;
    public const string DefaultWindowsPipeName = "UeDtLauncher.Agent.v1";
    public const string DefaultLinuxSocketPath = "/run/ue-dt-launcher/agent-v1.sock";
    public const string RuntimeCapability = "runtime-supervision-v1";
    public static bool RequiresRuntimeCapability(string command) => command.ToLowerInvariant() is "update" or "repair" or "rollback" or "service-run" or "runtime-recover" or "operation-resume" || command.StartsWith("launch-", StringComparison.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> AllowedCommands = new HashSet<string>(
        ["status", "catalog", "check", "update", "repair", "rollback", "rollback-preview", "service-run", "diagnostics", "project-asset", "doctor", "launch-begin", "launch-abort", "launch-attach", "launch-started", "launch-complete", "runtime-inspect", "runtime-recover", "operation-status", "operation-cancel", "operation-resume", "operation-discard"],
        StringComparer.OrdinalIgnoreCase);

    public static string ResolveEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("UE_DT_AGENT_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return OperatingSystem.IsWindows() ? DefaultWindowsPipeName : DefaultLinuxSocketPath;
    }

    public static string? Validate(ManagedAgentRequest request)
    {
        if (request.ProtocolVersion != Version) return $"Unsupported Agent protocol version: {request.ProtocolVersion}.";
        if (string.IsNullOrWhiteSpace(request.CorrelationId)) return "correlationId is required.";
        if (!AllowedCommands.Contains(request.Command)) return $"Agent command is not allowed: {request.Command}.";
        if (request.ProjectId is { Length: > 128 }) return "projectId is too long.";
        if (request.Command.StartsWith("operation-", StringComparison.Ordinal) &&
            (!Guid.TryParseExact(request.OperationId, "N", out _) || request.Selection is not null || request.RuntimeTicket is not null))
            return "Operation control requires an ID, without release or launch inputs.";
        if (request.Command.Equals("project-asset", StringComparison.OrdinalIgnoreCase) &&
            (!request.StreamProgress || string.IsNullOrWhiteSpace(request.ProjectId) || request.AssetKind is not ("hero" or "thumbnail") || request.Selection is not null))
            return "project-asset requires a project, hero/thumbnail kind and streaming, without release selection.";
        return null;
    }
}

public sealed class ManagedAgentRequest
{
    public int ProtocolVersion { get; set; } = ManagedAgentProtocol.Version;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = "status";
    public string? ProjectId { get; set; }
    public bool StreamProgress { get; set; }
    public ReleaseSelection? Selection { get; set; }
    public string? AssetKind { get; set; }
    public bool OnlineCheck { get; set; }
    public DoctorTarget? DoctorTarget { get; set; }
    public List<string> ClientCapabilities { get; set; } = [];
    public RuntimeLaunchTicket? RuntimeTicket { get; set; }
    public int? PayloadPid { get; set; }
    public bool ConfirmStopped { get; set; }
    public string? ServiceVersion { get; set; }
    public string? ExpectedBackupId { get; set; }
    public string? ExpectedBackupFingerprint { get; set; }
    public string? OperationId { get; set; }
}

public sealed class ManagedAgentResponse
{
    public void ThrowIfFailed()
    {
        if (Status == "Cancelled") throw new OperationCanceledException("Operation cancelled after workers and recovery completed.");
        if (Success) return;
        if (Runtime is not null) { var blocked=new RuntimeBlockedException(Runtime);blocked.Data["CorrelationId"]=CorrelationId;throw blocked; }
        if (Status == "client-upgrade-required") throw new RuntimeBlockedException(new(RuntimeState.Unknown, Status, "런처와 업데이트 서비스를 함께 업데이트해 주세요."));
        throw new AgentOperationException(ErrorCode ?? Status,CorrelationId,Message);
    }
    public int ProtocolVersion { get; set; } = ManagedAgentProtocol.Version;
    public string CorrelationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string? ClientIdentity { get; set; }
    public string? ErrorCode { get; set; }
    public RollbackPreview? RollbackPreview { get; set; }
    public bool IsFinal { get; set; } = true;
    public List<ManagedAgentProgress> Progress { get; set; } = new();
    public ManagedProjectStatus? ProjectStatus { get; set; }
    public ReleaseSelection? SelectedRelease { get; set; }
    public DistributionCatalog? Catalog { get; set; }
    public ManagedAssetChunk? AssetChunk { get; set; }
    public DoctorReport? DoctorReport { get; set; }
    public List<string> AgentCapabilities { get; set; } = [];
    public RuntimeLaunchTicket? RuntimeTicket { get; set; }
    public RuntimeHostRequest? RuntimeLaunch { get; set; }
    public RuntimeObservation? Runtime { get; set; }
    public OperationStatus? Operation { get; set; }
    public bool? InstallationCommitted { get; set; }
}

public sealed record ManagedAssetChunk(long Offset, long TotalBytes, string Sha256, string Extension, byte[] Data);

public sealed record ManagedAgentProgress(
    string Stage, string Message, double? Percent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BytesDownloaded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FileIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FileCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LauncherPerformanceMetrics? Performance = null)
{
    internal LauncherProgress ToLauncherProgress() => new(Stage, Message, Percent,
        BytesDownloaded, TotalBytes, FileIndex, FileCount, Performance);
}

public sealed record ManagedProjectStatus(
    bool IsInstalled,
    string? InstalledVersion,
    string? AvailableVersion,
    bool UpdateRequired,
    int MissingFiles,
    int ChangedFiles,
    bool HasBackup,
    PreviousInstallation? PreviousInstallation = null);

public sealed record PreviousInstallation(ReleaseSelection Release, string InstalledAtUtc);

public static class ManagedProjectStatusInspector
{
    public static Task<ManagedProjectStatus> InspectAsync(
        LauncherConfig config,
        LauncherManifest availableManifest,
        CancellationToken cancellationToken = default) =>
        InspectCoreAsync(config, availableManifest, Hashing.Sha256MatchesAsync, cancellationToken);

    internal static async Task<ManagedProjectStatus> InspectCoreAsync(
        LauncherConfig config,
        LauncherManifest availableManifest,
        Func<string, string, CancellationToken, Task<bool>> hashMatches,
        CancellationToken cancellationToken = default)
    {
        if (config.Performance is null || config.Performance.HashConcurrency is < 1 or > 4)
            throw new InvalidOperationException("performance.hashConcurrency must be between 1 and 4.");
        LauncherManifest? installedManifest = null;
        if (File.Exists(config.InstalledManifestPath))
        {
            installedManifest = await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath, cancellationToken);
        }

        // Each worker owns one result slot; counts do not depend on completion order.
        var outcomes = new byte[availableManifest.Files.Count]; // 0=unchanged, 1=missing, 2=changed
        await BoundedFileWorkers.RunAsync(availableManifest.Files.Count, config.Performance.HashConcurrency, async (index, token) =>
        {
            token.ThrowIfCancellationRequested();
            var file = availableManifest.Files[index];
            var installedPath = SafePath.ResolveInsideChecked(config.InstallDir, file.Path);
            if (!File.Exists(installedPath))
            {
                outcomes[index] = 1;
                return;
            }

            if (new FileInfo(installedPath).Length != file.Size ||
                !await hashMatches(installedPath, file.Sha256, token)) outcomes[index] = 2;
        }, cancellationToken);
        var missing = outcomes.Count(outcome => outcome == 1);
        var changed = outcomes.Count(outcome => outcome == 2);

        var installedVersion = installedManifest?.Version;
        var updateRequired = installedManifest is null
                             || !string.Equals(installedVersion, availableManifest.Version, StringComparison.OrdinalIgnoreCase)
                             || missing > 0
                             || changed > 0;
        return new ManagedProjectStatus(
            installedManifest is not null,
            installedVersion,
            availableManifest.Version,
            updateRequired,
            missing,
            changed,
            BackupManager.List(config.BackupDir).Count > 0,
            installedManifest is null ? await PreviousInstallationStatus.FindAsync(config, cancellationToken) : null);
    }
}

public sealed record ManagedLauncherPathLayout(
    string InstallRoot,
    string ConfigRoot,
    string StateRoot,
    string AppsRoot,
    string LogRoot,
    string CredentialRoot)
{
    public static ManagedLauncherPathLayout Current()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("UE_DT_AGENT_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            var root = Path.GetFullPath(overrideRoot);
            return UnderRoot(root, root);
        }

        if (OperatingSystem.IsWindows())
        {
            var dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UE-DT Launcher");
            var installRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "UE-DT Launcher");
            return UnderRoot(installRoot, dataRoot);
        }

        return new ManagedLauncherPathLayout(
            "/opt/ue-dt-launcher",
            "/etc/ue-dt-launcher",
            "/var/lib/ue-dt-launcher/state",
            "/var/lib/ue-dt-launcher/apps",
            "/var/log/ue-dt-launcher",
            "/etc/ue-dt-launcher/credentials");
    }

    private static ManagedLauncherPathLayout UnderRoot(string installRoot, string dataRoot) => new(
        installRoot,
        Path.Combine(dataRoot, "config"),
        Path.Combine(dataRoot, "state"),
        Path.Combine(dataRoot, "apps"),
        Path.Combine(dataRoot, "logs"),
        Path.Combine(dataRoot, "credentials"));
}

public sealed record ManagedMigrationPlan(
    string SourceConfigPath,
    string TargetConfigPath,
    string SourceStateRoot,
    string TargetStateRoot,
    IReadOnlyList<string> ProjectIds,
    bool TargetAlreadyExists,
    bool Applied = false,
    bool CanApply = false,
    string BlockingReason = "Shared installation ownership transfer is not supported. Keep the source and request a separate migration plan.");

public static class PortableMigrationService
{
    public static async Task<ManagedMigrationPlan> PlanAsync(
        string sourceConfigPath,
        ManagedLauncherPathLayout layout,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceConfigPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Legacy launcher config was not found.", source);
        var config = await JsonFiles.ReadAsync<LauncherConfig>(source, cancellationToken);
        var projectIds = config.Projects.Select(project => project.ProjectId)
            .Append(config.ProjectId ?? "default")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ManagedMigrationPlan(
            source,
            Path.Combine(layout.ConfigRoot, "launcher.config.json"),
            LauncherPaths.ResolveConfigRelative(source, config.StateRootDir),
            layout.StateRoot,
            projectIds,
            File.Exists(Path.Combine(layout.ConfigRoot, "launcher.config.json")));
    }

    public static Task ApplyAsync(ManagedMigrationPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing migration reuses InstallDir but creates another runtime/lock authority.
        // No target directory, state, config or source acknowledgement may be written.
        throw new InvalidOperationException("Portable-to-managed apply is blocked: shared installation ownership requires a separate migration plan. Use --dry-run for inspection; do not reuse the installation through two configs.");
    }

}

internal static class ManagedAgentFrameCodec
{
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonFiles.Options);
        if (payload.Length > ManagedAgentProtocol.MaxFrameBytes)
            throw new InvalidOperationException("Agent IPC frame exceeds the maximum size.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > ManagedAgentProtocol.MaxFrameBytes)
            throw new InvalidDataException($"Invalid Agent IPC frame length: {length}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonFiles.Options)
               ?? throw new InvalidDataException("Agent IPC JSON was empty or invalid.");
    }
}

public sealed class ManagedAgentClient(string? endpoint = null)
{
    private readonly string _endpoint = endpoint ?? ManagedAgentProtocol.ResolveEndpoint();

    public async Task<ManagedAgentResponse> SendRuntimeAsync(string command, LauncherConfig config, RuntimeLaunchTicket? ticket = null, int? payloadPid = null, bool confirm = false, CancellationToken cancellationToken = default, string? serviceVersion = null)
    {
        var request = new ManagedAgentRequest { Command = command, ProjectId = config.ProjectId, Selection = config.SelectedRelease,
            ClientCapabilities = [ManagedAgentProtocol.RuntimeCapability, RuntimeDataPolicy.Capability], RuntimeTicket = ticket, PayloadPid = payloadPid, ConfirmStopped = confirm, ServiceVersion = serviceVersion };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var stream = await ConnectAsync(timeout.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeout.Token);
        var response = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentResponse>(stream, timeout.Token);
        if (response.ProtocolVersion != 1 || response.CorrelationId != request.CorrelationId) throw new InvalidDataException("Runtime IPC response mismatch.");
        if (response.AgentCapabilities?.Contains(ManagedAgentProtocol.RuntimeCapability) != true) throw new RuntimeBlockedException(new(RuntimeState.Unknown,"client-upgrade-required","런처와 업데이트 서비스를 함께 업데이트해 주세요."));
        if ((config.RuntimeData?.Enabled == true || response.RuntimeTicket?.RequiresRuntimeData == true || ticket?.RequiresRuntimeData == true) &&
            response.AgentCapabilities?.Contains(RuntimeDataPolicy.Capability) != true)
            throw new RuntimeBlockedException(new(RuntimeState.Unknown,"client-upgrade-required","저장 경로 분리를 지원하는 런처와 업데이트 서비스를 함께 업데이트해 주세요."));
        return response;
    }

    public async Task<DoctorReport> DoctorAsync(bool online, CancellationToken cancellationToken = default, DoctorTarget? target = null)
    {
        var status = await SendAsync("status", timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
        if (status.AgentCapabilities?.Contains(DoctorPresentation.Capability) != true)
            throw new AgentOperationException("client-upgrade-required", status.CorrelationId, "상세 진단을 지원하는 업데이트 서비스로 갱신해 주세요.");
        var request = new ManagedAgentRequest { Command = "doctor", OnlineCheck = online, DoctorTarget = target, ProjectId = target?.ProjectId };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var stream = await ConnectAsync(timeout.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeout.Token);
        var response = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentResponse>(stream, timeout.Token);
        return DoctorResponse.ValidateEnvelope(response, request);
    }

    public async Task<string?> GetProjectAssetAsync(string projectId, string kind, string cacheRoot, CancellationToken cancellationToken = default)
    {
        var request = new ManagedAgentRequest { Command = "project-asset", ProjectId = projectId, AssetKind = kind, StreamProgress = true };
        var error = ManagedAgentProtocol.Validate(request); if (error is not null) throw new InvalidDataException(error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var stream = await ConnectAsync(timeout.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeout.Token);
        return await ManagedAssetReceiver.ReceiveAsync(stream, request.CorrelationId, cacheRoot, timeout.Token);
    }

    public async Task<ManagedAgentResponse> SendAsync(
        string command,
        string? projectId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (ManagedAgentProtocol.RequiresRuntimeCapability(command)) await RequireCapabilitiesAsync(cancellationToken);
        var request = new ManagedAgentRequest { Command = command, ProjectId = projectId, ClientCapabilities = [ManagedAgentProtocol.RuntimeCapability] };
        var validationError = ManagedAgentProtocol.Validate(request);
        if (validationError is not null) throw new InvalidOperationException(validationError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        await using var stream = await ConnectAsync(timeoutCts.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeoutCts.Token);
        var response = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentResponse>(stream, timeoutCts.Token);
        if (!string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
            throw new InvalidDataException("Agent response correlationId did not match the request.");
        return response;
    }

    public async Task<ManagedAgentResponse> SendStreamingAsync(
        string command,
        string? projectId,
        Action<ManagedAgentProgress> onProgress,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        ReleaseSelection? selection = null,
        RollbackPreview? expectedBackup = null,
        string? operationId = null)
    {
        if (ManagedAgentProtocol.RequiresRuntimeCapability(command)) await RequireCapabilitiesAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(onProgress);
        var request = new ManagedAgentRequest
        {
            Command = command,
            ProjectId = projectId,
            StreamProgress = true,
            Selection = selection,
            ExpectedBackupId = expectedBackup?.BackupId,
            ExpectedBackupFingerprint = expectedBackup?.MetadataFingerprint,
            CorrelationId = operationId ?? Guid.NewGuid().ToString("N"),
            ClientCapabilities = [ManagedAgentProtocol.RuntimeCapability]
        };
        var validationError = ManagedAgentProtocol.Validate(request);
        if (validationError is not null) throw new InvalidOperationException(validationError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
        await using var stream = await ConnectAsync(timeoutCts.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeoutCts.Token);
        return await ReadStreamingResponsesAsync(stream, request, onProgress, timeoutCts.Token);
    }

    public async Task<ManagedAgentResponse> SendOperationAsync(string action, string operationId, CancellationToken token = default)
    {
        if (action is not ("status" or "cancel" or "resume" or "discard")) throw new ArgumentException("Unknown operation action.");
        var capabilities = await SendAsync("status", cancellationToken: token);
        if (!capabilities.AgentCapabilities.Contains(OperationRegistry.Capability)) throw new InvalidOperationException("Update the Agent to manage operations.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(action == "resume" ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10));
        var request = new ManagedAgentRequest { Command = "operation-" + action, OperationId = operationId,
            StreamProgress = action == "resume", ClientCapabilities = [ManagedAgentProtocol.RuntimeCapability, OperationRegistry.Capability] };
        await using var stream = await ConnectAsync(timeout.Token);
        await ManagedAgentFrameCodec.WriteAsync(stream, request, timeout.Token);
        return await ReadStreamingResponsesAsync(stream, request, _ => { }, timeout.Token);
    }
    public async Task<ManagedAgentResponse> SendCancellableAsync(string command,string? projectId,Action<ManagedAgentProgress> progress,
        ReleaseSelection? selection=null,CancellationToken token=default,string? operationId=null)
    {
        token.ThrowIfCancellationRequested();
        var status=await SendAsync("status",cancellationToken:token);
        if(!status.AgentCapabilities.Contains(ManagedOperationCoordinator.Capability))
            throw new AgentOperationException("client-upgrade-required",status.CorrelationId,"취소를 지원하는 업데이트 서비스로 갱신해 주세요.");
        token.ThrowIfCancellationRequested();
        var id=operationId??Guid.NewGuid().ToString("N");
        return await ManagedOperationCoordinator.RunAsync(id,
            callback=>SendStreamingAsync(command,projectId,callback,selection:selection,operationId:id),
            ()=>SendOperationAsync("cancel",id),progress,token);
    }

    internal static async Task<ManagedAgentResponse> ReadStreamingResponsesAsync(
        Stream stream,
        ManagedAgentRequest request,
        Action<ManagedAgentProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var response = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentResponse>(stream, cancellationToken);
            if (!string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.Ordinal))
                throw new InvalidDataException("Agent response correlationId did not match the request.");

            foreach (var progress in response.Progress) onProgress(progress);
            if (response.IsFinal) return response;
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        using var connectTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(connectTimeout.Token); return pipe; }
            catch(OperationCanceledException ex) when(!cancellationToken.IsCancellationRequested) {pipe.Dispose();throw new AgentConnectionException(ex);}
            catch(IOException ex) {pipe.Dispose();throw new AgentConnectionException(ex);}
            catch {pipe.Dispose();throw;}
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint), connectTimeout.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch(OperationCanceledException ex) when(!cancellationToken.IsCancellationRequested)
        {socket.Dispose();throw new AgentConnectionException(ex);}
        catch(Exception ex) when(ex is SocketException or IOException)
        {
            socket.Dispose();
            throw new AgentConnectionException(ex);
        }
        catch { socket.Dispose();throw; }
    }

    private async Task RequireCapabilitiesAsync(CancellationToken token)
    {
        var status = await SendAsync("status", cancellationToken: token);
        if (status.AgentCapabilities?.Contains(ManagedAgentProtocol.RuntimeCapability) != true)
            throw new RuntimeBlockedException(new(RuntimeState.Unknown,"client-upgrade-required","런처와 업데이트 서비스를 함께 업데이트해 주세요."));
    }
}

public static class ManagedAppLauncher
{
    public static async Task<Process> LaunchAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(config.InstalledManifestPath))
            throw new FileNotFoundException("Installed manifest was not found.", config.InstalledManifestPath);
        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath, cancellationToken);
        LauncherEngine.ValidateManifest(manifest, config);
        var entryPoint = SafePath.ResolveInsideChecked(config.InstallDir, manifest.EntryPoint);
        if (!File.Exists(entryPoint)) throw new FileNotFoundException("Managed application entry point was not found.", entryPoint);
        if (config.WindowsIntegration.CreateDesktopShortcut || config.WindowsIntegration.CreateStartMenuShortcut || config.WindowsIntegration.RegisterAppEntry)
            WindowsIntegration.Apply(config, Path.Combine(ManagedLauncherPathLayout.Current().InstallRoot, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher"));
        return await RuntimeLauncher.LaunchAsync(config, cancellationToken);
    }
}
