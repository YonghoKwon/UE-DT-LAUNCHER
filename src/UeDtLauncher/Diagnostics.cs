using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record DoctorCheck(string Name, bool Success, string Message);
public sealed record DoctorReport(
    string GeneratedUtc,
    bool Healthy,
    string LauncherVersion,
    string OperatingSystem,
    IReadOnlyList<DoctorCheck> Checks);

public static class LauncherDoctor
{
    public static async Task<DoctorReport> RunAsync(
        string configPath,
        bool online,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        LauncherConfig? config = null;
        try
        {
            config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
            checks.Add(new DoctorCheck("config", true, $"schemaVersion {config.SchemaVersion}"));
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck("config", false, DiagnosticRedactor.Redact(ex.Message)));
        }
        if (config is not null)
        {
            var credentialConfigured = string.IsNullOrWhiteSpace(config.Security.CredentialName) || CredentialStore.Exists(config.Security.CredentialName);
            checks.Add(new DoctorCheck("credential", credentialConfigured,
                credentialConfigured ? "credential reference is available" : "credential reference is missing"));
            var keysAvailable = config.Security.TrustedSigningKeys.All(key => File.Exists(key.PublicKeyPath));
            checks.Add(new DoctorCheck("signing-keys", keysAvailable,
                keysAvailable ? "trusted public keys are available" : "one or more trusted public keys are missing"));
            try
            {
                Directory.CreateDirectory(config.StateRootDir);
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
                    await CatalogResolver.ResolveAsync(config, http, cancellationToken: cancellationToken);
                    checks.Add(new DoctorCheck("catalog-online", true, "catalog authentication and signature validation passed"));
                }
                catch (Exception ex) { checks.Add(new DoctorCheck("catalog-online", false, DiagnosticRedactor.Redact(ex.Message))); }
            }
        }
        try
        {
            var response = await new ManagedAgentClient().SendAsync("status", timeout: TimeSpan.FromSeconds(1), cancellationToken: cancellationToken);
            checks.Add(new DoctorCheck("agent", response.Success, response.Message));
        }
        catch (Exception ex) { checks.Add(new DoctorCheck("agent", false, DiagnosticRedactor.Redact(ex.Message))); }
        return new DoctorReport(
            DateTimeOffset.UtcNow.ToString("O"),
            checks.All(check => check.Success || check.Name == "agent"),
            typeof(LauncherDoctor).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            Environment.OSVersion.ToString(),
            checks);
    }
}

public static class DiagnosticsExporter
{
    public static async Task<string> ExportAsync(
        string configPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var config = await LauncherPaths.LoadResolvedAsync(configPath, cancellationToken);
        var doctor = await LauncherDoctor.RunAsync(configPath, online: false, cancellationToken);
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        using var archive = ZipFile.Open(fullOutput, ZipArchiveMode.Create);
        AddText(archive, "doctor.json", DiagnosticRedactor.Redact(JsonSerializer.Serialize(doctor, JsonFiles.Options)));
        AddText(archive, "config.sanitized.json", DiagnosticRedactor.Redact(await File.ReadAllTextAsync(Path.GetFullPath(configPath), cancellationToken)));
        AddFileIfPresent(archive, config.InstallStatePath, "state/install-state.json");
        AddFileIfPresent(archive, config.InstalledManifestPath, "state/installed-manifest.json");
        if (Directory.Exists(config.LogDir))
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
