namespace UeDtLauncher.Distribution.Authority;

internal sealed partial class AuthorityStore
{
    private const string InitializationDirectory = ".authority-init.v1";
    private string InitializationPath => Path.Combine(Root, InitializationDirectory);

    private void Initialize()
    {
        if (CheckRoot()) return;
        // Reserve the workspace before creating locks. The directory's name and contents
        // distinguish an interrupted bootstrap from a foreign root with lock files.
        var staging = InitializationPath;
        AuthorityDisk.ReserveInitializationDirectory(staging);
        AuthorityDisk.FlushDirectory(Root);

        using var writer = AcquireWriter(exclusiveMaintenance: false);
        if (CheckRoot()) return; // Another initializer published while this caller waited.

        foreach (var name in dataDirectories) Directory.CreateDirectory(Path.Combine(staging, name));
        var documents = InitialDocuments();
        foreach (var (relative, bytes) in documents)
        {
            if (relative == "HEAD") continue;
            var path = SafePath.ResolveInsideChecked(staging, relative);
            // CheckRoot verified any existing final file against the canonical generation zero.
            if (!File.Exists(path)) AuthorityDisk.Write(path, bytes);
        }
        // Re-sync directories for resumed files as well as new files before publishing.
        foreach (var name in dataDirectories) AuthorityDisk.FlushDirectory(Path.Combine(staging, name));
        var headPath = Path.Combine(staging, "HEAD");
        if (!File.Exists(headPath)) AuthorityDisk.Write(headPath, documents["HEAD"], head: true);
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
            foreach (var path in Directory.EnumerateFileSystemEntries(staging))
            {
                var name = Path.GetFileName(path);
                if (dataDirectories.Contains(name, StringComparer.Ordinal))
                {
                    if (!IsOrdinaryDirectory(path)) return false;
                    foreach (var file in Directory.EnumerateFileSystemEntries(path))
                        if (!IsInitialFile(file, name + "/" + Path.GetFileName(file), documents)) return false;
                }
                else if (!IsInitialFile(path, name, documents)) return false;
            }
            // These names may exist only as ordinary empty locks created after the reservation.
            return names.Where(name => name != InitializationDirectory).All(name => IsEmptyLock(Path.Combine(Root, name)));
        }
        catch (FileNotFoundException) when (Attributes(DirectoryPath) is not null) { return false; }
        catch (DirectoryNotFoundException) when (Attributes(DirectoryPath) is not null) { return false; }
    }

    private static bool IsInitialFile(string path, string relative, IReadOnlyDictionary<string, byte[]> documents)
    {
        var temporary = IsTemporary(relative);
        var name = temporary ? relative[..relative.LastIndexOf(".tmp-", StringComparison.Ordinal)] : relative;
        if (!documents.TryGetValue(name, out var bytes)) return false;
        var attributes = Attributes(path);
        // An in-flight writer can have renamed its temporary file after enumeration.
        if (attributes is null) return temporary;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        return temporary || new FileInfo(path).Length == bytes.Length &&
            ReadFile(path, bytes.Length).AsSpan().SequenceEqual(bytes);
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
