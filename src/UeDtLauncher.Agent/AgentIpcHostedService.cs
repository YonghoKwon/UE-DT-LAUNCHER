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
    private readonly SemaphoreSlim _connections = new(16, 16);
    private readonly List<Task> _clients = [];
    private OperationRegistry _operations = null!;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var layout = ManagedLauncherPathLayout.Current();
        _operations = new OperationRegistry(Path.Combine(layout.StateRoot, "operations"));
        foreach (var path in new[] { layout.ConfigRoot, layout.StateRoot, layout.AppsRoot, layout.LogRoot, layout.CredentialRoot })
            Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows())
            await RunWindowsPipeAsync(stoppingToken);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            await RunUnixSocketAsync(stoppingToken);
        else
            throw new PlatformNotSupportedException("Managed Agent IPC supports Windows, Linux, and macOS only.");
        await Task.WhenAll(_clients);
    }

    [SupportedOSPlatform("windows")]
    private async Task RunWindowsPipeAsync(CancellationToken stoppingToken)
    {
        var endpoint = ManagedAgentProtocol.ResolveEndpoint();
        logger.LogInformation("Agent IPC listening on named pipe {Endpoint}", endpoint);
        while (!stoppingToken.IsCancellationRequested)
        {
            await _connections.WaitAsync(stoppingToken);
            var pipe = CreateSecuredPipe(endpoint);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                var identity = TryGetClientIdentity(pipe);
                _clients.RemoveAll(task => task.IsCompleted);
                _clients.Add(HandleOwnedClientAsync(pipe, identity, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe.Dispose(); _connections.Release();
                break;
            }
            catch (Exception ex)
            {
                pipe.Dispose(); _connections.Release();
                logger.LogWarning(ex, "Agent named-pipe request failed");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateSecuredPipe(string endpoint)
    {
        var security = new PipeSecurity();
        // The service/console identity must be able to create additional server instances.
        // Authenticated clients retain only ReadWrite, not CreateNewInstance/FullControl.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
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
            16,
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
                await _connections.WaitAsync(stoppingToken);
                Socket client;
                try { client = await listener.AcceptAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { _connections.Release(); break; }
                var stream = new NetworkStream(client, ownsSocket: true);
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
                _clients.RemoveAll(task => task.IsCompleted);
                _clients.Add(HandleOwnedClientAsync(stream, peer, stoppingToken));
            }
        }
        finally
        {
            if (File.Exists(endpoint)) File.Delete(endpoint);
        }
    }

    private async Task HandleOwnedClientAsync(Stream stream, RuntimeIdentity? peer, CancellationToken stoppingToken)
    {
        try { await HandleClientAsync(stream, peer, stoppingToken); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException)
        { logger.LogDebug("Agent connection ended: {Type}", ex.GetType().Name); }
        catch (Exception ex) { logger.LogWarning("Agent connection rejected: {Type}", ex.GetType().Name); }
        finally { await stream.DisposeAsync(); _connections.Release(); }
    }

    private async Task HandleClientAsync(Stream stream, RuntimeIdentity? peer, CancellationToken cancellationToken)
    {
        var identity = peer?.Owner;
        ManagedAgentRequest? request = null;
        ManagedAgentResponse response;
        try
        {
            request = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
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

        response.AgentCapabilities = [ManagedAgentProtocol.RuntimeCapability, RollbackPreviewService.Capability, RuntimeDataPolicy.Capability, DoctorPresentation.Capability, OperationRegistry.Capability];
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
        var channel = Channel.CreateBounded<ManagedAgentProgress>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });
        var writer = Task.Run(async () =>
        {
            try { await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken))
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
            } } catch (IOException) { /* A disconnected observer never cancels the operation. */ }
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
        string? resumedDigest = null;
        if (request.Command is "operation-status" or "operation-cancel" or "operation-discard")
        {
            if (peer is null) return Error(request, "rejected", "Authenticated OS peer is required.", identity);
            try
            {
                var id = request.OperationId ?? throw new InvalidDataException("Operation ID is required.");
                var operation = request.Command switch
                {
                    "operation-cancel" => _operations.Cancel(id, peer),
                    "operation-discard" => _operations.Discard(id, peer),
                    _ => _operations.Inspect(id, peer)
                };
                return new() { CorrelationId = request.CorrelationId, Success = true, Status = operation.Phase, Operation = operation };
            }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { return Error(request, "operation-rejected", "Operation unavailable or not owned by this session.", identity); }
        }
        if (request.Command == "operation-resume")
        {
            if (peer is null) return Error(request, "rejected", "Authenticated OS peer is required.", identity);
            var previous = _operations.Inspect(request.OperationId ?? "", peer);
            if (previous.Phase is not ("Interrupted" or "Cancelled" or "Failed") || previous.Selection is null || previous.ManifestSha256 is null)
                return Error(request, "resume-rejected", "Operation cannot be resumed.", identity);
            request.Command = previous.Command; request.ProjectId = previous.Selection.ProjectId; request.Selection = previous.Selection;
            resumedDigest = previous.ManifestSha256;
        }
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
            if (request.Command.Equals("doctor", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Selection is not null || request.ProjectId != request.DoctorTarget?.ProjectId)
                    return Error(request, "invalid-request", "Diagnostic target does not match request.", identity);
                var report = await LauncherDoctor.RunAsync(configPath, request.OnlineCheck, cancellationToken, agentContext: true, target: request.DoctorTarget);
                return new ManagedAgentResponse { CorrelationId = request.CorrelationId, Success = report.Healthy, DoctorReport = report with { SupportId = request.CorrelationId }, AgentVersion = AgentVersion() };
            }
            if (!File.Exists(configPath)) return Error(request, "not-configured", $"Managed config was not found: {configPath}", identity);
            var config = await LoadProjectConfigAsync(configPath, request.ProjectId, cancellationToken);
            config.ExpectedResumeManifestSha256 = resumedDigest;
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
                    if (config.RuntimeData?.Enabled == true && request.ClientCapabilities?.Contains(RuntimeDataPolicy.Capability) != true)
                        return Error(request, "client-upgrade-required", "저장 경로 분리를 지원하는 런처로 업데이트해 주세요.", identity);
                    using (var http = SecureHttpClientFactory.Create(config)) await CatalogResolver.ResolveAsync(config, http, cancellationToken: cancellationToken);
                    await LaunchPolicy.VerifyOnlineAsync(config, cancellationToken);
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
                        if (config.RuntimeData?.Enabled == true && request.ClientCapabilities?.Contains(RuntimeDataPolicy.Capability) != true)
                            return Error(request, "client-upgrade-required", "저장 경로 분리를 지원하는 런처로 업데이트해 주세요.", identity);
                        var expectedHost = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
                        var launch = RuntimeStore.Attach(config, launchTicket, peer!, expectedHost);
                        return new() { CorrelationId = request.CorrelationId, Success = true, RuntimeLaunch = launch };
                    }
                    RuntimeStore.Report(config, launchTicket, peer!, request.Command == "launch-complete", request.PayloadPid);
                    return new() { CorrelationId = request.CorrelationId, Success = true, Runtime = RuntimeStore.Observe(config) };
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
                        var checkedResponse = Success(request, identity, "checked", $"Release {document.Manifest.Version}: missing {projectStatus.MissingFiles}, changed {projectStatus.ChangedFiles}. Metadata signature verified.", progress, projectStatus, config.SelectedRelease);
                        checkedResponse.Runtime = RuntimeStore.Observe(config);
                        return checkedResponse;
                    }
                case "update":
                case "repair":
                    using (var operation = _operations.Begin(Guid.TryParseExact(request.CorrelationId,"N",out _)?request.CorrelationId:Guid.NewGuid().ToString("N"), peer!, request.Selection, cancellationToken, request.Command))
                    {
                    config.LaunchAfterUpdate = false;
                    config.RepairMode = request.Command.Equals("repair", StringComparison.OrdinalIgnoreCase);
                    LauncherEngine? runningEngine = null;
                    try
                    {
                    using (var http = SecureHttpClientFactory.Create(config))
                    {
                        await CatalogResolver.ResolveAsync(config, http, (stage, message, percent) =>
                            AddProgress(new LauncherProgress(stage, message, percent)), operation.Token);
                        operation.Bind(config.SelectedRelease);
                        using var engine = new LauncherEngine(config, value =>
                        {
                            if (runningEngine?.ManifestSha256 is { } digest) operation.Bind(config.SelectedRelease, digest);
                            if (value.Stage == "Apply") operation.Phase("Applying");
                            else if (value.Stage == "Download") operation.Phase("Downloading");
                            AddProgress(value);
                        }, new FileLogger(layout.LogRoot), echoToConsole: false);
                        runningEngine = engine;
                        await engine.RunAsync(operation.Token);
                        }
                    operation.Finish(committed: true);
                    var installed = await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath, cancellationToken);
                    var completedStatus = await ManagedProjectStatusInspector.InspectAsync(config, installed, cancellationToken);
                    var result = Success(request, identity, "completed", config.RepairMode ? "Repair completed." : "Update completed.", progress, completedStatus, config.SelectedRelease);
                    result.Operation = operation.Status; return result;
                    }
                    catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
                    {
                        operation.Finish(runningEngine?.InstallationCommitted == true);
                        return new() { CorrelationId = request.CorrelationId, Success = operation.Status.Phase == "Completed", Status = operation.Status.Phase, Operation = operation.Status, SelectedRelease = config.SelectedRelease };
                    }
                    catch { operation.Finish(runningEngine?.InstallationCommitted == true, failed: true); throw; }
                    }
                case "rollback-preview":
                    if (request.Selection is not null) VersionedReleasePaths.Bind(config, request.Selection);
                    var preview = await RollbackPreviewService.ReadAsync(config, cancellationToken);
                    return new() { CorrelationId=request.CorrelationId, Success=true, Status="preview", RollbackPreview=preview, SelectedRelease=config.SelectedRelease };
                case "rollback":
                    if (request.Selection is not null) VersionedReleasePaths.Bind(config, request.Selection);
                    if (request.ExpectedBackupId is not null || request.ExpectedBackupFingerprint is not null)
                    {
                        if (request.ExpectedBackupId is null || request.ExpectedBackupFingerprint is null) throw new InvalidDataException("Both backup expectation fields are required.");
                        await RollbackPreviewService.RestoreExpectedAsync(config, request.ExpectedBackupId, request.ExpectedBackupFingerprint,
                            message => AddProgress(new LauncherProgress("Rollback", message, null)), cancellationToken);
                        ManagedProjectStatus restoredStatus;
                        if (File.Exists(config.InstalledManifestPath))
                        {
                            var restoredManifest=await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath,cancellationToken);
                            restoredStatus=(await ManagedProjectStatusInspector.InspectAsync(config,restoredManifest,cancellationToken)) with { AvailableVersion=null };
                        }
                        else restoredStatus=new(false,null,null,true,0,0,BackupManager.List(config.BackupDir).Count>0);
                        return Success(request, identity, "completed", "Confirmed backup restored.", progress, restoredStatus, config.SelectedRelease);
                    }
                    var backup = BackupManager.List(config.BackupDir).FirstOrDefault();
                    if (backup.BackupRoot is null) return Error(request, "no-backup", "No rollback backup is available.", identity);
                    await BackupManager.RestoreAsync(
                            backup.BackupRoot,
                            config,
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
            logger.LogError("Managed Agent command {Command} failed. SupportId {CorrelationId}; code {Code}; {Message}", request.Command, request.CorrelationId, LauncherFailure.Code(ex), DiagnosticRedactor.Redact(ex.Message));
            CrashReporter.Report(ex, "agent-command");
            var failure=Error(request, "failed", DiagnosticRedactor.Redact(ex.Message), identity);
            failure.ErrorCode=LauncherFailure.Code(ex);
            return failure;
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
            ErrorCode = status,
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
