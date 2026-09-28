using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UeDtLauncher.Distribution;

/// <summary>Bounded, identity-free aggregates. DB elapsed includes native lock waits, not a separate wait estimate.</summary>
public static class DistributionPerformance
{
    public const string MeterName = "UeDtLauncher.Distribution";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> DatabaseDuration = Meter.CreateHistogram<double>("distribution.database.duration", "ms");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("distribution.http.duration", "ms");
    private static readonly Histogram<double> SequenceWaitDuration = Meter.CreateHistogram<double>("distribution.sequence.wait", "ms");
    private static readonly Counter<long> Busy = Meter.CreateCounter<long>("distribution.database.busy");
    private static long databaseOperations, databaseTicks, requests, requestTicks, sequenceWaitTicks, databaseBusy, policyCompilations, policyReuses, assetHits, assetMisses;
    private static long authenticationOperations, authenticationTicks;

    public static IDisposable MeasureAuthentication() => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref authenticationOperations);
        Interlocked.Add(ref authenticationTicks, elapsed.Ticks);
    });

    public static IDisposable MeasureDatabase(string operation) => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref databaseOperations);
        Interlocked.Add(ref databaseTicks, elapsed.Ticks);
        DatabaseDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
    });
    public static IDisposable MeasureRequest() => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref requests);
        Interlocked.Add(ref requestTicks, elapsed.Ticks);
        RequestDuration.Record(elapsed.TotalMilliseconds);
    });
    public static IDisposable MeasureSequenceWait() => new Measurement(elapsed =>
    {
        Interlocked.Add(ref sequenceWaitTicks, elapsed.Ticks);
        SequenceWaitDuration.Record(elapsed.TotalMilliseconds);
    });
    public static void RecordDatabaseBusy(int sqliteErrorCode)
    {
        if (sqliteErrorCode is not (5 or 6)) return;
        Interlocked.Increment(ref databaseBusy);
        Busy.Add(1);
    }
    public static void RecordPolicyCompilation() => Interlocked.Increment(ref policyCompilations);
    public static void RecordPolicyReuse() => Interlocked.Increment(ref policyReuses);
    public static void RecordAssetHit() => Interlocked.Increment(ref assetHits);
    public static void RecordAssetMiss() => Interlocked.Increment(ref assetMisses);
    public static object Snapshot() => new
    {
        requests = Interlocked.Read(ref requests),
        httpElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref requestTicks)).TotalMilliseconds,
        databaseOperations = Interlocked.Read(ref databaseOperations),
        databaseElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref databaseTicks)).TotalMilliseconds,
        sequenceWaitMs = TimeSpan.FromTicks(Interlocked.Read(ref sequenceWaitTicks)).TotalMilliseconds,
        databaseBusyErrors = Interlocked.Read(ref databaseBusy),
        authenticationOperations = Interlocked.Read(ref authenticationOperations),
        authenticationElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref authenticationTicks)).TotalMilliseconds,
        policyCompilations = Interlocked.Read(ref policyCompilations), policyReuses = Interlocked.Read(ref policyReuses),
        assetCacheHits = Interlocked.Read(ref assetHits), assetCacheMisses = Interlocked.Read(ref assetMisses)
    };
    private sealed class Measurement(Action<TimeSpan> record) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose() => record(Stopwatch.GetElapsedTime(started));
    }
}
