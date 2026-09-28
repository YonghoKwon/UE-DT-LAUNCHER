using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.InteropServices;
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
    private static RuntimeIdentity? TryGetClientIdentity(NamedPipeServerStream pipe)
    {
        try { return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? RuntimeIdentities.Read(checked((int)pid)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [StructLayout(LayoutKind.Sequential)]
    private struct UnixPeerCredentials { public int Pid; public uint Uid, Gid; }
    [DllImport("libc", SetLastError=true)]
    private static extern int getsockopt(int socket, int level, int option, out UnixPeerCredentials credentials, ref uint size);

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
                RuntimeIdentity? peer = null;
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        uint size = (uint)Marshal.SizeOf<UnixPeerCredentials>();
                        if (getsockopt(checked((int)client.SafeHandle.DangerousGetHandle()), 1, 17, out var credentials, ref size) == 0 && size == 12)
                        {
                            var observed = RuntimeIdentities.Read(credentials.Pid);
                            if (observed.Owner == credentials.Uid.ToString()) peer = observed;
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException) { }
                }
                await HandleClientAsync(stream, peer, stoppingToken);
            }
        }
        finally
        {
            if (File.Exists(endpoint)) File.Delete(endpoint);
        }
    }

    private async Task HandleClientAsync(Stream stream, RuntimeIdentity? peer, CancellationToken cancellationToken)
    {
        var identity = peer?.Owner;
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
            else if (request.Command.Equals("project-asset", StringComparison.OrdinalIgnoreCase))
            {
                response = await HandleProjectAssetAsync(stream, request, identity, cancellationToken);
            }
            else if (request.StreamProgress)
            {
                response = await HandleStreamingRequestAsync(stream, request, peer, cancellationToken);
            }
            else
            {
                response = await HandleValidatedRequestAsync(request, peer, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            response = Error(request, "invalid-request", ex.Message, identity);
        }

        response.AgentCapabilities = [ManagedAgentProtocol.RuntimeCapability];
        await ManagedAgentFrameCodec.WriteAsync(stream, response, cancellationToken);
    }

    private async Task<ManagedAgentResponse> HandleProjectAssetAsync(Stream stream, ManagedAgentRequest request, string? identity, CancellationToken cancellationToken)
    {
        if (!await _commandGate.WaitAsync(0, cancellationToken)) return Error(request, "busy", "Another operation is running.", identity);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var layout = ManagedLauncherPathLayout.Current();
            var config = await LoadProjectConfigAsync(Path.Combine(layout.ConfigRoot, "launcher.config.json"), request.ProjectId, timeout.Token);
            using var http = SecureHttpClientFactory.Create(config);
            var catalog = await CatalogResolver.DownloadCatalogAsync(config, http, cancellationToken: timeout.Token);
            var project = catalog.Projects.SingleOrDefault(p => p.ProjectId == request.ProjectId);
            var asset = request.AssetKind == "hero" ? project?.Hero : project?.Thumbnail;
            var path = await ProjectAssetCache.GetAsync(asset, config, Path.Combine(layout.StateRoot, "project-images"), timeout.Token);
            if (asset is null || path is null) return Error(request, "no-asset", "Project image is unavailable.", identity);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[64 * 1024]; long offset = 0; int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (offset + read > asset.Size) throw new InvalidDataException("Cached image changed during transfer.");
                await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
                {
                    CorrelationId = request.CorrelationId, Success = true, IsFinal = false, Status = "asset-chunk",
                    AssetChunk = new(offset, asset.Size, asset.Sha256.ToLowerInvariant(), Path.GetExtension(path), buffer.AsSpan(0, read).ToArray())
                }, timeout.Token);
                offset += read;
            }
            if (offset != asset.Size) throw new InvalidDataException("Incomplete cached image.");
            return new ManagedAgentResponse { CorrelationId = request.CorrelationId, Success = true, Status = "completed", AgentVersion = AgentVersion() };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {
            logger.LogWarning("Project image transfer failed ({ErrorType}); using fallback", ex.GetType().Name);
            return Error(request, "asset-unavailable", "Project image is unavailable. A built-in image will be shown.", identity);
        }
        finally { _commandGate.Release(); }
    }

    private async Task<ManagedAgentResponse> HandleStreamingRequestAsync(
        Stream stream,
        ManagedAgentRequest request,
        RuntimeIdentity? peer,
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
                peer,
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
        RuntimeIdentity? peer,
        CancellationToken cancellationToken,
        Action<ManagedAgentProgress>? progressSink = null)
    {
        var identity = peer?.Owner;
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
        if (ManagedAgentProtocol.RequiresRuntimeCapability(request.Command))
        {
            if (peer is null || request.ClientCapabilities?.Contains(ManagedAgentProtocol.RuntimeCapability) != true)
                return Error(request, "client-upgrade-required", "런처와 업데이트 서비스를 함께 업데이트해 주세요.", identity);
        }
        if (!await _commandGate.WaitAsync(0, cancellationToken))
            return Error(request, "busy", "Another managed Agent operation is already running.", identity);
        try
        {
            var layout = ManagedLauncherPathLayout.Current();
            var configPath = Path.Combine(layout.ConfigRoot, "launcher.config.json");
            if (!File.Exists(configPath)) return Error(request, "not-configured", $"Managed config was not found: {configPath}", identity);
            var config = await LoadProjectConfigAsync(configPath, request.ProjectId, cancellationToken);
            if (request.Selection is not null)
            {
                if (string.IsNullOrWhiteSpace(config.DistributionServerUrl)) throw new InvalidOperationException("Explicit selection requires distribution server configuration.");
                request.Selection.Validate();
                if (request.Selection.ProjectId != request.ProjectId || request.Selection.Platform != config.TargetPlatform)
                    throw new InvalidDataException("Release selection does not match this project/platform.");
                config.Environment = request.Selection.Environment; config.Channel = request.Selection.Channel;
                config.VersionPolicy = "exact"; config.RequestedVersion = request.Selection.Version;
                config.ClientProfile = "developer"; // Server policy, not the presentation profile, authorizes this selection.
            }
            var progress = new List<ManagedAgentProgress>();
            void AddProgress(LauncherProgress value)
            {
                if (progress.Count >= 256) progress.RemoveAt(0);
                var item = new ManagedAgentProgress(value.Stage, DiagnosticRedactor.Redact(value.Message), value.Percent,
                    value.BytesDownloaded, value.TotalBytes, value.FileIndex, value.FileCount, value.Performance);
                progress.Add(item);
                progressSink?.Invoke(item);
            }

            switch (request.Command.ToLowerInvariant())
            {
                case "launch-begin":
                    using (var http = SecureHttpClientFactory.Create(config)) await CatalogResolver.ResolveAsync(config, http, cancellationToken: cancellationToken);
                    var hostPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
                    var ticket = RuntimeStore.Begin(config, peer!, hostPath);
                    return new() { CorrelationId = request.CorrelationId, Success = true, RuntimeTicket = ticket, SelectedRelease = config.SelectedRelease };
                case "launch-attach":
                case "launch-started":
                case "launch-complete":
                case "runtime-inspect":
                case "runtime-recover":
                    if (request.Selection is not null) VersionedReleasePaths.Bind(config, request.Selection);
                    if (request.Command == "runtime-inspect") return new() { CorrelationId = request.CorrelationId, Success = true, Runtime = RuntimeStore.Observe(config) };
                    if (request.Command == "runtime-recover")
                    {
                        if (!peer!.Administrator) throw new UnauthorizedAccessException("Managed runtime recovery requires a local administrator.");
                        RuntimeRecoveryRequest.Validate(config, request.ConfirmStopped, request.ServiceVersion);
                        var recovered = request.ServiceVersion is not null
                            ? RuntimeServiceState.ConfirmSelection(config, peer!, request.ServiceVersion)
                            : RuntimeStore.Recover(config, peer!, request.ConfirmStopped);
                        return new() { CorrelationId = request.CorrelationId, Success = true, Runtime = recovered };
                    }
                    var launchTicket = request.RuntimeTicket ?? throw new InvalidDataException("Runtime ticket is required.");
                    if (request.Command == "launch-attach")
                    {
                        var expectedHost = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
                        var launch = RuntimeStore.Attach(config, launchTicket, peer!, expectedHost);
                        return new() { CorrelationId = request.CorrelationId, Success = true, RuntimeLaunch = launch };
                    }
                    RuntimeStore.Report(config, launchTicket, peer!, request.Command == "launch-complete", request.PayloadPid);
                    return new() { CorrelationId = request.CorrelationId, Success = true, Runtime = RuntimeStore.Observe(config) };
                case "doctor":
                    var report = await LauncherDoctor.RunAsync(configPath, request.OnlineCheck, cancellationToken, agentContext: true);
                    return new ManagedAgentResponse { CorrelationId = request.CorrelationId, Success = report.Healthy, DoctorReport = report, AgentVersion = AgentVersion() };
                case "catalog":
                    using (var http = SecureHttpClientFactory.Create(config))
                    {
                        var catalog = await CatalogResolver.DownloadCatalogAsync(config, http, cancellationToken: cancellationToken);
                        return new ManagedAgentResponse { CorrelationId = request.CorrelationId, Success = true, Catalog = catalog, AgentVersion = AgentVersion() };
                    }
                case "check":
                        using (var http = SecureHttpClientFactory.Create(config))
                        {
                        await CatalogResolver.ResolveAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), cancellationToken);
                        var document = await ManifestDownloader.DownloadAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), cancellationToken);
                        var projectStatus = await ManagedProjectStatusInspector.InspectAsync(config, document.Manifest, cancellationToken);
                        var checkedResponse = Success(request, identity, "checked", $"Release {document.Manifest.Version} metadata, files and signatures are valid.", progress, projectStatus, config.SelectedRelease);
                        checkedResponse.Runtime = RuntimeStore.Observe(config);
                        return checkedResponse;
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
                    return Success(request, identity, "completed", config.RepairMode ? "Repair completed." : "Update completed.", progress, completedStatus, config.SelectedRelease);
                case "rollback":
                    if (request.Selection is not null) VersionedReleasePaths.Bind(config, request.Selection);
                    var backup = BackupManager.List(config.BackupDir).FirstOrDefault();
                    if (backup.BackupRoot is null) return Error(request, "no-backup", "No rollback backup is available.", identity);
                    await BackupManager.RestoreAsync(
                            backup.BackupRoot,
                            config.InstallDir,
                            config.InstalledManifestPath,
                            config.InstallStatePath,
                            message => progress.Add(new ManagedAgentProgress("Rollback", DiagnosticRedactor.Redact(message), null)),
                            cancellationToken);
                    return Success(request, identity, "completed", "Rollback completed.", progress);
                case "service-run":
                    var serviceExit = await ServiceRunner.RunOnceAsync(config, cancellationToken);
                    return serviceExit == 0
                        ? Success(request, identity, "completed", "One managed service cycle completed.", progress)
                        : Error(request, "service-failed", "Managed service cycle failed.", identity);
                case "diagnostics":
                    var output = Path.Combine(layout.LogRoot, $"agent-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                    await DiagnosticsExporter.ExportAsync(configPath, output, cancellationToken, agentContext: true);
                    return Success(request, identity, "completed", $"Diagnostics written: {output}", progress);
                default:
                    return Error(request, "unsupported", "Agent command handler is unavailable.", identity);
            }
        }
        catch (RuntimeBlockedException ex)
        {
            return new() { CorrelationId=request.CorrelationId, Success=false, Status=ex.Observation.Code, Message=ex.Observation.Message, Runtime=ex.Observation, AgentVersion=AgentVersion() };
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
        if (string.IsNullOrWhiteSpace(config.DistributionServerUrl) && !string.IsNullOrWhiteSpace(requestedProjectId) && selected is null && !requestedProjectId.Equals(config.ProjectId, StringComparison.OrdinalIgnoreCase))
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
        ManagedProjectStatus? projectStatus = null,
        ReleaseSelection? selectedRelease = null) => new()
        {
            CorrelationId = request.CorrelationId,
            Success = true,
            Status = status,
            Message = message,
            AgentVersion = AgentVersion(),
            ClientIdentity = identity,
            Progress = progress,
            ProjectStatus = projectStatus,
            SelectedRelease = selectedRelease
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
