using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Redactor_RemovesBearerUrlCredentialsJsonSecretsAndUserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = $"Authorization: Bearer secret-token https://user:pass@example.com/a {{\"password\":\"pw\"}} {profile}\\file";

        var redacted = DiagnosticRedactor.Redact(input);

        Assert.DoesNotContain("secret-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pw\"", redacted, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(profile)) Assert.DoesNotContain(profile, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void FileLogger_WritesHumanAndStructuredLogsWithoutSecrets()
    {
        using var temp = new TempDirectory();
        var logger = new FileLogger(temp.Path);

        logger.Log(LauncherLogLevel.Error, "Auth", "Bearer secret-token failed", eventId: "auth.failed", operationId: "op-1");

        var human = File.ReadAllText(logger.CurrentLogPath);
        var json = File.ReadAllText(logger.CurrentJsonLogPath);
        Assert.Contains("auth.failed", json);
        Assert.Contains("op-1", json);
        Assert.Contains("Error", human);
        Assert.DoesNotContain("secret-token", human);
        Assert.DoesNotContain("secret-token", json);
    }

    [Fact]
    public void CrashReporter_WritesSanitizedLocalReport()
    {
        using var temp = new TempDirectory();
        CrashReporter.Install(temp.Path);

        var path = CrashReporter.Report(new InvalidOperationException("Bearer crash-secret failed"), "test");

        Assert.NotNull(path);
        var report = File.ReadAllText(path!);
        Assert.Contains("InvalidOperationException", report);
        Assert.DoesNotContain("crash-secret", report);
    }

    [Fact]
    public async Task DiagnosticsExport_CreatesSanitizedSupportBundle()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "launcher.config.json");
        var logDir = Path.Combine(temp.Path, "logs");
        await JsonFiles.WriteAsync(configPath, new LauncherConfig
        {
            ProjectId = "diagnostics-test",
            InstallDir = "app",
            StateRootDir = ".state",
            LogDir = "logs"
        });
        var logger = new FileLogger(logDir);
        logger.Log("Network", "Authorization: Bearer bundle-secret");
        var output = Path.Combine(temp.Path, "support.zip");

        await DiagnosticsExporter.ExportAsync(configPath, output);

        Assert.True(File.Exists(output));
        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "doctor.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "config.sanitized.json");
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain("bundle-secret", await reader.ReadToEndAsync());
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-diagnostics-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
