using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UeDtLauncher.Distribution;

/// <summary>Bounded, identity-free aggregates. Elapsed phases may overlap.</summary>
public static class DistributionPerformance
{
    public const string MeterName = "UeDtLauncher.Distribution";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> AuthorityReadDuration = Meter.CreateHistogram<double>("distribution.authority.read", "ms");
    private static readonly Histogram<double> AuthorityCommitDuration = Meter.CreateHistogram<double>("distribution.authority.commit", "ms");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("distribution.http.duration", "ms");
    private static readonly Histogram<double> SequenceWaitDuration = Meter.CreateHistogram<double>("distribution.sequence.wait", "ms");
    private static long requests, requestTicks, sequenceWaitTicks, policyCompilations, policyReuses, assetHits, assetMisses;
    private static long authenticationOperations, authenticationTicks, authorityUnavailable, policyUnavailable;
    private static long authorityReads, authorityReadTicks, authorityReadRetries, authorityCommits, authorityCommitTicks;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> phases=new(StringComparer.Ordinal);
    public static IDisposable MeasurePhase(string phase)
    {
        if(phase is not ("policy-read" or "signing" or "catalog-build" or "authority-writer-wait" or "authority-release-read"))throw new ArgumentException("Unknown bounded metric phase.");
        return new Measurement(elapsed=>phases.AddOrUpdate(phase,elapsed.Ticks,(_,prior)=>prior+elapsed.Ticks));
    }

    public static IDisposable MeasureAuthentication() => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref authenticationOperations);
        Interlocked.Add(ref authenticationTicks, elapsed.Ticks);
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

    internal static IDisposable MeasureAuthorityRead() => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref authorityReads);
        Interlocked.Add(ref authorityReadTicks, elapsed.Ticks);
        AuthorityReadDuration.Record(elapsed.TotalMilliseconds);
    });

    internal static IDisposable MeasureAuthorityCommit() => new Measurement(elapsed =>
    {
        Interlocked.Increment(ref authorityCommits);
        Interlocked.Add(ref authorityCommitTicks, elapsed.Ticks);
        AuthorityCommitDuration.Record(elapsed.TotalMilliseconds);
    });

    internal static void RecordAuthorityReadRetry() => Interlocked.Increment(ref authorityReadRetries);
    internal static void RecordAuthorityUnavailable() => Interlocked.Increment(ref authorityUnavailable);
    internal static void RecordPolicyUnavailable() => Interlocked.Increment(ref policyUnavailable);

    public static void RecordPolicyCompilation() => Interlocked.Increment(ref policyCompilations);
    public static void RecordPolicyReuse() => Interlocked.Increment(ref policyReuses);
    public static void RecordAssetHit() => Interlocked.Increment(ref assetHits);
    public static void RecordAssetMiss() => Interlocked.Increment(ref assetMisses);
    public static object Snapshot() => new
    {
        requests = Interlocked.Read(ref requests),
        httpElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref requestTicks)).TotalMilliseconds,
        sequenceWaitMs = TimeSpan.FromTicks(Interlocked.Read(ref sequenceWaitTicks)).TotalMilliseconds,
        authenticationOperations = Interlocked.Read(ref authenticationOperations),
        authenticationElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref authenticationTicks)).TotalMilliseconds,
        authorityReads = Interlocked.Read(ref authorityReads),
        authorityReadRetries = Interlocked.Read(ref authorityReadRetries),
        authorityReadElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref authorityReadTicks)).TotalMilliseconds,
        authorityCommitAttempts = Interlocked.Read(ref authorityCommits),
        authorityCommitElapsedMs = TimeSpan.FromTicks(Interlocked.Read(ref authorityCommitTicks)).TotalMilliseconds,
        authorityUnavailable = Interlocked.Read(ref authorityUnavailable),
        policyUnavailable = Interlocked.Read(ref policyUnavailable),
        policyCompilations = Interlocked.Read(ref policyCompilations), policyReuses = Interlocked.Read(ref policyReuses),
        assetCacheHits = Interlocked.Read(ref assetHits), assetCacheMisses = Interlocked.Read(ref assetMisses)
        ,phaseElapsedMs=phases.ToDictionary(pair=>pair.Key,pair=>TimeSpan.FromTicks(pair.Value).TotalMilliseconds)
    };
    private sealed class Measurement(Action<TimeSpan> record) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose() => record(Stopwatch.GetElapsedTime(started));
    }
}
