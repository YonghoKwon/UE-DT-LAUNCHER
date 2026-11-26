using System.Text.Json;
using System.Text.Json.Serialization;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

internal sealed class CatalogAuthority(AuthorityStore store, string payloadRoot)
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private const int MaxMetadataBytes = 1024 * 1024;
    internal const int HttpSetMaxEntries = 1024;
    internal const long HttpSetMaxEstimatedBytes = 4L * 1024 * 1024;
    internal static readonly AsyncLocal<Action<AuthorityBoundary>?> PublicationBoundary = new();
    private readonly AuthorityParsedCache<ReleaseSidecar> metadataCache = new(SidecarPackageValidator.Parse, entryLimit: 512);
    private readonly object catalogCacheGate = new();
    private ((string Path, string Digest) Key, CatalogDocument Document, int SourceBytes)? cachedCatalog;
    private readonly object httpSetGate = new();
    private HttpCatalogSet? httpSet;

    internal int CachedCatalogSourceBytes
    {
        get { lock (catalogCacheGate) return cachedCatalog?.SourceBytes ?? 0; }
    }

    internal static PromotionMigration InspectExisting(string root)
    {
        var snapshot = AuthorityStore.InspectExisting(root);
        ValidateRoot(snapshot, Path.GetFullPath(root));
        return new(true, false, []);
    }

    internal static void ValidateRoot(AuthoritySnapshot snapshot, string root)
    {
        try
        {
            _ = new CatalogAuthority(snapshot.Store, root).Snapshot(snapshot);
            _ = CatalogSequenceAllocator.ReadHighWater(snapshot);
            _ = IntakeJobs.Read(snapshot);
            CredentialAuthority.Validate(snapshot);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            throw new AuthorityRootException(AuthorityRootKind.Damaged, "The file authority documents are invalid.", error);
        }
    }

    internal PromotionSnapshot Snapshot() => Snapshot(store.ReadSnapshot());

    internal PromotionSnapshot Snapshot(AuthoritySnapshot snapshot)
        => BuildSnapshot(snapshot, projectionOnly: false);

    internal PromotionSnapshot RequestSnapshot(AuthoritySnapshot snapshot)
    {
        const string area = "catalog";
        var key = (snapshot.Head.Files[area], snapshot.Head.Sha256[area]);
        if (Volatile.Read(ref httpSet) is { } current && current.Key == key) return current.View;
        lock (httpSetGate)
        {
            if (httpSet is { } cached && cached.Key == key) return cached.View;
            var view = BuildSnapshot(snapshot, projectionOnly: true);
            if (httpSet is null || httpSet.HeadGeneration <= snapshot.Generation)
            {
                var retained = FitsHttpSet(view) ? new HttpCatalogSet(key, snapshot.Generation, view) : null;
                Volatile.Write(ref httpSet, retained);
            }
            return view;
        }
    }

    private static bool FitsHttpSet(PromotionSnapshot view)
    {
        if (view.Releases.Count > HttpSetMaxEntries || view.Events.Count > HttpSetMaxEntries) return false;
        static long Text(string? value) => 24L + 2L * (value?.Length ?? 0);
        var releaseBytes = view.Releases.Sum(release =>
        {
            var m = release.Metadata;
            return 512L + Text(release.ReleaseId) + Text(release.JobId) + Text(release.Directory) +
                Text(m.ProjectId) + Text(m.DisplayName) + Text(m.Version) + Text(m.Environment) + Text(m.Channel) +
                Text(m.Platform) + Text(m.Notes) + Text(m.HeroPath) + Text(m.ThumbnailPath);
        });
        var eventBytes = view.Events.Sum(entry => 256L + Text(entry.Track) + Text(entry.ReleaseId) +
            Text(entry.PreviousRelease) + Text(entry.Actor) + Text(entry.AtUtc) + Text(entry.Reason) + Text(entry.Kind));
        return 256 + releaseBytes + eventBytes <= HttpSetMaxEstimatedBytes;
    }

    internal void ValidateRequestReleasePath(PublishedRelease release) =>
        _ = ResolveChecked(payloadRoot, "releases/" + release.ReleaseId);

    internal PublishedRelease? FindForRequest(string releaseId, AuthoritySnapshot snapshot)
    {
        const string area = "catalog";
        var key = (snapshot.Head.Files[area], snapshot.Head.Sha256[area]);
        if (Volatile.Read(ref httpSet) is not { } cached || cached.Key != key)
            return Find(releaseId, snapshot: snapshot);
        var match = cached.View.Releases.FirstOrDefault(release => release.ReleaseId == releaseId);
        if (match is not null) ValidateRequestReleasePath(match);
        return match;
    }

    internal void InvalidateRequestSnapshot()
    {
        lock (httpSetGate) Volatile.Write(ref httpSet, null);
    }

    private PromotionSnapshot BuildSnapshot(AuthoritySnapshot snapshot, bool projectionOnly)
    {
        var catalog = ReadCatalog(snapshot);
        // A path component shared by releases needs one reparse check in this snapshot read.
        // Discard the directory checks before the next request.
        var checkedDirectories = new HashSet<string>(PathComparer);
        using var measurement = DistributionPerformance.MeasurePhase("authority-release-read");
        return new PromotionSnapshot(catalog.PromotionReady,
            catalog.Releases.Select(entry => LoadRelease(entry, checkedDirectories, projectionOnly)).ToArray(), catalog.Promotions.ToArray());
    }

    internal PublishedRelease? Find(string value, bool byJob = false, AuthoritySnapshot? snapshot = null)
    {
        var catalog = ReadCatalog(snapshot ?? store.ReadSnapshot());
        var entry = catalog.Releases.Find(row => (byJob ? row.Job : row.Id) == value);
        using var measurement = DistributionPerformance.MeasurePhase("authority-release-read");
        return entry is null ? null : LoadRelease(entry);
    }

    internal PromotionInspection Promote(ReleaseSelection selection, string track, long expectedRevision, string reason)
    {
        PromotionInspection? result = null;
        store.Commit(snapshot =>
        {
            var catalog = ReadCatalog(snapshot);
            var target = catalog.Releases.Find(row => row.Id == selection.ReleaseId && row.Fields.Track == track)
                ?? throw new InvalidOperationException("Only a published release in this exact track can be promoted.");
            _ = LoadRelease(target);
            var history = catalog.Promotions.Where(entry => entry.Track == track).ToList();
            var current = history.LastOrDefault();
            if ((current?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("Promotion revision changed; inspect again before retrying.");
            if (current?.ReleaseId == selection.ReleaseId)
            {
                result = new(true, track, expectedRevision, current.ReleaseId, history);
                return CommitPlan.Audit();
            }

            var now = DateTimeOffset.UtcNow;
            var revision = checked((catalog.Promotions.LastOrDefault()?.Revision ?? 0) + 1);
            var promoted = new PromotionEvent(revision, track, target.Id, current?.ReleaseId,
                RuntimeIdentities.Current().Owner, now.ToString("O"), reason, "manual");
            history.Add(promoted);
            result = new(true, track, revision, target.Id, history);
            var change = Change(catalog with { Promotions = [.. catalog.Promotions, promoted] },
                new(now, "release-promoted", target.Id));
            ReleasePromotions.BeforeCommit.Value?.Invoke();
            return change;
        });
        return result!;
    }

    internal PublishedRelease Publish(string jobId, ReleaseSidecar metadata, byte[] bytes, IntakeWorkClaim claim, Action activate)
    {
        PublishedRelease? result = null;
        var conflict = false;
        CommitPublication(snapshot =>
        {
            var catalog = ReadCatalog(snapshot);
            var existing = catalog.Releases.Find(row => PathComparer.Equals(row.Id, metadata.ReleaseId));
            if (existing is not null)
            {
                if (existing.Job != jobId || existing.Id != metadata.ReleaseId)
                {
                    conflict = true;
                    var conflictedJobs = IntakeJobs.Read(snapshot);
                    claim.RejectConflictingPublication(conflictedJobs);
                    return conflictedJobs.Plan(new AuthorityEvent(DateTimeOffset.UtcNow, "failed", jobId));
                }
                throw new InvalidDataException("Publication catalog and job state differ.");
            }
            if (catalog.Releases.Exists(row => row.Job == jobId))
                throw new InvalidDataException("The job already owns a different release.");

            if (bytes.Length > MaxMetadataBytes)
                throw new InvalidDataException("Published metadata exceeds 1 MiB.");
            var relative = "releases/" + metadata.ReleaseId;
            var entry = new CatalogRelease(metadata.ReleaseId, jobId, relative, AuthorityDocuments.Hash(bytes), CatalogFields.From(metadata));
            var completedAt = DateTimeOffset.UtcNow;
            var change = Change(catalog with { Releases = [.. catalog.Releases, entry] },
                new(completedAt, "published", jobId));
            var jobs = IntakeJobs.Read(snapshot);
            claim.CompletePublication(jobs, completedAt);
            var areas = new Dictionary<AuthorityArea, JsonElement>(change.Areas)
            {
                [AuthorityArea.Jobs] = jobs.Plan().Areas[AuthorityArea.Jobs]
            };
            var path = MetadataPath(entry.Id);
            var directory = ResolveChecked(payloadRoot, relative);
            activate();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            if (OperatingSystem.IsLinux())
            {
                AuthorityDisk.SyncFileSystem(directory);
                AuthorityDisk.FlushDirectory(Path.Combine(payloadRoot, "releases"));
                AuthorityDisk.SyncFileSystem(store.ReleaseMetadataPath);
            }
            result = new(entry.Id, entry.Job, directory, metadata);
            return change with { Areas = areas };
        });
        if (conflict) throw new PublicationIdConflictException();
        return result!;
    }

    private void CommitPublication(Func<AuthoritySnapshot, CommitPlan> plan)
    {
        var boundary = PublicationBoundary.Value;
        if (boundary is null)
        {
            store.Commit(plan);
            return;
        }
        var previous = AuthorityDisk.Boundary.Value;
        AuthorityDisk.Boundary.Value = boundary;
        try { store.Commit(plan); }
        finally { AuthorityDisk.Boundary.Value = previous; }
    }

    private CatalogDocument ReadCatalog(AuthoritySnapshot snapshot)
    {
        var name = AuthorityDocuments.Name(AuthorityArea.Catalog);
        var key = (snapshot.Head.Files[name], snapshot.Head.Sha256[name]);
        lock (catalogCacheGate)
        {
            if (cachedCatalog is { } cached && cached.Key == key) return cached.Document;
            var catalog = snapshot.Get(AuthorityArea.Catalog).Deserialize<CatalogDocument>(AuthorityDocuments.Json)
                ?? throw new InvalidDataException("Catalog authority is missing.");
            Validate(catalog);
            cachedCatalog = snapshot.CatalogSourceBytes <= AuthorityAreaCache.ByteLimit
                ? (key, catalog, snapshot.CatalogSourceBytes) : null;
            return catalog;
        }
    }

    private static void Validate(CatalogDocument catalog)
    {
        if (catalog.SchemaVersion != AuthorityDocuments.Schema || !catalog.PromotionReady ||
            catalog.Releases is null || catalog.Promotions is null)
            throw new InvalidDataException("Catalog authority is invalid or unready.");
        var tracks = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(PathComparer);
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Releases)
        {
            if (entry?.Fields is not { } fields || entry.Job is null || entry.MetadataSha256 is not { Length: 64 } ||
                entry.MetadataSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                fields.ProjectId is null || fields.Environment is null || fields.Channel is null ||
                fields.Platform is null || fields.Version is null)
                throw new InvalidDataException("Catalog release entry is invalid.");
            var selection = new ReleaseSelection(fields.ProjectId, fields.Environment, fields.Channel, fields.Platform, fields.Version);
            var track = ReleasePromotions.Track(selection);
            ReleaseSidecar.Segment(entry.Job);
            if (entry.Id != selection.ReleaseId || entry.Directory != "releases/" + entry.Id ||
                !paths.Add(entry.Id) || !tracks.TryAdd(entry.Id, track) || !jobs.Add(entry.Job))
                throw new InvalidDataException("Catalog release identity or path is invalid.");
        }

        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        long revision = 0;
        foreach (var entry in catalog.Promotions)
        {
            if (entry is null || entry.Revision != ++revision || entry.Track is null || entry.ReleaseId is null ||
                !tracks.TryGetValue(entry.ReleaseId, out var track) || track != entry.Track ||
                current.GetValueOrDefault(track) != entry.PreviousRelease || entry.ReleaseId == entry.PreviousRelease ||
                entry.Kind != "manual" || string.IsNullOrWhiteSpace(entry.Actor) || string.IsNullOrWhiteSpace(entry.Reason) ||
                entry.Reason.Length > 2048 || entry.Reason.Contains('\0') || !DateTimeOffset.TryParseExact(entry.AtUtc, "O", null,
                    System.Globalization.DateTimeStyles.None, out _))
                throw new InvalidDataException("Catalog promotion history is invalid.");
            current[track] = entry.ReleaseId;
        }
    }

    private PublishedRelease LoadRelease(CatalogRelease entry, HashSet<string>? checkedDirectories = null, bool projectionOnly = false)
    {
        var path = ResolveChecked(store.ReleaseMetadataPath, entry.Id + ".json", checkedDirectories);
        using var input = AuthorityDisk.OpenReader(path);
        if (input.Length is <= 0 or > MaxMetadataBytes)
            throw new InvalidDataException("Published metadata has an invalid length.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || AuthorityDocuments.Hash(bytes) != entry.MetadataSha256)
            throw new InvalidDataException("Published metadata checksum is invalid.");
        ReleaseSidecar metadata;
        try { metadata = metadataCache.Parse(path, entry.MetadataSha256, bytes); }
        catch (Exception error) when (error is ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException("Published metadata has invalid fields.", error);
        }
        if (metadata.ReleaseId != entry.Id || CatalogFields.From(metadata) != entry.Fields)
            throw new InvalidDataException("Published metadata identity or catalog fields differ.");
        var directory = ResolveChecked(payloadRoot, entry.Directory, checkedDirectories);
        if (projectionOnly)
            return new(entry.Id, entry.Job, directory, entry.Fields.ToRequestMetadata());
        // Public callers get their own mutable metadata object, never the cached instance.
        return new(entry.Id, entry.Job, directory, new ReleaseSidecar
        {
            SchemaVersion = metadata.SchemaVersion, PackageFile = metadata.PackageFile, PackageSize = metadata.PackageSize,
            PackageSha256 = metadata.PackageSha256, ProjectId = metadata.ProjectId, DisplayName = metadata.DisplayName,
            Version = metadata.Version, Environment = metadata.Environment, Channel = metadata.Channel, Platform = metadata.Platform,
            PayloadRoot = metadata.PayloadRoot, EntryPoint = metadata.EntryPoint, ExecutablePaths = [.. metadata.ExecutablePaths],
            Notes = metadata.Notes, HeroPath = metadata.HeroPath, ThumbnailPath = metadata.ThumbnailPath
        });
    }

    private string MetadataPath(string releaseId) => ResolveChecked(store.ReleaseMetadataPath, releaseId + ".json");

    private static string ResolveChecked(string root, string relative, HashSet<string>? checkedDirectories = null)
    {
        try
        {
            var path = SafePath.ResolveInside(root, relative);
            var component = path;
            while (component.Length > root.Length)
            {
                var attributes = GetAttributes(component);
                if (attributes is { } found && (found & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Published release path contains a link.");
                if (checkedDirectories is not null && component != path && attributes is { } item &&
                    (item & FileAttributes.Directory) != 0)
                    checkedDirectories.Add(component);
                component = Path.GetDirectoryName(component)!;
                if (checkedDirectories?.Contains(component) == true)
                    break;
            }
            return path;
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Published release path is unsafe.", error); }
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static CommitPlan Change(CatalogDocument catalog, AuthorityEvent audit)
    {
        Validate(catalog);
        return new(new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Catalog] = JsonSerializer.SerializeToElement(catalog, AuthorityDocuments.Json)
        }, [audit]);
    }

    private sealed record CatalogDocument([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] List<CatalogRelease> Releases, [property: JsonRequired] List<PromotionEvent> Promotions,
        [property: JsonRequired] bool PromotionReady);

    private sealed record CatalogRelease(string Id, string Job, string Directory, string MetadataSha256, CatalogFields Fields);
    private sealed record HttpCatalogSet((string Path, string Digest) Key, long HeadGeneration, PromotionSnapshot View);

    private sealed record CatalogFields(string ProjectId, string? DisplayName, string Environment, string Channel,
        string Version, string Platform, string? Notes, string? HeroPath, string? ThumbnailPath)
    {
        [JsonIgnore]
        internal string Track => string.Join('/', ProjectId, Environment, Channel, Platform);

        internal static CatalogFields From(ReleaseSidecar metadata) => new(metadata.ProjectId, metadata.DisplayName,
            metadata.Environment, metadata.Channel, metadata.Version, metadata.Platform, metadata.Notes, metadata.HeroPath, metadata.ThumbnailPath);

        internal ReleaseSidecar ToRequestMetadata() => new()
        {
            ProjectId = ProjectId, DisplayName = DisplayName!, Environment = Environment, Channel = Channel,
            Version = Version, Platform = Platform, Notes = Notes, HeroPath = HeroPath, ThumbnailPath = ThumbnailPath
        };
    }
}

internal sealed class PublicationIdConflictException : IOException;
