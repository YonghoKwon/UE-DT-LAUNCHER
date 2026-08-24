using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace UeDtLauncher;

public static class ManagedAgentProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1024 * 1024;
    public const string DefaultWindowsPipeName = "UeDtLauncher.Agent.v1";
    public const string DefaultLinuxSocketPath = "/run/ue-dt-launcher/agent-v1.sock";

    public static readonly IReadOnlySet<string> AllowedCommands = new HashSet<string>(
        ["status", "check", "update", "repair", "rollback", "service-run", "diagnostics"],
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
        return null;
    }
}

public sealed class ManagedAgentRequest
{
    public int ProtocolVersion { get; set; } = ManagedAgentProtocol.Version;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = "status";
    public string? ProjectId { get; set; }
}

public sealed class ManagedAgentResponse
{
    public int ProtocolVersion { get; set; } = ManagedAgentProtocol.Version;
    public string CorrelationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string? ClientIdentity { get; set; }
    public bool IsFinal { get; set; } = true;
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
    bool Applied = false);

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

    public static async Task ApplyAsync(
        ManagedMigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.TargetAlreadyExists || File.Exists(plan.TargetConfigPath))
            throw new IOException($"Managed config already exists: {plan.TargetConfigPath}");

        var config = await JsonFiles.ReadAsync<LauncherConfig>(plan.SourceConfigPath, cancellationToken);
        config.InstallDir = LauncherPaths.ResolveConfigRelative(plan.SourceConfigPath, config.InstallDir);
        config.LogDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(plan.TargetStateRoot)!, "logs"));
        config.StateRootDir = plan.TargetStateRoot;
        foreach (var project in config.Projects)
        {
            if (!string.IsNullOrWhiteSpace(project.InstallPath))
                project.InstallPath = LauncherPaths.ResolveConfigRelative(plan.SourceConfigPath, project.InstallPath);
        }

        CopyDirectoryWithoutOverwrite(plan.SourceStateRoot, plan.TargetStateRoot);
        await JsonFiles.WriteAsync(plan.TargetConfigPath, config, cancellationToken);
    }

    private static void CopyDirectoryWithoutOverwrite(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var target = SafePath.ResolveInside(targetRoot, relative);
            if (File.Exists(target)) throw new IOException($"Migration target already contains state file: {relative}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, overwrite: false);
        }
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

    public async Task<ManagedAgentResponse> SendAsync(
        string command,
        string? projectId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ManagedAgentRequest { Command = command, ProjectId = projectId };
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

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
