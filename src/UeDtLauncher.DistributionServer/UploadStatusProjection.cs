using System.Text.Json;

namespace UeDtLauncher.Distribution;

internal sealed class UploadStatusProjection(IntakeStore store, Action<Exception> logFailure)
{
    public async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            using var lease = DistributionMaintenanceLease.Acquire(store.Root, false);
            var directory = SafePath.ResolveInsideChecked(store.Root, "upload-status");
            Directory.CreateDirectory(directory);
            // Serve and watch can share a root. Hold this across reading and writing so a
            // slower projection cannot overwrite a newer observation from another process.
            var lockPath = SafePath.ResolveInsideChecked(directory, ".projection.lock");
            using var writer = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var jobs = store.List().ToLookup(job => job.Source, comparer);
            var published = new Lazy<ProjectionPublications>(() => new(new ReleasePromotions(store).Snapshot()));
            var incoming = SafePath.ResolveInsideChecked(store.Root, "incoming");
            foreach (var source in Directory.EnumerateDirectories(incoming))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                    var uploadId = Path.GetFileName(source);
                    try { ReleaseSidecar.Segment(uploadId); }
                    catch (InvalidDataException) { continue; }
                    var job = jobs[source].SingleOrDefault();
                    var transferring = false;
                    var hasSidecar = false;
                    var hasZip = false;
                    foreach (var file in Directory.EnumerateFiles(source))
                    {
                        var name = Path.GetFileName(file);
                        transferring = name.EndsWith(".uploading", OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                        if (transferring) break;
                        hasSidecar |= comparer.Equals(name, "release.json");
                        hasZip |= name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    }
                    (UploadStatusRelease Release, bool Recommended)? release =
                        job?.State == "published" && published.Value.ByJob.TryGetValue(job.Id, out var publication)
                            ? publication : null;
                    var observation = Observe(uploadId, job?.State, transferring, hasSidecar && hasZip, release, DateTimeOffset.UtcNow);
                    if (observation is not null)
                    {
                        var path = SafePath.ResolveInsideChecked(directory, uploadId + ".json");
                        if (await HasChangedAsync(path, observation, token))
                            await WriteAsync(path, observation, token);
                    }
                }
                catch (Exception error) when (IsProjectionFailure(error, token)) { logFailure(error); }
            }
        }
        catch (Exception error) when (IsProjectionFailure(error, token)) { logFailure(error); }
    }

    private static bool IsProjectionFailure(Exception error, CancellationToken token) =>
        error is not OperationCanceledException || !token.IsCancellationRequested;

    internal static UploadStatus? Observe(string uploadId, string? jobState, bool transferring, bool complete,
        (UploadStatusRelease Release, bool Recommended)? release, DateTimeOffset time)
    {
        var state = transferring ? "transferring" : jobState switch
        {
            null => complete ? "waiting" : null,
            "validating" or "processing" => "processing",
            "waiting" or "failed" or "rejected" or "pending" or "publishing" => jobState,
            "published" => release?.Recommended == true ? "recommended" : "published",
            _ => null
        };
        return state is null ? null : new(uploadId, state, time, release?.Release);
    }

    private static async Task<bool> HasChangedAsync(string path, UploadStatus observation, CancellationToken token)
    {
        if (!File.Exists(path)) return true;
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (reader.Length > UploadStatus.MaximumBytes) return true;
        var data = new byte[reader.Length];
        await reader.ReadExactlyAsync(data, token);
        try
        {
            var previous = UploadStatus.Parse(data, observation.UploadId, out _);
            return previous != observation with { UpdatedAtUtc = previous.UpdatedAtUtc };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task WriteAsync(string path, UploadStatus observation, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await JsonFiles.WriteAsync(path, observation, token);
                return;
            }
            // On Windows an open destination can transiently prevent replacement.
            // The old complete document remains visible between these attempts.
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 3 &&
                (error is IOException or UnauthorizedAccessException) && (error.HResult & 0xFFFF) is 5 or 32 or 33)
            {
                await Task.Delay(10, token);
            }
        }
    }

    private sealed class ProjectionPublications
    {
        public Dictionary<string, (UploadStatusRelease Release, bool Recommended)> ByJob { get; } = new(StringComparer.Ordinal);

        public ProjectionPublications(PromotionSnapshot snapshot)
        {
            var latest = snapshot.Events.GroupBy(entry => entry.Track, StringComparer.Ordinal)
                .ToDictionary(track => track.Key, track => track.MaxBy(entry => entry.Revision)!.ReleaseId, StringComparer.Ordinal);
            foreach (var published in snapshot.Releases)
            {
                var metadata = published.Metadata;
                var release = new UploadStatusRelease(metadata.ProjectId, metadata.Version, metadata.Platform, metadata.Environment, metadata.Channel);
                var track = ReleasePromotions.Track(release.Selection);
                ByJob.Add(published.JobId, (release, snapshot.Ready && latest.GetValueOrDefault(track) == published.ReleaseId));
            }
        }
    }
}
