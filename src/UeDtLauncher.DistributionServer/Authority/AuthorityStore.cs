using System.Diagnostics;
using System.Text.Json;

namespace UeDtLauncher.Distribution.Authority;

internal sealed class AuthorityStore
{
    internal const int ReadRetries = 3;
    internal const int PriorGenerationsToKeep = 8;
    internal static readonly AsyncLocal<Action<long>?> AfterReadHead = new();
    internal static readonly AsyncLocal<Action?> BeforeAcquireWriter = new();

    private readonly AuthorityAreaCache cache = new();
    private readonly AuthorityAuditProjection projection;
    private readonly AuthorityLayout layout;
    private readonly DistributionMaintenanceLease? inspectionLease;
    private readonly string[] generationDirectories;
    private readonly string[] dataDirectories;
    private readonly object requestGenerationGate = new();
    private AuthorityHead? validatedRequestGeneration;
    private (byte[] Bytes, AuthorityHead Head)? cachedHead;
    private readonly object headGate = new();
    private RequestArea? requestAuth;
    private RequestArea? requestCatalog;
    private string Root { get; }
    internal string DirectoryPath { get; }
    internal string ReleaseMetadataPath => DataPath(layout.ReleaseMetadataDirectory);
    internal string ProgressPath => DataPath(layout.ProgressDirectory);
    internal Exception? OpenMaintenanceError { get; private set; }

