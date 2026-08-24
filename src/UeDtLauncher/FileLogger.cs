using System.Text.Json;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public enum LauncherLogLevel { Debug, Information, Warning, Error, Critical }

public sealed record StructuredLauncherLog(
    string TimestampUtc,
    string Level,
    string EventId,
    string OperationId,
    string Stage,
    string Message,
    string? ProjectId,
    string? Version,
    double? DurationMs,
    string? ErrorType);

/// <summary>Daily human and JSONL logs with bounded retention and central redaction.</summary>
public sealed class FileLogger
{
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const long MaxDirectoryBytes = 200L * 1024 * 1024;
    private readonly string _logDir;
    private readonly object _gate = new();
    private readonly int _keepDays;

    public FileLogger(string logDir, int keepDays = 30)
    {
        _logDir = Path.GetFullPath(logDir);
        _keepDays = Math.Max(1, keepDays);
        Directory.CreateDirectory(_logDir);
        PruneOldLogs();
    }

    public string CurrentLogPath => ResolveCurrentPath("log");
    public string CurrentJsonLogPath => ResolveCurrentPath("jsonl");

    public void Log(string stage, string message) => Log(LauncherLogLevel.Information, stage, message);

    public void Log(
        LauncherLogLevel level,
        string stage,
        string message,
        string? eventId = null,
        string? operationId = null,
        string? projectId = null,
        string? version = null,
        double? durationMs = null,
        Exception? exception = null)
    {
        var safeStage = DiagnosticRedactor.Redact(stage);
        var safeMessage = DiagnosticRedactor.Redact(message);
        var timestamp = DateTimeOffset.UtcNow;
        var record = new StructuredLauncherLog(
            timestamp.ToString("O"),
            level.ToString(),
            eventId ?? safeStage.ToLowerInvariant().Replace(' ', '-'),
            operationId ?? Guid.NewGuid().ToString("N"),
            safeStage,
            safeMessage,
            DiagnosticRedactor.RedactNullable(projectId),
            DiagnosticRedactor.RedactNullable(version),
            durationMs,
            exception?.GetType().Name);
        var human = $"{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{safeStage}] {safeMessage}{Environment.NewLine}";
        var json = JsonSerializer.Serialize(record, JsonFiles.Options) + Environment.NewLine;
        lock (_gate)
        {
            TryAppend(CurrentLogPath, human);
            TryAppend(CurrentJsonLogPath, json);
        }
    }

    private string ResolveCurrentPath(string extension)
    {
        var stem = $"launcher-{DateTime.Now:yyyyMMdd}";
        var primary = Path.Combine(_logDir, $"{stem}.{extension}");
        if (!File.Exists(primary) || new FileInfo(primary).Length < MaxFileBytes) return primary;
        for (var index = 1; index < 100; index++)
        {
            var candidate = Path.Combine(_logDir, $"{stem}-{index:00}.{extension}");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxFileBytes) return candidate;
        }
        return Path.Combine(_logDir, $"{stem}-overflow.{extension}");
    }

    private static void TryAppend(string path, string text)
    {
        try { File.AppendAllText(path, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_keepDays);
            var files = Directory.EnumerateFiles(_logDir, "launcher-*.*")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            foreach (var file in files.Where(file => file.LastWriteTime < cutoff)) file.Delete();
            files = files.Where(file => file.Exists).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
            {
                if (total <= MaxDirectoryBytes) break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public static partial class DiagnosticRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var result = BearerPattern().Replace(value, "$1<redacted>");
        result = UrlUserInfoPattern().Replace(result, "$1<redacted>@");
        result = SensitiveJsonPattern().Replace(result, "$1\"<redacted>\"");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) result = result.Replace(profile, "<user-profile>", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public static string? RedactNullable(string? value) => value is null ? null : Redact(value);

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*bearer\\s+|bearer\\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();
    [GeneratedRegex("(?i)(https?://)[^/@\\s]+@")]
    private static partial Regex UrlUserInfoPattern();
    [GeneratedRegex("(?i)(\"(?:token|password|passcode|authorization)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex SensitiveJsonPattern();
}
