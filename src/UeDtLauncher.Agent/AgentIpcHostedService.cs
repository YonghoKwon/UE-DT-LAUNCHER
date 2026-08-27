using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UeDtLauncher.Agent;

internal sealed class AgentIpcHostedService(ILogger<AgentIpcHostedService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);
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
            if (validationError is not null)
            {
                response = Error(request, "rejected", validationError, identity);
            }
            else if (request.StreamProgress)
            {
                response = await HandleStreamingRequestAsync(stream, request, identity, cancellationToken);
            }
            else
            {
                response = await HandleValidatedRequestAsync(request, identity, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            response = Error(request, "invalid-request", ex.Message, identity);
        }

        await ManagedAgentFrameCodec.WriteAsync(stream, response, cancellationToken);
    }

    private async Task<ManagedAgentResponse> HandleStreamingRequestAsync(
        Stream stream,
        ManagedAgentRequest request,
        string? identity,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ManagedAgentProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var writer = Task.Run(async () =>
        {
            await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken))
            {
                await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Status = "progress",
                    AgentVersion = AgentVersion(),
                    IsFinal = false,
                    Progress = [progress]
                }, cancellationToken);
            }
        }, cancellationToken);

        try
        {
            var response = await HandleValidatedRequestAsync(
                request,
                identity,
                cancellationToken,
                progress => channel.Writer.TryWrite(progress));
            channel.Writer.TryComplete();
            await writer;
            response.Progress.Clear();
            return response;
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            try { await writer; } catch { /* the final error response remains authoritative */ }
            throw;
        }
    }

    private async Task<ManagedAgentResponse> HandleValidatedRequestAsync(
        ManagedAgentRequest request,
        string? identity,
        CancellationToken cancellationToken,
        Action<ManagedAgentProgress>? progressSink = null)
    {
        if (request.Command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return new ManagedAgentResponse
            {
                CorrelationId = request.CorrelationId,
                Success = true,
                Status = "running",
                Message = "Managed Agent is running and IPC validation passed.",
                AgentVersion = AgentVersion(),
                ClientIdentity = identity
            };
        }
        if (!await _commandGate.WaitAsync(0, cancellationToken))
            return Error(request, "busy", "Another managed Agent operation is already running.", identity);
        try
        {
            var layout = ManagedLauncherPathLayout.Current();
            var configPath = Path.Combine(layout.ConfigRoot, "launcher.config.json");
            if (!File.Exists(configPath)) return Error(request, "not-configured", $"Managed config was not found: {configPath}", identity);
            var config = await LoadProjectConfigAsync(configPath, request.ProjectId, cancellationToken);
            var progress = new List<ManagedAgentProgress>();
            void AddProgress(LauncherProgress value)
            {
                if (progress.Count >= 256) progress.RemoveAt(0);
                var item = new ManagedAgentProgress(value.Stage, DiagnosticRedactor.Redact(value.Message), value.Percent);
                progress.Add(item);
                progressSink?.Invoke(item);
            }

            switch (request.Command.ToLowerInvariant())
            {
                case "check":
                        using (var http = SecureHttpClientFactory.Create(config))
                        {
                        await CatalogResolver.ResolveAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), cancellationToken);
                        var document = await ManifestDownloader.DownloadAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), cancellationToken);
                        var projectStatus = await ManagedProjectStatusInspector.InspectAsync(config, document.Manifest, cancellationToken);
                        return Success(request, identity, "checked", $"Release {document.Manifest.Version} metadata, files and signatures are valid.", progress, projectStatus);
                    }
                case "update":
                case "repair":
                    config.LaunchAfterUpdate = false;
                    config.RepairMode = request.Command.Equals("repair", StringComparison.OrdinalIgnoreCase);
                    using (var http = SecureHttpClientFactory.Create(config))
                    {
                        await CatalogResolver.ResolveAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), cancellationToken);
                        using var engine = new LauncherEngine(config, AddProgress, new FileLogger(layout.LogRoot), echoToConsole: false);
                            await engine.RunAsync(cancellationToken);
                        }
                    var installed = await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath, cancellationToken);
                    var completedStatus = await ManagedProjectStatusInspector.InspectAsync(config, installed, cancellationToken);
                    return Success(request, identity, "completed", config.RepairMode ? "Repair completed." : "Update completed.", progress, completedStatus);
                case "rollback":
                    var backup = BackupManager.List(config.BackupDir).FirstOrDefault();
                    if (backup.BackupRoot is null) return Error(request, "no-backup", "No rollback backup is available.", identity);
                    using (SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config)))
                    {
                        await BackupManager.RestoreAsync(
                            backup.BackupRoot,
                            config.InstallDir,
                            config.InstalledManifestPath,
                            config.InstallStatePath,
                            message => progress.Add(new ManagedAgentProgress("Rollback", DiagnosticRedactor.Redact(message), null)),
                            cancellationToken);
                    }
                    return Success(request, identity, "completed", "Rollback completed.", progress);
                case "service-run":
                    var serviceExit = await ServiceRunner.RunAsync(configPath, null, once: true, cancellationToken);
                    return serviceExit == 0
                        ? Success(request, identity, "completed", "One managed service cycle completed.", progress)
                        : Error(request, "service-failed", "Managed service cycle failed.", identity);
                case "diagnostics":
                    var output = Path.Combine(layout.LogRoot, $"agent-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                    await DiagnosticsExporter.ExportAsync(configPath, output, cancellationToken);
                    return Success(request, identity, "completed", $"Diagnostics written: {output}", progress);
                default:
                    return Error(request, "unsupported", "Agent command handler is unavailable.", identity);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Managed Agent command {Command} failed", request.Command);
            CrashReporter.Report(ex, "agent-command");
            return Error(request, "failed", DiagnosticRedactor.Redact(ex.Message), identity);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static async Task<LauncherConfig> LoadProjectConfigAsync(
        string configPath,
        string? requestedProjectId,
        CancellationToken cancellationToken)
    {
        var config = await JsonFiles.ReadAsync<LauncherConfig>(configPath, cancellationToken);
        var selected = string.IsNullOrWhiteSpace(requestedProjectId)
            ? config.Projects.FirstOrDefault(project => project.ProjectId.Equals(config.ProjectId, StringComparison.OrdinalIgnoreCase))
            : config.Projects.FirstOrDefault(project => project.ProjectId.Equals(requestedProjectId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(requestedProjectId) && selected is null && !requestedProjectId.Equals(config.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Project is not declared in managed config: {requestedProjectId}");
        if (!string.IsNullOrWhiteSpace(requestedProjectId)) config.ProjectId = requestedProjectId;
        LauncherPaths.ResolveInPlace(config, configPath, selected?.InstallPath);
        LauncherConfigValidator.Validate(config);
        return config;
    }

    private static ManagedAgentResponse Success(
        ManagedAgentRequest request,
        string? identity,
        string status,
        string message,
        List<ManagedAgentProgress> progress,
        ManagedProjectStatus? projectStatus = null) => new()
        {
            CorrelationId = request.CorrelationId,
            Success = true,
            Status = status,
            Message = message,
            AgentVersion = AgentVersion(),
            ClientIdentity = identity,
            Progress = progress,
            ProjectStatus = projectStatus
        };

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
            AgentVersion = AgentVersion(),
            ClientIdentity = identity
        };

    private static string AgentVersion() => typeof(AgentIpcHostedService).Assembly.GetCustomAttributes(false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? typeof(AgentIpcHostedService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
