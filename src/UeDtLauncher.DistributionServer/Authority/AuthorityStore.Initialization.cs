namespace UeDtLauncher.Distribution.Authority;

internal sealed partial class AuthorityStore
{
    private const string InitializationDirectory = ".authority-init.v1";
    private const string OwnerMarkerPrefix = ".bootstrap-owner.tmp-";
    private string InitializationPath => Path.Combine(Root, InitializationDirectory);

    private void Initialize()
    {
        if (CheckRoot()) return;
        // Reserve the workspace before creating locks. The directory's name and contents
        // distinguish an interrupted bootstrap from a foreign root with lock files.
        var staging = InitializationPath;
        CheckInitializationMounts();
        AuthorityDisk.ReserveInitializationDirectory(staging);
        CheckInitializationMounts();
        AuthorityDisk.FlushDirectory(Root);

        using var writer = AcquireWriter(exclusiveMaintenance: false);
        if (CheckRoot()) return; // Another initializer published while this caller waited.

        var owner = InitializationOwner(staging);
        if (owner is null)
        {
            // The empty marker is created atomically. Its nonce identifies exactly
            // which scratch files belong to this initialization across restarts.
            owner = Guid.NewGuid().ToString("N");
            using (File.Open(Path.Combine(staging, OwnerMarkerPrefix + owner), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            AuthorityDisk.FlushDirectory(staging);
        }
        foreach (var name in dataDirectories) Directory.CreateDirectory(Path.Combine(staging, name));
        var documents = InitialDocuments();
        foreach (var (relative, bytes) in documents)
        {
            if (relative == "HEAD") continue;
            var path = SafePath.ResolveInsideChecked(staging, relative);
            // CheckRoot verified any existing final file against the canonical generation zero.
            if (!File.Exists(path)) AuthorityDisk.Write(path, bytes, initializationOwner: owner);
        }
        // Re-sync directories for resumed files as well as new files before publishing.
        foreach (var name in dataDirectories) AuthorityDisk.FlushDirectory(Path.Combine(staging, name));
        var headPath = Path.Combine(staging, "HEAD");
        if (!File.Exists(headPath)) AuthorityDisk.Write(headPath, documents["HEAD"], head: true, initializationOwner: owner);
        AuthorityDisk.FlushDirectory(staging);
        AuthorityDisk.Reach(AuthorityBoundary.InitializationPrepared);
        AuthorityDisk.Rename(staging, DirectoryPath, directory: true);
        try
        {
            AuthorityDisk.Reach(AuthorityBoundary.InitializationPublished);
            AuthorityDisk.FlushDirectory(Root);
        }
        catch (IOException error) { throw new AuthorityCommitDurabilityException(0, error); }
    }

    private void CheckInitializationMounts()
    {
        if (!IsOrdinaryDirectory(Root)) return;
        CheckInitializationMount(InitializationPath, "initialization staging");
        CheckInitializationMount(DirectoryPath, "authority");
    }

    private void CheckInitializationMount(string path, string component)
    {
        if ((Attributes(path) & FileAttributes.Directory) != FileAttributes.Directory) return;
        var rootMount = AuthorityDisk.DirectoryMountId(Root);
        var mount = AuthorityDisk.DirectoryMountId(path);
        if (mount is not null && mount != rootMount)
            throw new AuthorityRootException(AuthorityRootKind.MountMismatch,
                $"The {component} directory must be on the same mount as the configured store root.");
    }

    private bool IsPendingInitialization(string[] names)
    {
        if (!names.Contains(InitializationDirectory, StringComparer.Ordinal) ||
            names.Any(name => name is not (InitializationDirectory or ".maintenance.lock" or ".writer.lock")))
            return false;

        var staging = InitializationPath;
        try
        {
            if (!IsOrdinaryDirectory(staging)) return false;
            var documents = InitialDocuments();
            var entries = Directory.GetFileSystemEntries(staging);
            var owner = InitializationOwner(staging);
            if (owner is null && entries.Length != 0) return false;
            foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                if (owner is not null && name == OwnerMarkerPrefix + owner) continue;
                if (dataDirectories.Contains(name, StringComparer.Ordinal))
                {
                    if (!IsOrdinaryDirectory(path)) return false;
                    CheckInitializationMount(path, "initialization data");
                    foreach (var file in Directory.EnumerateFileSystemEntries(path))
                        if (!IsInitialFile(file, name + "/" + Path.GetFileName(file), documents, owner)) return false;
                }
                else if (!IsInitialFile(path, name, documents, owner)) return false;
            }
            // These names may exist only as ordinary empty locks created after the reservation.
            return names.Where(name => name != InitializationDirectory).All(name => IsEmptyLock(Path.Combine(Root, name)));
        }
        catch (FileNotFoundException) when (Attributes(DirectoryPath) is not null) { return false; }
        catch (DirectoryNotFoundException) when (Attributes(DirectoryPath) is not null) { return false; }
    }

    private static string? InitializationOwner(string staging)
    {
        var markers = Directory.GetFileSystemEntries(staging, OwnerMarkerPrefix + "*");
        if (markers.Length != 1 || !IsEmptyLock(markers[0]) || !AuthorityDisk.MatchesInitializationPrefix(markers[0], [])) return null;
        var name = Path.GetFileName(markers[0]);
        var nonce = name.AsSpan(OwnerMarkerPrefix.Length);
        return name.StartsWith(OwnerMarkerPrefix, StringComparison.Ordinal) && Guid.TryParseExact(nonce, "N", out _)
            ? nonce.ToString() : null;
    }

    private static bool IsInitialFile(string path, string relative, IReadOnlyDictionary<string, byte[]> documents, string? owner)
    {
        if (documents.TryGetValue(relative, out var bytes))
            return Attributes(path) is { } committed && (committed & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                new FileInfo(path).Length == bytes.Length && ReadFile(path, bytes.Length).AsSpan().SequenceEqual(bytes);

        if (owner is null || !IsTemporary(relative)) return false;
        var stem = relative[..relative.LastIndexOf(".tmp-", StringComparison.Ordinal)];
        var ownershipSuffix = ".init-" + owner;
        if (!stem.EndsWith(ownershipSuffix, StringComparison.Ordinal) ||
            !documents.TryGetValue(stem[..^ownershipSuffix.Length], out bytes)) return false;
        var attributes = Attributes(path);
        if (attributes is null) return true; // The writer already renamed its recognized temporary file.
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        try
        {
            return AuthorityDisk.MatchesInitializationPrefix(path, bytes);
        }
        catch (FileNotFoundException) { return true; } // Atomically renamed after we enumerated it.
    }

    private static bool IsOrdinaryDirectory(string path) => (Attributes(path) &
        (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    private static bool IsEmptyLock(string path) => Attributes(path) is { } attributes &&
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && new FileInfo(path).Length == 0;

    private Dictionary<string, byte[]> InitialDocuments()
    {
        var areas = AuthorityDocuments.Areas.ToDictionary(AuthorityDocuments.Name,
            area => AuthorityDocuments.Encode(AuthorityDocuments.Empty(area)), StringComparer.Ordinal);
        areas.Add(AuthorityDocuments.Events, AuthorityDocuments.Encode(AuthorityDocuments.MakeBatch(0, [])));
        var files = areas.ToDictionary(pair => pair.Key,
            pair => AuthorityDocuments.RelativeFile(layout.DirectoryFor(pair.Key), 0), StringComparer.Ordinal);
        var hashes = areas.ToDictionary(pair => pair.Key, pair => AuthorityDocuments.Hash(pair.Value), StringComparer.Ordinal);
        var head = AuthorityDocuments.Seal(new(AuthorityDocuments.Schema, 0, files, hashes, null, ""));
        var documents = areas.ToDictionary(pair => files[pair.Key], pair => pair.Value, StringComparer.Ordinal);
        documents.Add("HEAD", AuthorityDocuments.Encode(head));
        return documents;
    }

    private void RemoveEmptyInitializationDirectory()
    {
        // A caller admitted before publication can reserve an empty workspace after the
        // rename. It cannot write to it without obtaining this same writer lease.
        if (!IsOrdinaryDirectory(InitializationPath) || Directory.EnumerateFileSystemEntries(InitializationPath).Any()) return;
        Directory.Delete(InitializationPath);
        AuthorityDisk.FlushDirectory(Root);
    }
}
