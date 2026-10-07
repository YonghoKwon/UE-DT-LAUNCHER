using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record PublishedAssetMetadata(string Path, string Sha256, long Size);
public sealed record PublishedAssets(PublishedAssetMetadata? Hero, PublishedAssetMetadata? Thumbnail);

/// <summary>LRU of immutable publication projections only. No client authorization or signed catalog envelopes.</summary>
public sealed class PublishedAssetCache
{
    public const int DefaultMaxEntries = 256;
    public const long DefaultMaxBytes = 32L * 1024 * 1024;
    private readonly int maxEntries;
    private readonly long maxBytes;
    private readonly object gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = new();
    private readonly LinkedList<Entry> lru = new();
    private long retainedBytes;
    private sealed record Key(string ReleaseId, string JobId, string Directory, string? Hero, string? Thumbnail);
    private sealed record Entry(Key Key, PublishedAssets Assets, long Bytes);
    public int Count { get { lock (gate) return entries.Count; } }
    public long RetainedBytes { get { lock (gate) return retainedBytes; } }

    public PublishedAssetCache(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        if (maxEntries <= 0 || maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        this.maxEntries = maxEntries;
        this.maxBytes = maxBytes;
    }
    public PublishedAssets Get(PublishedRelease release)
    {
        if (release.Metadata.HeroPath is null && release.Metadata.ThumbnailPath is null) return new(null, null);
        var key = new Key(release.ReleaseId, release.JobId, release.Directory, release.Metadata.HeroPath, release.Metadata.ThumbnailPath);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                lru.Remove(cached); lru.AddFirst(cached);
                DistributionPerformance.RecordAssetHit();
                return cached.Value.Assets;
            }
        }
        DistributionPerformance.RecordAssetMiss();
        // Read outside the LRU lock. Duplicate concurrent misses are harmless; no mutable manifest escapes.
        using var stream = File.OpenRead(Path.Combine(release.Directory, "manifest.json"));
        var manifestBytes = stream.Length;
        var manifest = JsonSerializer.Deserialize<LauncherManifest>(stream, JsonFiles.Options)
            ?? throw new InvalidDataException("Invalid published manifest.");
        PublishedAssetMetadata? Find(string? path)
        {
            if (path is null) return null;
            var file = manifest.Files.SingleOrDefault(file => file.Path == path.Replace('\\', '/'));
            return file is null ? null : new(file.Path, file.Sha256, file.Size);
        }
        var assets = new PublishedAssets(Find(key.Hero), Find(key.Thumbnail));
        // Charge at least the full source manifest plus retained strings/objects, conservatively bounding the cache.
        static long TextBytes(string? text) => 2L * (text?.Length ?? 0);
        static long AssetBytes(PublishedAssetMetadata? asset) => asset is null ? 0 : 64 + TextBytes(asset.Path) + TextBytes(asset.Sha256);
        var cost = Math.Max(manifestBytes, 256 + TextBytes(key.ReleaseId) + TextBytes(key.JobId) + TextBytes(key.Directory) +
            TextBytes(key.Hero) + TextBytes(key.Thumbnail) + AssetBytes(assets.Hero) + AssetBytes(assets.Thumbnail));
        if (cost > maxBytes) return assets;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var concurrent)) return concurrent.Value.Assets;
            while (entries.Count >= maxEntries || retainedBytes + cost > maxBytes)
            {
                var last = lru.Last!;
                retainedBytes -= last.Value.Bytes;
                entries.Remove(last.Value.Key); lru.RemoveLast();
            }
            entries.Add(key, lru.AddFirst(new Entry(key, assets, cost)));
            retainedBytes += cost;
        }
        return assets;
    }
}
