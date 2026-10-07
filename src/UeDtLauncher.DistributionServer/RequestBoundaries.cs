using System.Net;

namespace UeDtLauncher.Distribution;

public static class DistributionClientAddress
{
    public static IPAddress? Resolve(IPAddress? peer, IReadOnlyList<string?> canonical)
    {
        if (peer is null) return null;
        if (canonical.Count != 0 && IPAddress.IsLoopback(peer))
            return canonical.Count == 1 && IPAddress.TryParse(canonical[0], out var value) ? value : null;
        return peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
    }
}

internal sealed class DistributionRequestLimits
{
    private readonly int? rateLimit;
    private readonly SemaphoreSlim? downloads;
    private readonly object gate = new();
    private long window;
    private int count;
    private readonly TimeProvider clock;
    internal DistributionRequestLimits(DistributionSettings settings,TimeProvider? clock=null)
    {
        this.clock=clock??TimeProvider.System;
        if (settings.MaxApiRequestsPerSecond is <= 0 or > 1000000 || settings.MaxConcurrentDownloads is <= 0 or > 4096)
            throw new InvalidDataException("Request limits must be explicit positive bounded values or null (disabled).");
        rateLimit = settings.MaxApiRequestsPerSecond;
        if (settings.MaxConcurrentDownloads is { } limit) downloads = new(limit, limit);
    }
    internal bool AllowRequest()
    {
        if (rateLimit is null) return true;
        lock (gate)
        {
            var now = clock.GetTimestamp()/clock.TimestampFrequency;
            if (window != now) { window = now; count = 0; }
            if (count >= rateLimit) return false;
            count++; return true;
        }
    }
    internal IDisposable? Download()
    {
        if (downloads is null) return new Release(null);
        return downloads.Wait(0) ? new Release(downloads) : null;
    }
    private sealed class Release(SemaphoreSlim? slots) : IDisposable
    {
        private SemaphoreSlim? owned = slots;
        public void Dispose() => Interlocked.Exchange(ref owned, null)?.Release();
    }
}
