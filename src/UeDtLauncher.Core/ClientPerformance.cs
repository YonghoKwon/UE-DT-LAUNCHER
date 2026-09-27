using System.Diagnostics;

namespace UeDtLauncher;

/// <summary>Per-update counters. Hash time is summed worker time; phase times are wall time.</summary>
public sealed record LauncherPerformanceMetrics(
    double HashMilliseconds, double DownloadMilliseconds, double CopyMilliseconds,
    double ApplyMilliseconds, long NetworkBytes, long ReusedBytes);

internal sealed class ClientPerformanceCounters
{
    private long _hashTicks, _downloadTicks, _copyTicks, _applyTicks, _networkBytes, _reusedBytes;
    internal void AddHash(long started) => Interlocked.Add(ref _hashTicks, Stopwatch.GetTimestamp() - started);
    internal void AddDownload(long started) => Interlocked.Add(ref _downloadTicks, Stopwatch.GetTimestamp() - started);
    internal void AddCopy(long started) => Interlocked.Add(ref _copyTicks, Stopwatch.GetTimestamp() - started);
    internal void AddApply(long started) => Interlocked.Add(ref _applyTicks, Stopwatch.GetTimestamp() - started);
    internal void AddNetworkBytes(long bytes) => Interlocked.Add(ref _networkBytes, bytes);
    internal void AddReusedBytes(long bytes) => Interlocked.Add(ref _reusedBytes, bytes);
    internal LauncherPerformanceMetrics Snapshot() => new(
        Milliseconds(ref _hashTicks), Milliseconds(ref _downloadTicks), Milliseconds(ref _copyTicks),
        Milliseconds(ref _applyTicks), Interlocked.Read(ref _networkBytes), Interlocked.Read(ref _reusedBytes));
    private static double Milliseconds(ref long ticks) => Interlocked.Read(ref ticks) * 1000.0 / Stopwatch.Frequency;
}

internal static class BoundedFileWorkers
{
    // Parallel.ForEachAsync cancels siblings on failure and joins every worker before throwing.
    internal static Task RunAsync(int count, int concurrency, Func<int, CancellationToken, ValueTask> work,
        CancellationToken cancellationToken) => Parallel.ForEachAsync(Enumerable.Range(0, count),
        new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken }, work);
}

internal sealed class DownloadProgressAggregator
{
    private readonly object _gate = new();
    private readonly long[] _highWater;
    private readonly bool[] _completed;
    private readonly IReadOnlyList<ManifestFile> _files;
    private readonly Action<LauncherProgress> _report;
    private readonly long _total;
    private readonly Func<long> _timestamp;
    private long _logicalBytes, _lastReport;
    private int _completedCount;

    internal DownloadProgressAggregator(IReadOnlyList<ManifestFile> files, Action<LauncherProgress> report, Func<long>? timestamp = null)
    {
        _files = files; _report = report;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp; _lastReport = _timestamp();
        _highWater = new long[files.Count]; _completed = new bool[files.Count];
        _total = files.Sum(file => Math.Max(0, file.Size));
    }

    internal void Report(int index, long bytes, bool completed = false)
    {
        lock (_gate)
        {
            var value = Math.Clamp(bytes, 0, Math.Max(0, _files[index].Size));
            // A retried request or a restarted Range transfer never counts logical bytes twice.
            if (value > _highWater[index]) { _logicalBytes += value - _highWater[index]; _highWater[index] = value; }
            if (completed && !_completed[index]) { _completed[index] = true; _completedCount++; }
            var now = _timestamp();
            if (_completedCount != _files.Count && Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 200) return;
            _lastReport = now;
            var percent = _total <= 0 ? 65 : 35 + Math.Clamp(_logicalBytes / (double)_total, 0, 1) * 30;
            _report(new LauncherProgress("DownloadProgress", _files[index].Path, percent,
                _logicalBytes, _total, _completedCount, _files.Count));
        }
    }
}
