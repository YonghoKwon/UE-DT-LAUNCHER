namespace UeDtLauncher;

/// <summary>
/// Minimal daily-rolling file logger so update history survives restarts without external dependencies.
/// </summary>
public sealed class FileLogger
{
    private readonly string _logDir;
    private readonly object _gate = new();

    public FileLogger(string logDir, int keepDays = 14)
    {
        _logDir = Path.GetFullPath(logDir);
        Directory.CreateDirectory(_logDir);
        PruneOldLogs(keepDays);
    }

    public string CurrentLogPath => Path.Combine(_logDir, $"launcher-{DateTime.Now:yyyyMMdd}.log");

    public void Log(string stage, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{stage}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(CurrentLogPath, line);
            }
            catch (IOException)
            {
                // Logging must never break an update run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void PruneOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, keepDays));
            foreach (var file in Directory.EnumerateFiles(_logDir, "launcher-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