    private AuthorityStore(string root, AuthorityLayout layout, DistributionMaintenanceLease? inspectionLease = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.inspectionLease = inspectionLease;
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

    internal static AuthorityStore Open(string root, AuthorityLayout? layout = null,
        Action<AuthoritySnapshot>? validate = null) =>
        OpenCore(new(root, layout ?? AuthorityLayout.Default), validate);

    private static AuthorityStore OpenCore(AuthorityStore store, Action<AuthoritySnapshot>? validate = null)
    {
        // Validation precedes creation of even the maintenance/writer locks.
        if (!store.CheckRoot(validateHistory: true, out var previous)) store.Initialize();
        else validate?.Invoke(previous!);
        using var writer = store.AcquireWriter(exclusiveMaintenance: false);
        if (!store.CheckRoot(validateHistory: false, out var current))
            throw new AuthorityRootException(AuthorityRootKind.Damaged, "Authority HEAD is missing.");
        if (previous?.Head.HeadSha256 != current!.Head.HeadSha256) validate?.Invoke(current);
        store.OpenMaintenanceError = store.Recover(writer);
        return store;
    }

    internal static AuthorityStore OpenReadOnly(string root, DistributionMaintenanceLease exclusiveLease, AuthorityLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(exclusiveLease);
        var store = new AuthorityStore(root, layout ?? AuthorityLayout.Default, exclusiveLease);
        return exclusiveLease.UseExclusive(store.Root, () =>
        {
            if (!store.CheckRoot(validateHistory: true))
                throw new AuthorityRootException(AuthorityRootKind.Damaged, "Authority HEAD is missing.");
            return store;
        });
    }

    // CLI inspection must reject old, mixed, and damaged roots without creating any file or lock.
    internal static AuthoritySnapshot InspectExisting(string root, bool validateHistory = true)
    {
        var store = new AuthorityStore(root, AuthorityLayout.Default);
        return store.CheckRoot(validateHistory, out var snapshot)
            ? snapshot!
            : throw new AuthorityRootException(AuthorityRootKind.Damaged, "Authority HEAD is missing.");
    }

    internal AuthoritySnapshot ReadSnapshot() => inspectionLease is null
        ? ReadSnapshotCore(request: false, includeCatalog: true)
        : inspectionLease.UseExclusive(Root, () => ReadSnapshotCore(request: false, includeCatalog: true));

    internal AuthoritySnapshot ReadRequestSnapshot(bool includeCatalog, bool verifyCatalogBytes = true) =>
        ReadSnapshotCore(request: true, includeCatalog, verifyCatalogBytes);

    private AuthoritySnapshot ReadSnapshotCore(bool request, bool includeCatalog, bool verifyCatalogBytes = true)
    {
        using var measurement = DistributionPerformance.MeasureAuthorityRead();
        for (var attempt = 0; ; attempt++)
        {
            try { return LoadSnapshot(request, includeCatalog, verifyCatalogBytes); }
            catch (Exception error) when (IsReadError(error))
            {
                if (attempt == ReadRetries)
                    throw new AuthorityStoreException("Could not read a valid authority snapshot after three retries.", error);
                DistributionPerformance.RecordAuthorityReadRetry();
                if (error is FileNotFoundException or DirectoryNotFoundException ||
                    OperatingSystem.IsWindows() && (error.HResult & 0xFFFF) is 32 or 33)
                {
                    // Missing or sharing-conflicted entries can become visible after a concurrent
                    // rename or cleanup. Space out the same three HEAD re-reads.
                    Thread.Sleep(4 << attempt);
                }
                else Thread.Yield();
            }
        }
    }

    internal WriterLease Lock(bool exclusiveMaintenance = false)
    {
        if (inspectionLease is not null) throw new InvalidOperationException("This authority store was opened for read-only inspection.");
        if (!CheckRoot()) throw new AuthorityRootException(AuthorityRootKind.Damaged, "Authority HEAD is missing.");
        return AcquireWriter(exclusiveMaintenance);
    }

    internal AuthorityCommitResult Commit(Func<AuthoritySnapshot, CommitPlan> makePlan)
    {
        using var writer = Lock();
        return CommitUnderHeldLock(writer, makePlan);
    }

    internal AuthorityCommitResult CommitSequence(Func<AuthoritySnapshot, JsonElement> reserve)
    {
        using var writer = Lock();
        return CommitUnderHeldLock(writer, snapshot => new(
            new Dictionary<AuthorityArea, JsonElement> { [AuthorityArea.Sequence] = reserve(snapshot) }, []),
            sweepReleaseMetadata: false);
    }

    internal AuthorityCommitResult CommitOffline(DistributionMaintenanceLease maintenance, Func<AuthoritySnapshot, CommitPlan> makePlan) =>
        maintenance.UseExclusive(Root, () =>
        {
            using var writer = new WriterLease(this, OpenWriterFile(Root), null);
            return CommitUnderHeldLock(writer, makePlan);
        });

    internal AuthorityCommitResult CommitUnderHeldLock(WriterLease writer, Func<AuthoritySnapshot, CommitPlan> makePlan,
        bool sweepReleaseMetadata = true) =>
        writer.Use<AuthorityCommitResult>(this, () =>
        {
            using var measurement = DistributionPerformance.MeasureAuthorityCommit();
            var snapshot = ReadSnapshot();
            PrepareCleanup(snapshot, sweepReleaseMetadata);
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
        try
        {
            var snapshot = ReadSnapshot();
            PrepareCleanup(snapshot);
            return RunMaintenance(snapshot);
        }
        catch (Exception error) when (IsReadError(error)) { return error; }
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

    internal AuthorityEventBatch ReadBatch(AuthoritySnapshot snapshot, long generation) => inspectionLease is null
        ? ReadBatchCore(snapshot, generation)
        : inspectionLease.UseExclusive(Root, () => ReadBatchCore(snapshot, generation));

    private AuthorityEventBatch ReadBatchCore(AuthoritySnapshot snapshot, long generation)
    {
        ValidateSnapshotOwner(snapshot);
        if (generation < 0 || generation > snapshot.Generation) throw new ArgumentOutOfRangeException(nameof(generation));
        var relative = AuthorityDocuments.RelativeFile(layout.EventsDirectory, generation);
        try
        {
            var bytes = ReadCommittedFile(relative);
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

    private AuthoritySnapshot LoadSnapshot(bool request, bool includeCatalog, bool verifyCatalogBytes)
    {
        var head = ReadHead();
        AfterReadHead.Value?.Invoke(head.Generation);
        if (request)
        {
            var requested = new Dictionary<AuthorityArea, JsonElement>
            {
                [AuthorityArea.Auth] = ReadRequestArea(head, AuthorityArea.Auth, ref requestAuth, verifyBytes: false)
            };
            if (includeCatalog)
                requested.Add(AuthorityArea.Catalog, ReadRequestArea(head, AuthorityArea.Catalog, ref requestCatalog, verifyCatalogBytes));
            ValidateRequestGeneration(head, includeCatalog);
            return new(this, head, requested);
        }

        var areas = new Dictionary<AuthorityArea, JsonElement>();
        foreach (var area in AuthorityDocuments.Areas)
            areas.Add(area, ReadArea(head, area));
        VerifyCurrentEvents(head);
        return new(this, head, areas);
    }

    private AuthorityHead ReadHead()
    {
        var bytes = ReadCommittedFile("HEAD", maxBytes: 64 * 1024);
        lock (headGate)
        {
            if (cachedHead is { } cached && bytes.AsSpan().SequenceEqual(cached.Bytes)) return cached.Head;
            var head = AuthorityDocuments.Decode<AuthorityHead>(bytes);
            AuthorityDocuments.Validate(head, layout);
            if (!bytes.AsSpan().SequenceEqual(AuthorityDocuments.Encode(head)))
                throw new InvalidDataException("Authority HEAD is not in its canonical format.");
            var frozen = AuthorityDocuments.Freeze(head);
            cachedHead = (bytes, frozen);
            return frozen;
        }
    }

    private JsonElement ReadArea(AuthorityHead head, AuthorityArea area)
        => ReadArea(head, area, out _);

    private JsonElement ReadArea(AuthorityHead head, AuthorityArea area, out int size)
    {
        var name = AuthorityDocuments.Name(area);
        var relative = head.Files[name];
        var digest = head.Sha256[name];
        var content = ReadCommittedFile(relative);
        size = content.Length;
        VerifyHash(content, digest);
        return cache.Parse(relative, digest, content);
    }

    private JsonElement ReadRequestArea(AuthorityHead head, AuthorityArea area, ref RequestArea? slot, bool verifyBytes)
    {
        var name = AuthorityDocuments.Name(area);
        var key = (head.Files[name], head.Sha256[name]);
        try
        {
            if (!verifyBytes && Volatile.Read(ref slot) is { } current && current.Key == key)
            {
                // Only the authenticated HEAD reference identifies the verified content.
                // Continue checking that the selected file exists and its path is regular.
                _ = ResolveCommittedFile(key.Item1, requireFile: true);
                return current.Document;
            }
            var document = ReadArea(head, area, out var size);
            Volatile.Write(ref slot, size <= AuthorityAreaCache.ByteLimit ? new RequestArea(key, document) : null);
            return document;
        }
        catch { Volatile.Write(ref slot, null); throw; }
    }

    private sealed record RequestArea((string Path, string Digest) Key, JsonElement Document);

    private void ValidateRequestGeneration(AuthorityHead head, bool catalogRead)
    {
        if (Volatile.Read(ref validatedRequestGeneration)?.HeadSha256 == head.HeadSha256) return;
        lock (requestGenerationGate)
        {
            if (validatedRequestGeneration?.HeadSha256 == head.HeadSha256) return;
            _ = ReadArea(head, AuthorityArea.Jobs);
            _ = ReadArea(head, AuthorityArea.Sequence);
            if (!catalogRead) _ = ReadArea(head, AuthorityArea.Catalog);
            VerifyCurrentEvents(head);
            if (validatedRequestGeneration is null || validatedRequestGeneration.Generation <= head.Generation)
                Volatile.Write(ref validatedRequestGeneration, head);
        }
    }

    private void VerifyCurrentEvents(AuthorityHead head)
    {
        var events = ReadCommittedFile(head.Files[AuthorityDocuments.Events]);
        VerifyHash(events, head.Sha256[AuthorityDocuments.Events]);
        DecodeBatch(events, head.Generation);
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

    private byte[] ReadCommittedFile(string relative, int maxBytes = int.MaxValue)
        => ReadFile(ResolveCommittedFile(relative, requireFile: false), maxBytes);

    private string ResolveCommittedFile(string relative, bool requireFile)
    {
        var path = SafePath.ResolveInside(DirectoryPath, relative);
        // A single attribute lookup checks each component. File.Exists followed by GetAttributes
        // performs the same probe twice and suppresses access errors on the first probe.
        for (var component = path; component.Length > DirectoryPath.Length; component = Path.GetDirectoryName(component)!)
        {
            var attributes = Attributes(component);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                throw new IOException("Authority storage refuses linked directories.");
            if (requireFile && component == path &&
                (attributes is null || (attributes & FileAttributes.Directory) != 0))
                throw new FileNotFoundException("Referenced authority file is unavailable.", path);
        }
        return path;
    }

    private static byte[] ReadFile(string path, int maxBytes = int.MaxValue)
    {
        using var stream = AuthorityDisk.OpenReader(path);
        if (stream.Length > maxBytes) throw new InvalidDataException("Authority document exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Authority file length changed while reading.");
        return bytes;
    }

    private bool CheckRoot(bool validateHistory = false) => CheckRoot(validateHistory, out _);

    private bool CheckRoot(bool validateHistory, out AuthoritySnapshot? validatedSnapshot)
    {
        validatedSnapshot = null;
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
            var hasAuthority = Attributes(DirectoryPath) is not null;
            foreach (var file in names)
                if (file.Equals("distribution.db", StringComparison.OrdinalIgnoreCase) ||
                    (file.Equals("distribution.db-shm", StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("distribution.db-journal", StringComparison.OrdinalIgnoreCase)) ||
                    file.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                    file.StartsWith("distribution.pre-v6-", StringComparison.OrdinalIgnoreCase) &&
                    file.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    throw new AuthorityRootException(AuthorityRootKind.LegacyDatabase, "Legacy distribution database detected.");

            if (!hasAuthority)
            {
                foreach (var marker in layout.ExistingDistributionMarkers ?? AuthorityLayout.Default.ExistingDistributionMarkers!)
                    if (Attributes(SafePath.ResolveInsideChecked(Root, marker)) is not null)
                        throw new AuthorityRootException(AuthorityRootKind.ExistingDistribution, "Distribution files exist without an authority HEAD.");
                if (names.Length != 0)
                {
                    // A concurrent initializer can publish authority between enumeration and this probe.
                    if (Attributes(DirectoryPath) is not null) return CheckRoot(validateHistory, out validatedSnapshot);
                    throw new AuthorityRootException(AuthorityRootKind.UnrecognizedLayout, "Existing files use an unrecognized distribution layout.");
                }
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
            validatedSnapshot = snapshot;
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
        var parent = Path.GetDirectoryName(Root) ?? throw new IOException("Authority initialization requires a parent directory.");
        CreateDurableDirectory(Root);
        var staging = AuthorityDisk.Temporary(Path.Combine(parent, ".authority-init"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var name in dataDirectories) CreateDurableDirectory(Path.Combine(staging, name));
            var updates = AuthorityDocuments.Areas.ToDictionary(area => area, area => AuthorityDocuments.Encode(AuthorityDocuments.Empty(area)));
            Publish(staging, null, 0, updates, AuthorityDocuments.MakeBatch(0, []));
            AuthorityDisk.FlushDirectory(parent);
            AuthorityDisk.Reach(AuthorityBoundary.InitializationPrepared);
            if (CheckRoot()) return; // A concurrent opener published a complete authority first.
            try { AuthorityDisk.Rename(staging, DirectoryPath, directory: true); }
            catch (IOException) when (Attributes(DirectoryPath) is not null && CheckRoot()) { return; }
            try
            {
                AuthorityDisk.Reach(AuthorityBoundary.InitializationPublished);
                AuthorityDisk.FlushDirectory(Root);
                AuthorityDisk.FlushDirectory(parent);
            }
            catch (IOException error) { throw new AuthorityCommitDurabilityException(0, error); }
        }
        finally
        {
            // This path is unique to this initializer. A killed process leaves it outside the selected root.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private WriterLease AcquireWriter(bool exclusiveMaintenance)
    {
        BeforeAcquireWriter.Value?.Invoke();
        var maintenance = DistributionMaintenanceLease.Acquire(Root, exclusiveMaintenance, create: false);
        try { return new(this, OpenWriterFile(Root), maintenance); }
        catch { maintenance.Dispose(); throw; }
    }

    private static FileStream OpenWriterFile(string root)
    {
        var path = SafePath.ResolveInsideChecked(root, ".writer.lock");
        using var wait = DistributionPerformance.MeasurePhase("authority-writer-wait");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) { Thread.Sleep(20); }
        }
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

    private void PrepareCleanup(AuthoritySnapshot snapshot, bool sweepReleaseMetadata = true)
    {
        // A preceding writer may have died after rename but before flushing the directory.
        // Keep every old reference and orphan until the currently visible HEAD is durable.
        AuthorityDisk.FlushDirectory(Root);
        AuthorityDisk.Reach(AuthorityBoundary.BeforeCleanupDirectoryFlush);
        AuthorityDisk.FlushDirectory(DirectoryPath);
        CleanOrphans(snapshot, sweepReleaseMetadata);
    }

    private void CleanOrphans(AuthoritySnapshot snapshot, bool sweepReleaseMetadata)
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
        // A sequence reservation does not publish release paths. Startup recovery and
        // publication still sweep metadata before a new publication can use its path.
        if (sweepReleaseMetadata) CleanMetadataOrphans(snapshot);
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
        var files = Directory.EnumerateFiles(ReleaseMetadataPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        });
        foreach (var file in files)
            if (IsTemporary(file) || file.EndsWith(".json", StringComparison.Ordinal) &&
                !live.Contains(Path.GetRelativePath(ReleaseMetadataPath, file).Replace(Path.DirectorySeparatorChar, '/')))
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

    internal sealed class WriterLease(AuthorityStore owner, FileStream writer, IDisposable? maintenance) : IDisposable
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
                finally { maintenance?.Dispose(); }
            }
        }
    }
}
