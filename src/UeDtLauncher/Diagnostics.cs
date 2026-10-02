using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record DoctorCheck(string Name, bool Success, string Message)
{
    public string? State { get; init; }
    public string? Code { get; init; }
    public string? Subject { get; init; }
    public string? ActionOwner { get; init; }
    public string? NextAction { get; init; }
}
public sealed record DoctorReport(
    string GeneratedUtc,
    bool Healthy,
    string LauncherVersion,
    string OperatingSystem,
    IReadOnlyList<DoctorCheck> Checks)
{
    public string? PreparationState { get; init; }
    public string? SupportId { get; init; }
    public DoctorTarget? Target { get; init; }
}

public static class LauncherDoctor
{
    public static async Task<DoctorReport> RunAsync(
        string configPath,
        bool online,
        CancellationToken cancellationToken = default,
        bool agentContext = false,
        DoctorTarget? target = null)
    {
        var checks = new List<DoctorCheck>();
        LauncherConfig? config = null;
        try
        {
            config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken, readOnly: true);
            if (!string.IsNullOrWhiteSpace(config.Security.CredentialName)) DeviceCredentials.ValidateIdentifier(config.Security.CredentialName);
            target?.Apply(config);
            LauncherConfigValidator.Validate(config);
            checks.Add(new DoctorCheck("config", true, $"schemaVersion {config.SchemaVersion}"));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            config = null;
            checks.Add(new DoctorCheck("config", false, "런처 설정을 읽거나 검증할 수 없습니다."));
        }
        if (config is not null)
        {
            if (config.IsManagedDeployment && !agentContext)
            {
                try
                {
                    target = DoctorTarget.From(config);
                    var report = await new ManagedAgentClient().DoctorAsync(online, cancellationToken, target);
                    checks[0] = DoctorPresentation.Normalize(checks[0], "client");
                    return DoctorPresentation.Complete(report with { Checks = checks.Concat(report.Checks).ToArray(), Target = target });
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or OperationCanceledException or InvalidOperationException)
                {
                    checks.Add(DoctorPresentation.Failure("agent", ex, "client"));
                    return DoctorPresentation.Complete(new(DateTimeOffset.UtcNow.ToString("O"), false,
                        typeof(LauncherDoctor).Assembly.GetName().Version?.ToString() ?? "0", Environment.OSVersion.ToString(),
                        checks.Select(c => DoctorPresentation.Normalize(c, "client")).ToArray()) { Target = target });
                }
            }
            if (config.RuntimeData?.Enabled == true)
            {
                checks.Add(new("runtime-data-policy", true, "UE per-user/per-release policy; application-specific writers may ignore UserDir. Payload rollback does not restore user data."));
                if (agentContext && config.IsManagedDeployment)
                    checks.Add(new("runtime-data-host-preflight", false, "사용자 세션의 저장 경로 쓰기 검사는 실행 직전에 수행합니다.") { State = "deferred", Subject = "user-host" });
                else
                {
                    try
                    {
                        RuntimeDataPolicy.InspectUserRootReadOnly(config);
                        checks.Add(new("runtime-data-permissions", true, "Read-only owner/permission checks passed; actual writes are checked by runtime-host at launch."));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { checks.Add(new("runtime-data-permissions", false, "User runtime directory is unavailable or unsafe; ask the administrator to check configuration and permissions.")); }
                }
            }
            var credential = string.IsNullOrWhiteSpace(config.Security.CredentialName) ? null : DeviceCredentials.Inspect(config.Security.CredentialName, DeviceCredentials.StorageLayout(config));
            var credentialConfigured = credential is null || (credential.Ready && credential.Type == config.Security.AuthenticationMode);
            checks.Add(new DoctorCheck("credential", credentialConfigured,
                credentialConfigured ? "credential format, ownership and identity access passed" : "credential is missing, inaccessible, unsafe or has the wrong type"));
            var publicKeys = config.Security.TrustedSigningKeys.Select(key => key.PublicKeyPath).ToList();
            if (publicKeys.Count == 0 && config.SchemaVersion < 3)
                publicKeys.AddRange(new[] { config.CatalogPublicKeyPath, config.ManifestPublicKeyPath }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!));
            var keysAvailable = !config.RequireSignedManifests || publicKeys.Count > 0;
            foreach (var path in publicKeys)
            {
                try { using var key = System.Security.Cryptography.ECDsa.Create(); key.ImportFromPem(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException) { keysAvailable = false; }
            }
            checks.Add(new DoctorCheck("signing-keys", keysAvailable,
                keysAvailable ? "trusted public keys are available" : "one or more trusted public keys are missing"));
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(config.StateRootDir))!);
                checks.Add(new DoctorCheck("disk-space", drive.AvailableFreeSpace > 512L * 1024 * 1024,
                    $"{drive.AvailableFreeSpace / (1024 * 1024)} MiB free"));
            }
            catch (Exception ex) { checks.Add(new DoctorCheck("disk-space", false, DiagnosticRedactor.Redact(ex.Message))); }
            if (online)
            {
                try
                {
                    using var http = SecureHttpClientFactory.Create(config);
                    var catalog = await CatalogResolver.DownloadCatalogAsync(config, http, cancellationToken: cancellationToken);
                    checks.Add(new DoctorCheck("catalog-online", true, "catalog authentication and signature validation passed"));
                    checks.Add(DoctorPresentation.ReleaseReadiness(config, catalog));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested) { checks.Add(DoctorPresentation.Failure("catalog-online", ex, agentContext ? "agent" : "client")); }
            }
        }
        if (config?.IsManagedDeployment == false) checks.Add(new("agent", true, "로컬 모드에서는 업데이트 서비스가 필요하지 않습니다.") { State = "not-applicable", Code = "local-mode", Subject = "client" });
        if (!online) checks.Add(new("catalog-online", false, "온라인 연결은 검사하지 않았습니다.") { State = "deferred", Code = "online-not-checked", Subject = agentContext ? "agent" : "client", ActionOwner = "user", NextAction = "온라인 점검을 실행해 주세요." });
        checks = checks.Select(check => DoctorPresentation.Normalize(check, agentContext ? "agent" : "client")).ToList();
        return DoctorPresentation.Complete(new DoctorReport(
            DateTimeOffset.UtcNow.ToString("O"),
            checks.All(check => check.State != "failed"),
            typeof(LauncherDoctor).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Environment.OSVersion.ToString(),
            checks) { Target = config is null ? null : DoctorTarget.From(config) });
    }
}

