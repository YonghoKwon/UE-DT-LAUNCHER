using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

public sealed record IntakeJob(string Id, string Source, string State, string? Snapshot, string? Message,
    string? Phase = null, long ProcessedBytes = 0, long TotalBytes = 0,
    long EstimatedAdditionalDiskBytes = 0, string? ProgressUpdatedAt = null);

public sealed class DistributionSettings
{
    public string Root { get; set; } = "/srv/ue-dt-distribution";
    public string PublicUrl { get; set; } = "https://updates.example.com";
    public string SigningKeyPath { get; set; } = "/etc/ue-dt-distribution/signing.pem";
    public string SigningKeyId { get; set; } = "release-1";
    public string ListenUrl { get; set; } = "http://127.0.0.1:18500";
    public string PolicyPath { get; set; } = "/etc/ue-dt-distribution/access-policy.json";
    public ZipIntakeLimits Limits { get; set; } = new();
    public int IntakeWorkers { get; set; } = 1;
    public string AuthenticationMode { get; set; } = "bearer";
    public int? MaxApiRequestsPerSecond { get; set; }
    public int? MaxConcurrentDownloads { get; set; }
}

public sealed partial class IntakeStore
{
    private readonly Func<string, long>? availableBytes;
    private string? scanCursor;
    internal AuthorityStore Authority { get; }
    internal CredentialAuthority Credentials { get; }
    internal CatalogAuthority Catalog { get; }
    public DistributionSettings Settings { get; }
    public string Root { get; }
    internal static IntakeStore OpenForEnrollmentValidation(DistributionSettings settings)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Root));
        if (File.Exists(SafePath.ResolveInsideChecked(root, "restore-staged.json")))
            throw new InvalidDataException("Staged restored root rejects enrollment.");
        return new(settings, AuthorityStore.InspectExisting(root, validateHistory: false).Store);
    }

    private IntakeStore(DistributionSettings settings, AuthorityStore authority)
    {
        if (settings.IntakeWorkers is < 1 or > 2) throw new InvalidDataException("IntakeWorkers must be 1 or 2.");
        Settings = settings;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Root));
        Authority = authority;
        Credentials = new(Authority);
        Catalog = new(Authority, Root);
    }

    public IntakeStore(DistributionSettings settings, Func<string, long>? availableBytes = null)
    {
        this.availableBytes = availableBytes;
        if (settings.IntakeWorkers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(settings.IntakeWorkers), "IntakeWorkers must be 1 or 2.");
        Settings = settings;
        Root = settings.Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Root));
        if (File.Exists(Path.Combine(Root, "restore-staged.json")))
            throw new InvalidDataException("Staged restored root rejects live operations.");
        Authority = AuthorityStore.Open(Root, validate: snapshot => CatalogAuthority.ValidateRoot(snapshot, Root));
        Credentials = new(Authority);
        Catalog = new(Authority, Root);
        using (Authority.Lock())
            foreach (var directory in new[] { "incoming", "processing", "releases", "archive", ".job-locks" })
                Directory.CreateDirectory(Path.Combine(Root, directory));
    }

    public async Task<IntakeJob> IngestAsync(string directory, CancellationToken token = default,
        IProgress<PackageWorkProgress>? observer = null)
    {
        using var operationLease = DistributionMaintenanceLease.Acquire(Root, false);
        var source = Path.GetFullPath(directory);
        var incoming = Path.Combine(Root, "incoming");
        if (!string.Equals(Path.GetDirectoryName(source), incoming, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Upload must be an immediate child of incoming.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
        using var jobLock = LockJob(id);
        var previous = Find(id);
        if (previous is { State: "pending" or "published" or "rejected" or "failed" or "publishing" }) return previous;
        var job = new IntakeJob(id, source, "waiting", null, null);
        SaveJob(job, resetClaim: true);
        IntakeWorkClaim? work = null;
        try
        {
            RejectLink(source);
            if (Directory.EnumerateFiles(source, "*.uploading").Any()) return job;
            var metadataPath = Path.Combine(source, "release.json");
            if (!File.Exists(metadataPath)) return job;
            RejectLink(metadataPath);
            var metadataBytes = await SidecarPackageValidator.ReadDocumentAsync(metadataPath, token);
            var metadata = SidecarPackageValidator.Parse(metadataBytes);
            var zip = Path.Combine(source, metadata.PackageFile);
            if (!File.Exists(zip)) return job;
            RejectLink(zip);
            var snapshot = Path.Combine(Root, "processing", id + "-" + Guid.NewGuid().ToString("N"));
            work = new IntakeWorkClaim(this, job, "validating", snapshot, snapshot, "waiting", "validating");
            job = Get(id);
            var progress = Progress(id, observer);
            var estimate = IntakeDiskSpace.Estimate(metadata.PackageSize);
            progress.Report(new("snapshot-copy", 0, metadata.PackageSize, estimate));
            IntakeDiskSpace.Require(Root, estimate, availableBytes);
            Directory.CreateDirectory(snapshot);
            // Copies are private and immutable to upload users. Approval uses only this snapshot.
            await File.WriteAllBytesAsync(Path.Combine(snapshot, "release.json"), metadataBytes, token);
            var capturedZip = Path.Combine(snapshot, metadata.PackageFile);
            await using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(capturedZip, FileMode.CreateNew, FileAccess.Write))
            {
                if (input.Length != metadata.PackageSize || input.Length > Settings.Limits.MaxZipBytes)
                    throw new InvalidDataException("ZIP size mismatch or limit exceeded.");
                var buffer = new byte[128 * 1024]; long copied = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += read;
                    if (copied > metadata.PackageSize) throw new InvalidDataException("ZIP changed while capturing upload.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress.Report(new("snapshot-copy", copied, metadata.PackageSize, IntakeDiskSpace.Estimate(metadata.PackageSize - copied)));
                }
                if (copied != metadata.PackageSize) throw new InvalidDataException("ZIP truncated while capturing upload.");
            }
            await SidecarPackageValidator.ValidateAndExtractAsync(capturedZip, metadata, Path.Combine(snapshot, "payload"), Settings.Limits, token, progress, availableBytes);
            progress.Report(new("awaiting-approval", metadata.PackageSize, metadata.PackageSize, 0));
            job = Get(id) with { State = "pending", Snapshot = snapshot };
        }
        catch (OperationCanceledException) { job = Find(id) ?? job; throw; }
        catch (Exception ex) { job = (Find(id) ?? job) with { State = "failed", Message = ex.Message }; }
        finally
        {
            // Make a successful snapshot durable before removing its active-work protection.
            try { Save(job); }
            finally { work?.Dispose(); }
        }
        return job;
    }

    public void Reject(string id)
    {
        using var jobLock = LockJob(id);
        if (!ChangeState(id, "pending", "rejected")) throw new InvalidOperationException("Only pending jobs can be rejected.");
    }
    public void Retry(string id)
    {
        using var jobLock = LockJob(id);
        if (!ChangeState(id, "failed", "waiting", reset: true)) throw new InvalidOperationException("Only failed jobs can be retried.");
    }
    public async Task ScanAsync(CancellationToken token)
    {
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Settings.IntakeWorkers * 2)
        { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var workers = Enumerable.Range(0, Settings.IntakeWorkers).Select(_ => ConsumeAsync()).ToArray();
        try
        {
            var finished = IntakeJobs.Read(Authority.ReadSnapshot()).Jobs
                .Where(job => job.State is "pending" or "published" or "rejected" or "failed" or "publishing")
                .Select(job => IntakeJobs.Resolve(Root, job.Source)).ToHashSet(IntakeJobs.PathComparer);
            var previousCursor = scanCursor;
            var afterCursor = previousCursor is null;
            var full = false;
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "incoming")))
            {
                token.ThrowIfCancellationRequested();
                if (!afterCursor) { if (directory == previousCursor) afterCursor = true; continue; }
                if (!Enqueue(directory)) { full = true; break; }
            }
            // Rotate admission so persistent partial uploads at the front cannot starve ready uploads.
            if (!full && previousCursor is not null)
                foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "incoming")))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Enqueue(directory)) break;
                    if (directory == previousCursor) break;
                }
            bool Enqueue(string directory)
            {
                if (finished.Contains(directory)) return true;
                if (!queue.Writer.TryWrite(directory)) return false;
                scanCursor = directory; return true;
                // Full queues defer to the next scan; never spawn per-upload tasks.
            }
        }
        finally { queue.Writer.TryComplete(); await Task.WhenAll(workers); }

        async Task ConsumeAsync()
        {
            await foreach (var directory in queue.Reader.ReadAllAsync(token))
                try { await IngestAsync(directory, token); }
                catch (IOException) { /* Job claimed by another process; retry on the next scan. */ }
        }
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Upload links are not accepted.");
    }
}
