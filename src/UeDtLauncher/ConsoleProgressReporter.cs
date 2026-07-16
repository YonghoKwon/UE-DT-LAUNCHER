using System.Diagnostics;

namespace UeDtLauncher;

/// <summary>
/// Renders <see cref="LauncherProgress"/> events to the console for the CLI <c>run</c> path.
/// When stdout is an interactive terminal it draws a single in-place progress bar that updates
/// with <c>\r</c>; when stdout is redirected (file, journald, NSSM) it falls back to plain
/// line output with no carriage-return spam.
/// </summary>
public sealed class ConsoleProgressReporter
{
    private readonly bool _interactive;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private long _lastBytes = -1;
    private long _lastSampleMs;
    private double _bytesPerSecond;
    private long _lastRedrawMs = long.MinValue;

    // Most recent per-file (n/m) info, captured from "Download" stage events.
    private int _fileIndex;
    private int _fileCount;

    // Whether an in-place bar line is currently "open" (no trailing newline yet).
    private bool _barOpen;
    private int _previousLineLength;

    public ConsoleProgressReporter()
    {
        _interactive = !Console.IsOutputRedirected;
    }

    /// <summary>Callback compatible with the engine's <c>Action&lt;LauncherProgress&gt;</c>.</summary>
    public void Report(LauncherProgress progress)
    {
        switch (progress.Stage)
        {
            case "DownloadProgress":
                HandleDownloadProgress(progress);
                break;
            case "Download":
                HandleDownloadStage(progress);
                break;
            default:
                HandleOtherStage(progress);
                break;
        }
    }

    /// <summary>Closes an open in-place bar line with a newline. Call once when the run ends.</summary>
    public void Finish()
    {
        if (_barOpen)
        {
            Console.Out.Write(Environment.NewLine);
            _barOpen = false;
            _previousLineLength = 0;
        }
    }

    private void HandleDownloadProgress(LauncherProgress progress)
    {
        var bytes = progress.BytesDownloaded ?? 0;
        var total = progress.TotalBytes ?? 0;
        var fileIndex = progress.FileIndex ?? _fileIndex;
        var fileCount = progress.FileCount ?? _fileCount;
        _fileIndex = fileIndex;
        _fileCount = fileCount;

        UpdateSpeed(bytes);

        if (!_interactive)
        {
            // Plain output already conveys progress via the per-file "Download" line.
            return;
        }

        var nowMs = _stopwatch.ElapsedMilliseconds;
        if (_barOpen && nowMs - _lastRedrawMs < 100)
        {
            return; // throttle to ~10 redraws/sec
        }
        _lastRedrawMs = nowMs;

        var percent = progress.Percent ?? 0;
        var line = FormatProgressLine(percent, fileIndex, fileCount, _bytesPerSecond, bytes, total);
        WriteInPlace(line);
    }

    private void HandleDownloadStage(LauncherProgress progress)
    {
        // Engine sends "(n/m) path"; parse the n/m so the bar shows the right current file.
        ParseFileIndex(progress.Message);

        if (_interactive)
        {
            // Do not print a separate line; the in-place bar carries the current file.
            return;
        }

        WriteLineSafe(FormatPlainLine(progress));
    }

    private void HandleOtherStage(LauncherProgress progress)
    {
        if (_barOpen)
        {
            // Finish the open in-place bar line before printing normal output.
            Console.Out.Write(Environment.NewLine);
            _barOpen = false;
            _previousLineLength = 0;
        }

        WriteLineSafe(FormatPlainLine(progress));
    }

    private void WriteInPlace(string line)
    {
        var padded = line;
        if (line.Length < _previousLineLength)
        {
            padded = line + new string(' ', _previousLineLength - line.Length);
        }
        _previousLineLength = line.Length;
        _barOpen = true;
        Console.Out.Write('\r');
        Console.Out.Write(padded);
    }

    private void WriteLineSafe(string line) => Console.Out.WriteLine(line);

    private void UpdateSpeed(long bytes)
    {
        var nowMs = _stopwatch.ElapsedMilliseconds;
        if (_lastBytes < 0 || bytes < _lastBytes)
        {
            // First sample (defer speed until we have two points). The engine reports cumulative,
            // monotonic byte counts, so bytes-going-backwards is only a defensive guard.
            _lastBytes = bytes;
            _lastSampleMs = nowMs;
            return;
        }

        var elapsedMs = nowMs - _lastSampleMs;
        if (elapsedMs <= 0) return;

        var deltaBytes = bytes - _lastBytes;
        var instantaneous = deltaBytes / (elapsedMs / 1000.0);
        _bytesPerSecond = _bytesPerSecond <= 0 ? instantaneous : _bytesPerSecond * 0.6 + instantaneous * 0.4;
        _lastBytes = bytes;
        _lastSampleMs = nowMs;
    }

    private void ParseFileIndex(string message)
    {
        // Expected shape: "(n/m) path"
        if (string.IsNullOrEmpty(message) || message[0] != '(') return;
        var close = message.IndexOf(')');
        if (close <= 1) return;
        var inside = message.AsSpan(1, close - 1);
        var slash = inside.IndexOf('/');
        if (slash <= 0) return;
        if (int.TryParse(inside.Slice(0, slash), out var n)) _fileIndex = n;
        if (int.TryParse(inside.Slice(slash + 1), out var m)) _fileCount = m;
    }

    private static string FormatPlainLine(LauncherProgress progress) =>
        progress.Percent.HasValue
            ? $"[{progress.Stage}] {progress.Message} ({progress.Percent:0}%)"
            : $"[{progress.Stage}] {progress.Message}";

    /// <summary>Pure renderer for the single progress line (no leading <c>\r</c> or padding).</summary>
    internal static string FormatProgressLine(double percent, int fileIndex, int fileCount, double bytesPerSecond, long bytesDownloaded, long totalBytes)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var bar = RenderBar(clamped, 20);
        var speed = bytesPerSecond > 0 ? DiskSpace.FormatBytes((long)bytesPerSecond) : "0 B";
        var done = DiskSpace.FormatBytes(Math.Max(0, bytesDownloaded));
        var total = DiskSpace.FormatBytes(Math.Max(0, totalBytes));
        return $"[{bar}] {clamped,3:0}%  파일 {fileIndex}/{fileCount}  {speed}/s  {done} / {total}";
    }

    /// <summary>Pure fixed-width progress bar, e.g. <c>####----------------</c>.</summary>
    internal static string RenderBar(double percent, int width)
    {
        if (width <= 0) return string.Empty;
        var clamped = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(clamped / 100.0 * width, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, width);
        return new string('#', filled) + new string('-', width - filled);
    }
}