public static class DiagnosticsExporter
{
    public static async Task<string> ExportAsync(
        string configPath,
        string outputPath,
        CancellationToken cancellationToken = default,
        bool agentContext = false)
    {
        var config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken, readOnly: true);
        var doctor = await LauncherDoctor.RunAsync(configPath, online: false, cancellationToken, agentContext);
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        using var archive = ZipFile.Open(fullOutput, ZipArchiveMode.Create);
        AddText(archive, "doctor.json", DiagnosticRedactor.Redact(JsonSerializer.Serialize(doctor, JsonFiles.Options)));
        AddText(archive, "config.sanitized.json", DiagnosticRedactor.Redact(await File.ReadAllTextAsync(Path.GetFullPath(configPath), cancellationToken)));
        // Client-side managed bundles include only display config and Agent diagnostics.
        // Protected state and logs may be exported by the Agent's administrator path.
        if (!config.IsManagedDeployment || agentContext)
        {
            AddFileIfPresent(archive, config.InstallStatePath, "state/install-state.json");
            AddFileIfPresent(archive, config.InstalledManifestPath, "state/installed-manifest.json");
        }
        if ((!config.IsManagedDeployment || agentContext) && Directory.Exists(config.LogDir))
        {
            foreach (var log in Directory.EnumerateFiles(config.LogDir, "launcher-*.*")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
            {
                AddText(archive, "logs/" + Path.GetFileName(log), DiagnosticRedactor.Redact(await File.ReadAllTextAsync(log, cancellationToken)));
            }
        }
        return fullOutput;
    }

    private static void AddFileIfPresent(ZipArchive archive, string source, string entryName)
    {
        if (!File.Exists(source)) return;
        AddText(archive, entryName, DiagnosticRedactor.Redact(File.ReadAllText(source)));
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}

public static class CrashReporter
{
    private static string? _logRoot;
    private static int _reporting;

    public static void Install(string logRoot)
    {
        _logRoot = Path.GetFullPath(logRoot);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception"), "unhandled-domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "unobserved-task");
            args.SetObserved();
        };
    }

    public static string? Report(Exception exception, string source)
    {
        if (Interlocked.Exchange(ref _reporting, 1) != 0) return null;
        try
        {
            var root = _logRoot ?? Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");
            var payload = new
            {
                timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                source,
                processId = Environment.ProcessId,
                launcherVersion = typeof(CrashReporter).Assembly.GetName().Version?.ToString(),
                operatingSystem = Environment.OSVersion.ToString(),
                errorType = exception.GetType().FullName,
                message = DiagnosticRedactor.Redact(exception.Message),
                stackTrace = DiagnosticRedactor.Redact(exception.StackTrace)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonFiles.Options));
            return path;
        }
        catch { return null; }
        finally { Volatile.Write(ref _reporting, 0); }
    }
}
