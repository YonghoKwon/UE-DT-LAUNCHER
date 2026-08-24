using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UeDtLauncher.Agent;

internal sealed class AgentIpcHostedService(ILogger<AgentIpcHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var layout = ManagedLauncherPathLayout.Current();
        foreach (var path in new[] { layout.ConfigRoot, layout.StateRoot, layout.AppsRoot, layout.LogRoot, layout.CredentialRoot })
            Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows())
            await RunWindowsPipeAsync(stoppingToken);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            await RunUnixSocketAsync(stoppingToken);
        else
            throw new PlatformNotSupportedException("Managed Agent IPC supports Windows, Linux, and macOS only.");
    }

    [SupportedOSPlatform("windows")]
    private async Task RunWindowsPipeAsync(CancellationToken stoppingToken)
    {
        var endpoint = ManagedAgentProtocol.ResolveEndpoint();
        logger.LogInformation("Agent IPC listening on named pipe {Endpoint}", endpoint);
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreateSecuredPipe(endpoint);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var identity = TryGetClientIdentity(pipe);
                await HandleClientAsync(pipe, identity, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent named-pipe request failed");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateSecuredPipe(string endpoint)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            endpoint,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetClientIdentity(NamedPipeServerStream pipe)
    {
        try { return pipe.GetImpersonationUserName(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private async Task RunUnixSocketAsync(CancellationToken stoppingToken)
    {
        var endpoint = ManagedAgentProtocol.ResolveEndpoint();
        var directory = Path.GetDirectoryName(endpoint);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(endpoint)) File.Delete(endpoint);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        File.SetUnixFileMode(endpoint,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(8);
        logger.LogInformation("Agent IPC listening on Unix socket {Endpoint}", endpoint);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                await using var stream = new NetworkStream(client, ownsSocket: true);
                await HandleClientAsync(stream, "local-socket-user", stoppingToken);
            }
        }
        finally
        {
            if (File.Exists(endpoint)) File.Delete(endpoint);
        }
    }

    private async Task HandleClientAsync(Stream stream, string? identity, CancellationToken cancellationToken)
    {
        ManagedAgentRequest? request = null;
        ManagedAgentResponse response;
        try
        {
            request = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream, cancellationToken);
            var validationError = ManagedAgentProtocol.Validate(request);
            response = validationError is null
                ? HandleValidatedRequest(request, identity)
                : Error(request, "rejected", validationError, identity);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            response = Error(request, "invalid-request", ex.Message, identity);
        }

        await ManagedAgentFrameCodec.WriteAsync(stream, response, cancellationToken);
    }

    private static ManagedAgentResponse HandleValidatedRequest(ManagedAgentRequest request, string? identity)
    {
        if (request.Command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return new ManagedAgentResponse
            {
                CorrelationId = request.CorrelationId,
                Success = true,
                Status = "running",
                Message = "Managed Agent is running and IPC validation passed.",
                AgentVersion = typeof(AgentIpcHostedService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                ClientIdentity = identity
            };
        }

        return Error(
            request,
            "not-configured",
            "The command is allowed, but no managed project configuration has been activated yet.",
            identity);
    }

    private static ManagedAgentResponse Error(
        ManagedAgentRequest? request,
        string status,
        string message,
        string? identity) => new()
        {
            CorrelationId = request?.CorrelationId ?? string.Empty,
            Success = false,
            Status = status,
            Message = message,
            AgentVersion = typeof(AgentIpcHostedService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            ClientIdentity = identity
        };
}
