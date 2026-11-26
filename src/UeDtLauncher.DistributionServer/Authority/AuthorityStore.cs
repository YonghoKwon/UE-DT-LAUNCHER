using System.Diagnostics;
using System.Text.Json;

namespace UeDtLauncher.Distribution.Authority;

internal sealed class AuthorityStore
{
    internal const int ReadRetries = 3;
    internal const int PriorGenerationsToKeep = 8;
    internal static readonly AsyncLocal<Action<long>?> AfterReadHead = new();

    private readonly AuthorityAreaCache cache = new();
    private readonly AuthorityAuditProjection projection;
    private readonly AuthorityLayout layout;
    private readonly string[] generationDirectories;
    private readonly string[] dataDirectories;
    private string Root { get; }
    internal string DirectoryPath { get; }
    internal string ReleaseMetadataPath => DataPath(layout.ReleaseMetadataDirectory);
    internal string ProgressPath => DataPath(layout.ProgressDirectory);
    internal Exception? OpenMaintenanceError { get; private set; }

    private AuthorityStore(string root, AuthorityLayout layout)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.layout = layout with
        {
            ExistingDistributionMarkers = layout.ExistingDistributionMarkers?.ToArray()
        };
        this.layout.Validate();
        DirectoryPath = Path.Combine(Root, layout.AuthorityDirectory);
        generationDirectories = AuthorityDocuments.Areas.Select(area => layout.DirectoryFor(AuthorityDocuments.Name(area)))
            .Append(layout.EventsDirectory).ToArray();
        dataDirectories = [.. generationDirectories, layout.ReleaseMetadataDirectory, layout.ProgressDirectory];
        projection = new(this);
    }

    internal static AuthorityStore Open(string root, AuthorityLayout? layout = null)
    {
        var store = new AuthorityStore(root, layout ?? AuthorityLayout.Default);
        // Validation precedes creation of even the maintenance/writer locks.
        var existing = store.CheckRoot(validateHistory: true);
        if (!existing) CreateDurableDirectory(store.Root);
        using var writer = store.AcquireWriter(exclusiveMaintenance: false);
        // Initialization and a second probe are serialized across first-time openers.
        if (!store.CheckRoot(initializing: !existing)) store.Initialize();
        var snapshot = store.ReadSnapshot();
        store.CleanOrphans(snapshot);
        store.OpenMaintenanceError = store.RunMaintenance(snapshot);
        return store;
    }

    internal AuthoritySnapshot ReadSnapshot()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return LoadSnapshot(); }
            catch (Exception error) when (IsReadError(error))
            {
                if (attempt == ReadRetries)
                    throw new AuthorityStoreException("Could not read a valid authority snapshot after three retries.", error);
                Thread.Yield();
            }
        }
    }

    internal WriterLease Lock(bool exclusiveMaintenance = false)
    {
        if (!CheckRoot()) throw new AuthorityRootException(AuthorityRootKind.Damaged, "Authority HEAD is missing.");
        return AcquireWriter(exclusiveMaintenance);
    }

    internal AuthorityCommitResult Commit(Func<AuthoritySnapshot, CommitPlan> makePlan)
    {
        using var writer = Lock();
        return CommitUnderHeldLock(writer, makePlan);
    }

    internal AuthorityCommitResult CommitUnderHeldLock(WriterLease writer, Func<AuthoritySnapshot, CommitPlan> makePlan) =>
        writer.Use<AuthorityCommitResult>(this, () =>
        {
            var snapshot = ReadSnapshot();
            CleanOrphans(snapshot);
            var plan = makePlan(snapshot);
            if (plan.Areas.Count == 0 && plan.Events.Count == 0)
                return new(snapshot.Generation, false, RunMaintenance(snapshot));

            var generation = checked(snapshot.Generation + 1);
            var batch = AuthorityDocuments.MakeBatch(generation, plan.Events);
            var changes = plan.Areas.ToDictionary(pair => pair.Key, pair => AuthorityDocuments.ValidateArea(pair.Value).Clone());
            var updates = changes.ToDictionary(pair => pair.Key, pair => AuthorityDocuments.Encode(pair.Value));
            foreach (var area in updates.Keys) AuthorityDocuments.Name(area);
            var nextAreas = AuthorityDocuments.Areas.ToDictionary(area => area,
                area => changes.TryGetValue(area, out var updated) ? updated : snapshot.Get(area));
            var next = Publish(DirectoryPath, snapshot.Head, generation, updates, batch);
            var nextSnapshot = new AuthoritySnapshot(this, next, nextAreas);
            return new(generation, true, RunMaintenance(nextSnapshot));
        });

    internal Exception? Recover(WriterLease writer) => writer.Use(this, () =>
    {
        var snapshot = ReadSnapshot();
        CleanOrphans(snapshot);
        return RunMaintenance(snapshot);
    });

    internal IEnumerable<StoredAuthorityEvent> ReadAudit(AuthoritySnapshot snapshot)
    {
        ValidateSnapshotOwner(snapshot);
        for (long generation = 0; ; generation++)
        {
            foreach (var entry in ReadBatch(snapshot, generation).Events) yield return entry;
            if (generation == snapshot.Generation) yield break;
        }
    }

    internal bool HasAudit(AuthoritySnapshot snapshot, string action, string job) =>
        ReadAudit(snapshot).Any(entry => entry.Action == action && entry.Job == job);

    internal AuthorityEventBatch ReadBatch(AuthoritySnapshot snapshot, long generation)
    {
        ValidateSnapshotOwner(snapshot);
        if (generation < 0 || generation > snapshot.Generation) throw new ArgumentOutOfRangeException(nameof(generation));
        var relative = AuthorityDocuments.RelativeFile(layout.EventsDirectory, generation);
        try
        {
            var bytes = ReadFile(DataPath(relative));
            if (generation == snapshot.Generation)
                VerifyHash(bytes, snapshot.Head.Sha256[AuthorityDocuments.Events]);
            return DecodeBatch(bytes, generation);
        }
        catch (Exception error) when (IsReadError(error))
        {
            throw new AuthorityStoreException($"Could not read authority event generation {generation}.", error);
        }
    }

    internal string DataPath(string relative) => SafePath.ResolveInsideChecked(DirectoryPath, relative);

    private void ValidateSnapshotOwner(AuthoritySnapshot snapshot)
    {
        if (snapshot.Store != this) throw new ArgumentException("Snapshot belongs to another authority store.", nameof(snapshot));
    }

    private AuthoritySnapshot LoadSnapshot()
    {
        var bytes = ReadFile(DataPath("HEAD"));
        var head = AuthorityDocuments.Decode<AuthorityHead>(bytes);
        AuthorityDocuments.Validate(head, layout);
        if (!bytes.AsSpan().SequenceEqual(AuthorityDocuments.Encode(head)))
            throw new InvalidDataException("Authority HEAD is not in its canonical format.");
        AfterReadHead.Value?.Invoke(head.Generation);
        var areas = new Dictionary<AuthorityArea, JsonElement>();
        foreach (var area in AuthorityDocuments.Areas)
        {
            var name = AuthorityDocuments.Name(area);
            var relative = head.Files[name];
            var digest = head.Sha256[name];
            var content = ReadFile(DataPath(relative));
            VerifyHash(content, digest);
            areas.Add(area, cache.Parse(relative, digest, content));
        }
        var events = ReadFile(DataPath(head.Files[AuthorityDocuments.Events]));
        VerifyHash(events, head.Sha256[AuthorityDocuments.Events]);
        DecodeBatch(events, head.Generation);
        return new(this, AuthorityDocuments.Freeze(head), areas);
    }

    private static AuthorityEventBatch DecodeBatch(byte[] bytes, long generation)
    {
        var batch = AuthorityDocuments.ValidateBatch(AuthorityDocuments.Decode<AuthorityEventBatch>(bytes), generation);
        if (!bytes.AsSpan().SequenceEqual(AuthorityDocuments.Encode(batch)))
            throw new InvalidDataException("Authority event generation is not in its canonical format.");
        return batch;
    }

    private static void VerifyHash(byte[] bytes, string digest)
    {
        if (AuthorityDocuments.Hash(bytes) != digest)
            throw new InvalidDataException("Authority generation checksum is invalid.");
    }

    private static byte[] ReadFile(string path)
    {
        using var stream = AuthorityDisk.OpenReader(path);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Authority file length changed while reading.");
        return bytes;
    }

    private bool CheckRoot(bool validateHistory = false, bool initializing = false)
    {
        try
        {
            var existingDirectory = Root;
            while (!Directory.Exists(existingDirectory))
            {
                if (Attributes(existingDirectory) is not null) throw new IOException("Authority root must be a directory.");
                existingDirectory = Path.GetDirectoryName(existingDirectory)
                    ?? throw new IOException("No existing parent for the authority root.");
            }
            RejectLink(existingDirectory);
            AuthorityDisk.EnsureSupportedFileSystem(existingDirectory);
            if (existingDirectory != Root) return false;

            var names = Directory.EnumerateFileSystemEntries(Root).Select(path => Path.GetFileName(path)!).ToArray();
            foreach (var file in names)
                if (file.Equals("distribution.db", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith("distribution.pre-v6-", StringComparison.OrdinalIgnoreCase) &&
                    file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    throw new AuthorityRootException(AuthorityRootKind.LegacyDatabase, "Legacy distribution database detected.");

            if (Attributes(DirectoryPath) is null)
            {
                foreach (var marker in layout.ExistingDistributionMarkers ?? AuthorityLayout.Default.ExistingDistributionMarkers!)
                    if (Attributes(SafePath.ResolveInsideChecked(Root, marker)) is not null)
                        throw new AuthorityRootException(AuthorityRootKind.ExistingDistribution, "Distribution files exist without an authority HEAD.");
                // The private second check sees the two leases just acquired after the empty-root probe.
                if (names.Any(name => !initializing || name is not (".maintenance.lock" or ".writer.lock")))
                    throw new AuthorityRootException(AuthorityRootKind.UnrecognizedLayout, "Existing files use an unrecognized distribution layout.");
                return false;
            }
            RejectLink(DirectoryPath);
            AuthorityDisk.EnsureSupportedFileSystem(DirectoryPath);
            foreach (var name in dataDirectories)
            {
                var directory = DataPath(name);
                if (!Directory.Exists(directory)) throw new IOException("Authority directory is missing.");
                AuthorityDisk.EnsureSupportedFileSystem(directory);
            }
            var snapshot = ReadSnapshot();
            if (validateHistory)
                for (long generation = 0; generation < snapshot.Generation; generation++)
                    ReadBatch(snapshot, generation);
            return true;
        }
        catch (AuthorityRootException) { throw; }
        catch (Exception error) when (error is AuthorityFileSystemException or PlatformNotSupportedException)
        {
            throw new AuthorityRootException(AuthorityRootKind.UnsupportedFileSystem, error.Message, error);
        }
        catch (Exception error) when (IsReadError(error))
        {
            throw new AuthorityRootException(AuthorityRootKind.Damaged, "Distribution authority root is damaged or inaccessible.", error);
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void RejectLink(string path)
    {
        if ((Attributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            throw new IOException("Authority storage refuses linked directories.");
    }

    private static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or OverflowException;

    private static void CreateDurableDirectory(string directory)
    {
        if (Directory.Exists(directory)) return;
        var parent = Path.GetDirectoryName(directory)!;
        CreateDurableDirectory(parent);
        Directory.CreateDirectory(directory);
        AuthorityDisk.FlushDirectory(parent);
    }

    private void Initialize()
    {
        var staging = AuthorityDisk.Temporary(DirectoryPath);
        CreateDurableDirectory(staging);
        foreach (var name in dataDirectories) CreateDurableDirectory(Path.Combine(staging, name));
        var updates = AuthorityDocuments.Areas.ToDictionary(area => area, area => AuthorityDocuments.Encode(AuthorityDocuments.Empty(area)));
        Publish(staging, null, 0, updates, AuthorityDocuments.MakeBatch(0, []));
        AuthorityDisk.Rename(staging, DirectoryPath, directory: true);
        try { AuthorityDisk.FlushDirectory(Root); }
        catch (IOException error) { throw new AuthorityCommitDurabilityException(0, error); }
    }

    private WriterLease AcquireWriter(bool exclusiveMaintenance)
    {
        var maintenance = DistributionMaintenanceLease.Acquire(Root, exclusiveMaintenance, create: false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var path = SafePath.ResolveInsideChecked(Root, ".writer.lock");
            while (true)
            {
                try { return new(this, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), maintenance); }
                catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) { Thread.Sleep(20); }
            }
        }
        catch { maintenance.Dispose(); throw; }
    }

    private AuthorityHead Publish(string directory, AuthorityHead? previous, long generation,
        IReadOnlyDictionary<AuthorityArea, byte[]> updates, AuthorityEventBatch batch)
    {
        var files = previous is null ? new Dictionary<string, string>() : new(previous.Files);
        var hashes = previous is null ? new Dictionary<string, string>() : new(previous.Sha256);
        foreach (var (area, bytes) in updates) WriteGeneration(AuthorityDocuments.Name(area), bytes);
        WriteGeneration(AuthorityDocuments.Events, AuthorityDocuments.Encode(batch));

        var head = AuthorityDocuments.Seal(new(AuthorityDocuments.Schema, generation, files, hashes, previous?.Generation, ""));
        AuthorityDisk.Write(Path.Combine(directory, "HEAD"), AuthorityDocuments.Encode(head), head: true);
        if (previous is null) AuthorityDisk.FlushDirectory(directory);
        else AuthorityDisk.CompleteHeadPublication(directory, generation);
        return AuthorityDocuments.Freeze(head);

        void WriteGeneration(string area, byte[] bytes)
        {
            var relative = AuthorityDocuments.RelativeFile(layout.DirectoryFor(area), generation);
            AuthorityDisk.Write(SafePath.ResolveInsideChecked(directory, relative), bytes);
            files[area] = relative;
            hashes[area] = AuthorityDocuments.Hash(bytes);
        }
    }

    private void CleanOrphans(AuthoritySnapshot snapshot)
    {
        foreach (var area in generationDirectories)
        {
            var directory = DataPath(area);
            foreach (var file in Directory.EnumerateFiles(directory))
                if (IsTemporary(file) || AuthorityDocuments.TryGeneration(area, area + "/" + Path.GetFileName(file), out var generation) &&
                    generation > snapshot.Generation)
                    AuthorityDisk.TryDelete(file);
        }
        foreach (var file in Directory.EnumerateFiles(DirectoryPath))
            if (IsTemporary(file)) AuthorityDisk.TryDelete(file);
        CleanMetadataOrphans(snapshot);
    }

    private void CleanMetadataOrphans(AuthoritySnapshot snapshot)
    {
        var catalog = snapshot.Get(AuthorityArea.Catalog);
        if (!catalog.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return;
        var live = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var release in releases.EnumerateArray())
        {
            // Integration owns the business schema. If it cannot be interpreted, preserve metadata.
            if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) return;
            live.Add(id.GetString()! + ".json");
        }
        foreach (var file in Directory.EnumerateFiles(ReleaseMetadataPath))
            if (IsTemporary(file) || file.EndsWith(".json", StringComparison.Ordinal) && !live.Contains(Path.GetFileName(file)))
                AuthorityDisk.TryDelete(file);
    }

    private static bool IsTemporary(string path)
    {
        var name = Path.GetFileName(path);
        var marker = name.LastIndexOf(".tmp-", StringComparison.Ordinal);
        return marker > 0 && Guid.TryParseExact(name.AsSpan(marker + 5), "N", out _);
    }

    private Exception? RunMaintenance(AuthoritySnapshot snapshot)
    {
        try
        {
            projection.Update(snapshot);
            foreach (var area in AuthorityDocuments.Areas.Select(AuthorityDocuments.Name))
            {
                var retained = snapshot.Head.Files[area];
                var directory = layout.DirectoryFor(area);
                foreach (var file in Directory.EnumerateFiles(DataPath(directory)))
                {
                    var relative = directory + "/" + Path.GetFileName(file);
                    if (relative != retained && AuthorityDocuments.TryGeneration(directory, relative, out var generation) &&
                        generation < snapshot.Generation - PriorGenerationsToKeep)
                        AuthorityDisk.TryDelete(file);
                }
            }
            return null;
        }
        catch (Exception error) when (IsReadError(error)) { return error; }
    }

    internal sealed class WriterLease(AuthorityStore owner, FileStream writer, IDisposable maintenance) : IDisposable
    {
        private readonly object gate = new();
        private bool disposed;
        private bool inUse;

        internal T Use<T>(AuthorityStore store, Func<T> operation)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (store != owner || inUse) throw new InvalidOperationException("Authority writer lease is mismatched or already in use.");
                inUse = true;
                try { return operation(); }
                finally { inUse = false; }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (inUse) throw new InvalidOperationException("Cannot release an active authority writer lease.");
                disposed = true;
                try { writer.Dispose(); }
                finally { maintenance.Dispose(); }
            }
        }
    }
}
