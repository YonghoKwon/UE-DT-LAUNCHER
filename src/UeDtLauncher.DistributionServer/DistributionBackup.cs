using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

public sealed record BackupFile(string Path, long Bytes, string Sha256);
public sealed record DistributionBackupManifest(int SchemaVersion, string SourceRoot, string Origin, string SigningKeyId,
    string SigningPublicKeySha256, string PolicySha256, IReadOnlyList<BackupFile> Files);

public static class DistributionBackup
{
    internal static readonly AsyncLocal<Action<string>?> Boundary=new(); // Tests only; no operational bypass input.
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool Inside(string root, string target) => string.Equals(Path.GetFullPath(root), Path.GetFullPath(target), Comparison) ||
        Path.GetFullPath(target).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);
    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    internal static IReadOnlyList<string> Files(string root)
    {
        var output = new List<string>(); var pending = new Stack<(string Directory, int Depth)>(); pending.Push((root, 0));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > 128) throw new IOException("Tree exceeds maintenance depth limit.");
            if ((File.GetAttributes(current.Directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Maintenance refuses linked trees.");
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Maintenance refuses linked entries.");
                if (Directory.Exists(path)) pending.Push((path, current.Depth + 1)); else output.Add(path);
                if (output.Count + pending.Count > 500000) throw new IOException("Tree exceeds maintenance entry limit.");
            }
        }
        return output.Order(StringComparer.Ordinal).ToArray();
    }
    private static string Signer(DistributionSettings settings)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(settings.SigningKeyPath));
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }
    private static async Task VerifyPublishedReleaseAsync(string directory, string releaseId, ReleaseSidecar metadata, DistributionSettings settings)
    {
        metadata.Validate();
        if (metadata.ReleaseId != releaseId) throw new InvalidDataException("Release identity differs from its metadata.");
        // Matching two corrupted copies is not a cryptographic verification.
        var document = System.Text.Encoding.UTF8.GetString(await ReadBoundedAsync(Path.Combine(directory, "manifest.json"), 8 * 1024 * 1024));
        var signature = JsonSerializer.Deserialize<DetachedSignatureEnvelope>(await ReadBoundedAsync(Path.Combine(directory, "manifest.json.sig"), 64 * 1024), JsonFiles.Options)
            ?? throw new InvalidDataException("Missing release signature.");
        if (signature.SchemaVersion != 2 || signature.Algorithm != "ECDSA-P256-SHA256" || signature.KeyId != settings.SigningKeyId)
            throw new InvalidDataException("Release signing identity is unavailable; keep restore staged for operator review.");
        using var signer = ECDsa.Create(); signer.ImportFromPem(await File.ReadAllTextAsync(settings.SigningKeyPath));
        ManifestSignatureVerifier.Verify(document, signature.Signature, signer.ExportSubjectPublicKeyInfoPem());
        if (signature.PayloadSha256 is not null && !string.Equals(signature.PayloadSha256, Digest(System.Text.Encoding.UTF8.GetBytes(document)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release signature payload digest differs.");
        var manifest = JsonSerializer.Deserialize<LauncherManifest>(document, JsonFiles.Options)
            ?? throw new InvalidDataException("Missing release manifest.");
        if (manifest.AppId != metadata.ProjectId || manifest.Version != metadata.Version || manifest.Channel != metadata.Channel ||
            manifest.Platform != metadata.Platform || manifest.EntryPoint != metadata.EntryPoint ||
            manifest.BaseUrl != settings.PublicUrl.TrimEnd('/') + "/releases/" + releaseId + "/files" ||
            manifest.Files is null || manifest.Files.Count is < 1 or > 250000)
            throw new InvalidDataException("Release manifest no longer matches the approved identity.");
        var filesRoot = Path.Combine(directory, "files");
        var expected = new HashSet<string>(metadata.Platform == "windows-x64" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Null release file.");
            var relative = ReleaseSidecar.Relative(file.Path);
            if (!expected.Add(relative) || file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) || file.Url != relative)
                throw new InvalidDataException("Invalid release file metadata.");
            var path = SafePath.ResolveInsideChecked(filesRoot, relative);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !await Hashing.Sha256MatchesAsync(path, file.Sha256))
                throw new InvalidDataException("Release content fails its signed manifest.");
        }
        if (!expected.Contains(manifest.EntryPoint) || !expected.SetEquals(Files(filesRoot).Select(path => Path.GetRelativePath(filesRoot, path).Replace('\\', '/'))))
            throw new InvalidDataException("Release payload inventory differs from its signed manifest.");
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int limit)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("Release metadata exceeds its limit.");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes);
        return bytes;
    }
    public static object Plan(DistributionSettings settings) => new
    {
        Root = Path.GetFullPath(settings.Root), Mode = "offline-manual", PrivateSigningKeyIncluded = false,
        Directories = new[] { "incoming", "processing", "releases", "archive", "authority" },
        Requirement = "Stop serve/watch/workers; run as server owner/admin. Complete-source-loss recovery is excluded."
    };
    private static AuthoritySnapshot ReadValidatedAuthority(string root)
    {
        var snapshot = AuthorityStore.InspectExisting(root);
        CatalogAuthority.ValidateRoot(snapshot, root);
        return snapshot;
    }

    public static async Task<DistributionBackupManifest> CreateAsync(DistributionSettings settings, string destination)
    {
        var root = Path.GetFullPath(settings.Root); destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || Inside(root, destination)) throw new IOException("Backup needs a new directory outside server root.");
        _ = ReadValidatedAuthority(root);
        using var lease = DistributionMaintenanceLease.Acquire(root, true, create: false);
        var authority = ReadValidatedAuthority(root);
        using var oldServer = new AuthenticationProcessLease(root);
        foreach(var journal in Directory.EnumerateFiles(root,"retention-*.json"))
            if(new FileInfo(journal).Length>64*1024*1024 || (await JsonFiles.ReadAsync<RetentionJournal>(journal)).Phase!="Completed")
                throw new InvalidDataException("Complete interrupted retention before backup.");
        MaintenanceStorage.PrivateDirectory(destination);
        var content = Path.Combine(destination, "content"); Directory.CreateDirectory(content);
        foreach (var name in new[] { "incoming", "processing", "releases", "archive" })
        {
            var source = SafePath.ResolveInsideChecked(root, name); if (!Directory.Exists(source)) continue;
            foreach (var file in Files(source))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(settings.SigningKeyPath), Comparison)) continue;
                var target = SafePath.ResolveInsideChecked(content, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
            }
        }
        CopyAuthority(authority, content);
        var policy = await File.ReadAllBytesAsync(settings.PolicyPath); await File.WriteAllBytesAsync(Path.Combine(destination, "policy.json"), policy);
        var entries = new List<BackupFile>();
        foreach (var path in Files(content)) entries.Add(new(Path.GetRelativePath(content, path).Replace('\\', '/'), new FileInfo(path).Length, await Hashing.Sha256FileAsync(path)));
        var manifest = new DistributionBackupManifest(1, root, settings.PublicUrl.TrimEnd('/'), settings.SigningKeyId, Signer(settings), Digest(policy), entries);
        await JsonFiles.WriteAsync(Path.Combine(destination, "backup.json"), manifest);
        await VerifyAsync(destination); return manifest;
    }
    public static async Task<DistributionBackupManifest> VerifyAsync(string directory)
    {
        var path = SafePath.ResolveInsideChecked(directory, "backup.json");
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Backup manifest too large.");
        var manifest = await JsonFiles.ReadAsync<DistributionBackupManifest>(path);
        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Count > 500000) throw new InvalidDataException("Invalid backup format.");
        var content = Path.Combine(directory, "content"); var expected = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var target = SafePath.ResolveInsideChecked(content, file.Path);
            if (!expected.Add(Path.GetFullPath(target)) || !File.Exists(target) || new FileInfo(target).Length != file.Bytes || !await Hashing.Sha256MatchesAsync(target, file.Sha256))
                throw new InvalidDataException("Backup content mismatch.");
        }
        if (!expected.SetEquals(Files(content).Select(Path.GetFullPath))) throw new InvalidDataException("Backup contains unlisted files.");
        if (Digest(await File.ReadAllBytesAsync(Path.Combine(directory, "policy.json"))) != manifest.PolicySha256) throw new InvalidDataException("Backup policy mismatch.");
        ValidateAudit(ReadValidatedAuthority(content));
        return manifest;
    }

    private static string PrepareAuthorityDirectories(string target)
    {
        var layout = AuthorityLayout.Default;
        var directory = SafePath.ResolveInsideChecked(target, layout.AuthorityDirectory);
        Directory.CreateDirectory(directory);
        foreach (var name in AuthorityDocuments.Areas.Select(area => layout.DirectoryFor(AuthorityDocuments.Name(area)))
            .Concat([layout.EventsDirectory, layout.ReleaseMetadataDirectory, layout.ProgressDirectory]))
            Directory.CreateDirectory(SafePath.ResolveInsideChecked(directory, name));
        AuthorityDisk.FlushDirectory(directory);
        AuthorityDisk.FlushDirectory(target);
        return directory;
    }

    // The source lease freezes HEAD and its files. The destination remains fenced during activation.
    private static void CopyAuthority(AuthoritySnapshot snapshot, string target, bool activating = false)
    {
        var directory = PrepareAuthorityDirectories(target);
        IEnumerable<string> FilesInSnapshot()
        {
            foreach (var path in snapshot.Head.Files.Values) yield return path;
            for (long generation = 0; generation < snapshot.Generation; generation++)
                yield return AuthorityDocuments.RelativeFile(AuthorityLayout.Default.EventsDirectory, generation);
            var catalog = new CatalogAuthority(snapshot.Store, Path.GetDirectoryName(snapshot.Store.DirectoryPath)!);
            foreach (var release in catalog.Snapshot(snapshot).Releases)
                yield return AuthorityLayout.Default.ReleaseMetadataDirectory + "/" + release.ReleaseId + ".json";
        }
        foreach (var path in FilesInSnapshot())
        {
            var bytes = File.ReadAllBytes(snapshot.Store.DataPath(path));
            var destination = SafePath.ResolveInsideChecked(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) AuthorityDisk.Write(destination, bytes);
            else
            {
                using var existing = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (existing.Length != bytes.Length)
                    throw new InvalidDataException("Staged authority has a conflicting immutable generation file.");
                var copied = new byte[bytes.Length];
                existing.ReadExactly(copied);
                if (!bytes.AsSpan().SequenceEqual(copied))
                    throw new InvalidDataException("Staged authority has a conflicting immutable generation file.");
                existing.Flush(flushToDisk: true);
                AuthorityDisk.FlushDirectory(Path.GetDirectoryName(destination)!);
            }
        }
        ValidateAudit(snapshot);
        CopyProjection(snapshot.Store.DataPath("audit.jsonl"), SafePath.ResolveInsideChecked(directory, "audit.jsonl"));
        if (activating) Boundary.Value?.Invoke("authority-files-copied");
        var head = SafePath.ResolveInsideChecked(directory, "HEAD");
        AuthorityDisk.Write(head, File.ReadAllBytes(snapshot.Store.DataPath("HEAD")), head: true);
        AuthorityDisk.CompleteHeadPublication(directory, snapshot.Generation);
        if (activating) Boundary.Value?.Invoke("authority-head-replaced");
        ValidateAudit(ReadValidatedAuthority(target));
    }

    private static void CopyProjection(string source, string destination)
    {
        var temporary = AuthorityDisk.Temporary(destination);
        try
        {
            using var input = AuthorityDisk.OpenReader(source);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            AuthorityDisk.Rename(temporary, destination, replace: true);
            AuthorityDisk.FlushDirectory(Path.GetDirectoryName(destination)!);
        }
        finally { AuthorityDisk.TryDelete(temporary); }
    }

    private static void ValidateAudit(AuthoritySnapshot snapshot)
    {
        using var input = AuthorityDisk.OpenReader(snapshot.Store.DataPath("audit.jsonl"));
        foreach (var entry in snapshot.Store.ReadAudit(snapshot))
        {
            var expected = AuthorityDocuments.Encode(entry);
            var line = new byte[expected.Length];
            input.ReadExactly(line);
            if (!expected.AsSpan().SequenceEqual(line) || input.ReadByte() != (byte)'\n')
                throw new InvalidDataException("Audit projection differs from committed events.");
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException("Audit projection contains data beyond committed events.");
    }

    private static void RebaseReleaseDirectories(SqliteConnection db, string original, string target)
    {
        using var tx = db.BeginTransaction();
        using var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT rowid,directory FROM releases";
        var rows = new List<(long Id, string Path)>();
        using (var r = q.ExecuteReader()) while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        foreach (var row in rows)
        {
            var old = Path.GetFullPath(row.Path);
            if (!Inside(original, old)) throw new InvalidDataException("Backup DB references a path outside its original server root.");
            q.CommandText = "UPDATE releases SET directory=$path WHERE rowid=$id"; q.Parameters.Clear();
            q.Parameters.AddWithValue("$path", SafePath.ResolveInsideChecked(target, Path.GetRelativePath(original, old)));
            q.Parameters.AddWithValue("$id", row.Id); q.ExecuteNonQuery();
        }
        tx.Commit();
    }
    internal static SqliteConnection Open(string root, bool readOnly)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "distribution.db"), Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); db.Open(); return db;
    }
    public static async Task<object> StageAsync(string backup, string target, DistributionSettings current)
    {
        var manifest = await VerifyAsync(backup); target = Path.GetFullPath(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Restore target must be new/empty.");
        if (Inside(current.Root, target) || Inside(target, current.Root)) throw new IOException("Restore target must not overlap the live root.");
        if (manifest.Origin != current.PublicUrl.TrimEnd('/') || manifest.SigningKeyId != current.SigningKeyId || manifest.SigningPublicKeySha256 != Signer(current))
            throw new InvalidDataException("Restore origin or signing identity differs.");
        MaintenanceStorage.PrivateDirectory(target);
        using var targetLease=DistributionMaintenanceLease.Acquire(target,true);
        if(Directory.EnumerateFileSystemEntries(target).Any(path=>Path.GetFileName(path)!=".maintenance.lock"))throw new IOException("Restore target changed before reservation.");
        // Fence is persisted before copying any authority or payload. Interrupted staging cannot be served.
        await JsonFiles.WriteAsync(Path.Combine(target, "restore-staged.json"), manifest);
        var content = Path.Combine(backup, "content");
        foreach (var file in manifest.Files)
        { var path = SafePath.ResolveInsideChecked(target, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(SafePath.ResolveInsideChecked(content, file.Path), path, false); }
        foreach(var file in manifest.Files)
            if(!await Hashing.Sha256MatchesAsync(SafePath.ResolveInsideChecked(target,file.Path),file.Sha256))throw new InvalidDataException("Restored staging changed during copy.");
        PrepareAuthorityDirectories(target);
        foreach (var name in new[] { "incoming", "processing", "releases", "archive", ".job-locks" })
            Directory.CreateDirectory(SafePath.ResolveInsideChecked(target, name));
        ValidateAudit(ReadValidatedAuthority(target));
        return new { Staged = true, Public = false, Target = target, Next = "restore activate --target ... --confirm with surviving current source; private key remains separate" };
    }
    public static async Task<object> ActivateAsync(string target, DistributionSettings current, bool confirm)
    {
        if (!confirm) throw new ArgumentException("Restore activation requires --confirm.");
        target=Path.GetFullPath(target);var original=Path.GetFullPath(current.Root);
        if(Inside(original,target)||Inside(target,original))throw new InvalidDataException("Restore source and target overlap.");
        _ = AuthorityStore.InspectExisting(original);
        _ = AuthorityStore.InspectExisting(target);
        var ordered=new[]{original,target}.Order(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).ToArray();
        using var firstLease=DistributionMaintenanceLease.Acquire(ordered[0],true,create:false);
        using var secondLease=DistributionMaintenanceLease.Acquire(ordered[1],true,create:false);
        var authority = ReadValidatedAuthority(original);
        _ = ReadValidatedAuthority(target);
        using var oldServer = new AuthenticationProcessLease(current.Root);
        if(File.Exists(Path.Combine(current.Root,"restore-staged.json")))throw new InvalidDataException("Staged source is not current authority.");
        var fence = Path.Combine(target, "restore-staged.json");
        var manifest = await JsonFiles.ReadAsync<DistributionBackupManifest>(fence);
        if (Path.GetFullPath(target) == Path.GetFullPath(current.Root)) throw new InvalidDataException("Cannot activate over source root.");
        if (manifest.Origin != current.PublicUrl.TrimEnd('/') || manifest.SigningKeyId != current.SigningKeyId || manifest.SigningPublicKeySha256 != Signer(current)) throw new InvalidDataException("Current origin/signer changed.");
        var intake = IntakeJobs.Read(authority);
        var deletionPaths = intake.MaintenanceDeletions.Select(item => item.Path).ToHashSet(IntakeJobs.PathComparer);
        var catalog = new CatalogAuthority(authority.Store, original).Snapshot(authority);
        async Task MatchDirectory(string directory)
        {
            var relative=Path.GetRelativePath(original,Path.GetFullPath(directory));var destination=SafePath.ResolveInsideChecked(target,relative);
            if(!Directory.Exists(directory))
            {
                if(!deletionPaths.Contains(relative.Replace('\\','/')))throw new InvalidDataException("Authority references an unrecorded missing directory.");
                if(Directory.Exists(destination))throw new InvalidDataException("Staged backup would resurrect deliberately deleted content; stage a current backup.");
                return;
            }
            if(!Directory.Exists(destination))throw new InvalidDataException("Latest required directory is absent in restore.");
            var originals=Files(directory);var restored=Files(destination);
            var names=originals.Select(path=>Path.GetRelativePath(directory,path).Replace('\\','/')).ToHashSet(StringComparer.Ordinal);
            if(!names.SetEquals(restored.Select(path=>Path.GetRelativePath(destination,path).Replace('\\','/'))))throw new InvalidDataException("Restore has extra or missing files.");
            foreach(var file in originals)
            {var counterpart=SafePath.ResolveInsideChecked(destination,Path.GetRelativePath(directory,file));if(new FileInfo(file).Length!=new FileInfo(counterpart).Length || await Hashing.Sha256FileAsync(file)!=await Hashing.Sha256FileAsync(counterpart))throw new InvalidDataException("Restored file changed.");}
        }
        var required = intake.Jobs.SelectMany(job => new[] { job.Source, job.Snapshot })
            .Concat(intake.ActiveWork.SelectMany(work => new[] { work.Scratch, work.Snapshot }))
            .OfType<string>().Distinct(IntakeJobs.PathComparer);
        foreach (var path in required) await MatchDirectory(IntakeJobs.Resolve(original, path));
        // Check the entire public tree in both directions, then independently verify each signed release.
        await MatchDirectory(SafePath.ResolveInsideChecked(original, "releases"));
        foreach (var release in catalog.Releases)
        {
            var restored = SafePath.ResolveInsideChecked(target, Path.GetRelativePath(original, release.Directory));
            await VerifyPublishedReleaseAsync(restored, release.ReleaseId, release.Metadata, current);
        }

        Boundary.Value?.Invoke("restored-releases-verified");
        Boundary.Value?.Invoke("authority-snapshot");
        var policy = await new FileAccessPolicyProvider(current.PolicyPath).LoadAsync(CancellationToken.None);
        _ = CompiledAccessPolicy.Create(policy);
        await JsonFiles.WriteAsync(Path.Combine(target, "restored-policy.json"), policy);
        Boundary.Value?.Invoke("policy-written");
        CopyAuthority(authority, target, activating: true);
        Boundary.Value?.Invoke("restored-authority-verified");
        Boundary.Value?.Invoke("before-public-fence");
        File.Delete(fence); // Final public fence transition; failures before this stay blocked.
        AuthorityDisk.FlushDirectory(target);
        return new { Activated = true, Target = target, PolicyPath = Path.Combine(target, "restored-policy.json"), PrivateKeyIncluded = false };
    }
}
