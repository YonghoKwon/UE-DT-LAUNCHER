using System.Security.Cryptography;
using System.Text.Json;

namespace UeDtLauncher;

public sealed class BackupInfo
{
    public string? PreviousVersion { get; set; }
    public string? NewVersion { get; set; }
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    // Paths that the update introduced; rollback deletes them so the install matches the previous file set.
    public List<string> AddedPaths { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? AdditionalBackupHashes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PackageAddedPaths { get; set; }
}
public sealed record BackupPruneResult(int RemovedCount,int FailedCount,int RemainingCount,IReadOnlyList<string> FailedBackupIds);
public sealed record BackupListEntry(string Id, string? PreviousVersion, string? NewVersion, string? CreatedAtUtc);
public sealed record BackupTarget(string Environment, string Channel, string Platform, string VersionPolicy, string? RequestedVersion);

public static class BackupManager
{
    public const string MetaDirName = ".uedt-meta";
    private const string InfoFileName = "backup-info.json";
    private const string ManifestFileName = "installed-manifest.json";
    private const string StateFileName = "install-state.json";
    private const string InventoryDigestFileName = "rollback-inventory.sha256";
    private const int MaxBackupMetadataBytes = 64 * 1024 * 1024;

    public static string CreateBackupRoot(string backupDir)
    {
        var prefix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var name = suffix == 0 ? prefix : $"{prefix}-{suffix:00}";
            var root = Path.Combine(backupDir, name);
            try
            {
                if (Directory.Exists(root)) continue;
                Directory.CreateDirectory(root);
                return root;
            }
            catch (IOException) when (suffix < 99)
            {
            }
        }

        throw new IOException($"Could not allocate a unique backup directory in: {backupDir}");
    }

    public static async Task WriteBackupMetadataAsync(string backupRoot, BackupInfo info, string installedManifestPath,
        string installStatePath, CancellationToken cancellationToken = default, bool includeInventoryDigest = false)
    {
        var metaDir = Path.Combine(backupRoot, MetaDirName);
        Directory.CreateDirectory(metaDir);
        var infoPath = Path.Combine(metaDir, InfoFileName);
        await JsonFiles.WriteAsync(infoPath, info, cancellationToken);
        if (includeInventoryDigest)
            await File.WriteAllTextAsync(Path.Combine(metaDir, InventoryDigestFileName),
                await Hashing.Sha256FileAsync(infoPath, cancellationToken), cancellationToken);
        if (File.Exists(installedManifestPath)) File.Copy(installedManifestPath, Path.Combine(metaDir, ManifestFileName), overwrite: true);
        if (File.Exists(installStatePath)) File.Copy(installStatePath, Path.Combine(metaDir, StateFileName), overwrite: true);
    }

    public static IReadOnlyList<(string BackupRoot, BackupInfo? Info)> List(string backupDir)
        => BackupDirectories(backupDir).Select(directory => (directory, ReadBackupInfo<BackupInfo>(directory))).ToArray();

    private static IEnumerable<string> BackupDirectories(string backupDir, int? maximumDirectories = null)
    {
        if (!Directory.Exists(backupDir)) return [];
        var directories = Directory.EnumerateDirectories(backupDir);
        if (maximumDirectories is { } maximum)
        {
            var bounded = directories.Take(maximum + 1).ToArray();
            if (bounded.Length > maximum) throw new InvalidDataException("Too many entries in the backup directory.");
            directories = bounded;
        }
        return directories.Where(directory =>
        {
            var name = Path.GetFileName(directory);
            return (name.Length == 14 || name.Length == 17 && name[14] == '-' && name[15..].All(char.IsAsciiDigit))
                && name[..14].All(char.IsAsciiDigit);
        }).OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
    }

    private static T? ReadBackupInfo<T>(string directory, bool bounded = false) where T : class
    {
        var path = Path.Combine(directory, MetaDirName, InfoFileName);
        if (!File.Exists(path)) return null;
        try
        {
            if (bounded && new FileInfo(path).Length > MaxBackupMetadataBytes) return null;
            return JsonFiles.ReadAsync<T>(path).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Preserve the legacy listing of directories with unreadable metadata.
            return null;
        }
    }

    public static void Prune(string backupDir, int keepCount, Action<string>? log = null)
        =>PruneEntries(backupDir,keepCount,log,path=>Directory.Delete(path,true));

    public static BackupPruneResult PruneWithResult(string backupDir,int keepCount,Action<string>? log=null)
        =>PruneWithResult(backupDir,keepCount,log,path=>Directory.Delete(path,true));

    internal static BackupPruneResult PruneWithResult(string backupDir,int keepCount,Action<string>? log,Action<string> delete)
    {
        var counts=PruneEntries(backupDir,keepCount,log,delete);
        return new(counts.Removed,counts.Failed.Count,List(backupDir).Count,counts.Failed);
    }
    private static (int Removed,List<string> Failed) PruneEntries(string backupDir,int keepCount,Action<string>? log,Action<string> delete)
    {
        var backups = List(backupDir);
        var removed=0;var failed=new List<string>();
        foreach (var (backupRoot, _) in backups.Skip(Math.Max(0, keepCount)))
        {
            try
            {
                delete(backupRoot);
                removed++;
                log?.Invoke($"Removed old backup: {Path.GetFileName(backupRoot)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(Path.GetFileName(backupRoot));
                log?.Invoke($"Could not remove old backup {Path.GetFileName(backupRoot)}: {ex.Message}");
            }
        }
        return (removed,failed);
    }

    /// <summary>
    /// Restores the installation to the state captured in <paramref name="backupRoot"/>:
    /// deletes files the corresponding update added, copies backed-up files back, and restores
    /// the installed manifest and install state files.
    /// </summary>
    public static async Task RestoreAsync(string backupRoot, string installDir, string installedManifestPath, string installStatePath, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(backupRoot)) throw new DirectoryNotFoundException($"Backup directory does not exist: {backupRoot}");
        var config = new LauncherConfig { InstallDir=installDir, InstalledManifestPath=installedManifestPath, InstallStatePath=installStatePath, AppPidPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installStatePath))!,"app.pid") };
        using var lease = InstallationMutationLease.Acquire(config);
        await RestoreUnderLeaseAsync(backupRoot, config, lease, log, cancellationToken);
    }

    public static async Task RestoreAsync(string backupRoot, LauncherConfig config, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        using var lease = InstallationMutationLease.Acquire(config);
        await RestoreUnderLeaseAsync(backupRoot, config, lease, log, cancellationToken);
    }

    internal static IReadOnlyList<BackupListEntry> ListForRollback(LauncherConfig config) =>
        RollbackDirectories(config).Select(directory =>
        {
            var info = ReadBackupInfo<BackupListEntry>(directory, bounded: true);
            return new BackupListEntry(Path.GetFileName(directory), BoundedDisplay(info?.PreviousVersion),
                BoundedDisplay(info?.NewVersion), BoundedDisplay(info?.CreatedAtUtc));
        }).ToArray();

    private static string? BoundedDisplay(string? value) => value is { Length: <= 128 } && !value.Any(char.IsControl) ? value : null;

    private static IEnumerable<string> RollbackDirectories(LauncherConfig config)
    {
        if (File.Exists(UpdateTransactionManager.JournalPath(config)))
            throw new InvalidOperationException("Backup is changing; check again after the installation operation.");
        return BackupDirectories(config.BackupDir, 4096);
    }

    internal static async Task<bool> RestoreSelectionAsync(LauncherConfig config, string? backupId,
        Action<string>? log = null, CancellationToken cancellationToken = default, string? expectedFingerprint = null)
    {
        if (expectedFingerprint is null && !Directory.Exists(config.BackupDir)) return false;
        using var lease = InstallationMutationLease.AcquireMaintenance(config);
        if (expectedFingerprint is not null)
        {
            var preview = await RollbackPreviewService.ReadAsync(config, cancellationToken);
            if (preview is null || !preview.CanRestore || preview.BackupId != backupId ||
                preview.MetadataFingerprint != expectedFingerprint)
                throw new RollbackPreviewChangedException();
        }

        var selected = RollbackDirectories(config).FirstOrDefault(directory =>
            backupId is null || string.Equals(Path.GetFileName(directory), backupId, StringComparison.Ordinal));
        if (selected is null)
        {
            if (expectedFingerprint is not null) throw new RollbackPreviewChangedException();
            return false;
        }
        var root = SafePath.ResolveInsideChecked(config.BackupDir, Path.GetFileName(selected));
        if (config.SelectedRelease is { } release)
        {
            var plan = await BuildReleaseRestorePlanAsync(root, config, release, cancellationToken);
            lease.Validate(config);
            ApplyReleaseRestore(plan, config, cancellationToken);
            log?.Invoke($"Restored backup {Path.GetFileName(root)}.");
        }
        else await RestoreUnderLeaseAsync(root, config, lease, log, cancellationToken);
        return true;
    }

    private sealed record ReleaseRestorePlan(IReadOnlyList<string> Deletes, IReadOnlyDictionary<string, string> Copies,
        string Manifest, string State);

    private static async Task<BackupInfo> ReadReleaseBackupInfoAsync(string root, CancellationToken token)
    {
        var path = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{InfoFileName}");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxBackupMetadataBytes)
            throw new InvalidDataException("Backup metadata is missing or too large.");
        var encoded = await File.ReadAllBytesAsync(path, token);
        var digestPath = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{InventoryDigestFileName}");
        var digestFile = new FileInfo(digestPath);
        if (digestFile.Exists && (digestFile.Length != 64 ||
            !string.Equals(await File.ReadAllTextAsync(digestPath, token),
                Convert.ToHexString(SHA256.HashData(encoded)), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Backup inventory digest differs from its metadata.");

        var payload = encoded.AsMemory();
        if (payload.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) payload = payload[3..];
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Backup metadata must be an object.");
        var entries = document.RootElement.EnumerateObject().Where(property =>
            property.Name.Equals(nameof(BackupInfo.AddedPaths), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Backup metadata must contain its complete deletion list.");
        var info = document.RootElement.Deserialize<BackupInfo>(JsonFiles.Options)
            ?? throw new InvalidDataException("Backup metadata is missing.");
        if (!digestFile.Exists && (info.AdditionalBackupHashes is not null || info.PackageAddedPaths is not null))
            throw new InvalidDataException("Backup payload inventory is missing its digest.");
        return info;
    }

    private static async Task<ReleaseRestorePlan> BuildReleaseRestorePlanAsync(string root,
        LauncherConfig config, ReleaseSelection release, CancellationToken token)
    {
        var info = await ReadReleaseBackupInfoAsync(root, token);
        if (info.NewVersion != release.Version)
            throw new InvalidDataException("Backup metadata is missing, invalid, or belongs to another release.");

        var manifestPath = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{ManifestFileName}");
        var statePath = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{StateFileName}");
        if (File.Exists(statePath))
        {
            var state = await JsonFiles.ReadAsync<InstallState>(statePath, token);
            if (state.Version != release.Version || (state.Channel is not null && state.Channel != release.Channel) ||
                (state.Environment is not null && state.Environment != release.Environment) ||
                (state.Platform is not null && state.Platform != release.Platform))
                throw new InvalidDataException("Backup installation state belongs to another release.");
        }
        if (info.PackageAddedPaths is null && File.Exists(config.InstallStatePath))
        {
            var liveState = await JsonFiles.ReadAsync<InstallState>(config.InstallStatePath, token);
            if (liveState.AppliedPackageHashes.Count > 0)
                throw new InvalidDataException("Legacy backup has no verifiable package deletion inventory.");
        }

        LauncherManifest? previous = null;
        if (File.Exists(manifestPath))
        {
            if (info.PreviousVersion != release.Version)
                throw new InvalidDataException("Backup version differs from its release.");
            previous = await ReadReleaseManifestAsync(manifestPath, release, token);
        }
        else if (info.PreviousVersion is not null || File.Exists(statePath) || info.AddedPaths.Count == 0)
            throw new InvalidDataException("Backup manifest is missing.");

        var before = (previous?.Files ?? []).ToDictionary(file => RestorePath(config.InstallDir, file.Path),
            SafePath.FileSystemComparer);
        var additionalBackups = (info.AdditionalBackupHashes ?? []).ToDictionary(
            entry => RestorePath(config.InstallDir, entry.Key), entry => entry.Value, SafePath.FileSystemComparer);
        var packageAdded = (info.PackageAddedPaths ?? []).Select(path => RestorePath(config.InstallDir, path))
            .ToHashSet(SafePath.FileSystemComparer);

        var deletes = new HashSet<string>(SafePath.FileSystemComparer);
        foreach (var relative in info.AddedPaths)
        {
            var path = RestorePath(config.InstallDir, relative);
            if (!deletes.Add(path) || Directory.Exists(path))
                throw new InvalidDataException("Backup contains an unexpected deletion.");
        }

        var copies = ReadBackupCopies(root, config.InstallDir, token);
        if (File.Exists(config.InstalledManifestPath))
        {
            var current = await ReadReleaseManifestAsync(config.InstalledManifestPath, release, token);
            packageAdded.UnionWith(current.Files.Select(file => RestorePath(config.InstallDir, file.Path))
                .Where(path => !before.ContainsKey(path) && !copies.ContainsKey(path)));
        }
        if (!deletes.SetEquals(packageAdded))
            throw new InvalidDataException("Backup deletion list is incomplete or contains unexpected paths.");
        foreach (var (target, source) in copies)
        {
            if (before.ContainsKey(target)) continue;
            if (!additionalBackups.TryGetValue(target, out var hash) || !await Hashing.Sha256MatchesAsync(source, hash, token))
                throw new InvalidDataException("Backup contains unexpected or damaged payload data.");
        }
        if (additionalBackups.Keys.Any(path => !copies.ContainsKey(path)))
            throw new InvalidDataException("Backup payload inventory is incomplete.");
        foreach (var (target, file) in before)
        {
            if (!copies.TryGetValue(target, out var source))
            {
                if (deletes.Contains(target))
                    throw new InvalidDataException("Backup would delete a required installation file.");
                source = target;
            }
            var details = new FileInfo(source);
            if (!details.Exists || details.Length != file.Size || !await Hashing.Sha256MatchesAsync(source, file.Sha256, token))
                throw new InvalidDataException("Backup payload does not match the previous installation manifest.");
        }
        return new(deletes.ToArray(), copies, manifestPath, statePath);
    }

    private static async Task<LauncherManifest> ReadReleaseManifestAsync(string path, ReleaseSelection release, CancellationToken token)
    {
        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(path, token);
        LauncherEngine.ValidateManifest(manifest);
        if (manifest.Version != release.Version || manifest.AppId != release.ProjectId ||
            manifest.Channel != release.Channel || manifest.Platform != release.Platform)
            throw new InvalidDataException("Backup manifest belongs to another release.");
        return manifest;
    }

    private static string RestorePath(string root, string relative)
    {
        var path = SafePath.ResolveInsideChecked(root, ReleaseSidecar.Relative(relative));
        var top = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0];
        if (top == "." || SafePath.FileSystemComparer.Equals(top, MetaDirName))
            throw new InvalidDataException("Backup payload path is reserved.");
        return path;
    }

    private static Dictionary<string, string> ReadBackupCopies(string root, string installDir, CancellationToken token)
    {
        var copies = new Dictionary<string, string>(SafePath.FileSystemComparer);
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 500000 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Backup exceeds its inventory limit or contains a linked entry.");
                var relative = Path.GetRelativePath(root, entry);
                if (SafePath.FileSystemComparer.Equals(relative, MetaDirName)) continue;
                var target = RestorePath(installDir, relative);
                if (Directory.Exists(entry))
                {
                    if (File.Exists(target)) throw new InvalidDataException("Backup directory conflicts with an installed file.");
                    pending.Push(entry);
                }
                else if (Directory.Exists(target) || !copies.TryAdd(target, entry))
                    throw new InvalidDataException("Duplicate or conflicting backup destination.");
            }
        }
        return copies;
    }

    private static void ApplyReleaseRestore(ReleaseRestorePlan plan, LauncherConfig config, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var path in plan.Deletes)
        {
            File.Delete(path);
        }
        foreach (var (target, source) in plan.Copies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        RestoreMetaFile(plan.Manifest, config.InstalledManifestPath);
        RestoreMetaFile(plan.State, config.InstallStatePath);
    }

    internal static async Task RestoreUnderLeaseAsync(string backupRoot, LauncherConfig config, InstallationMutationLease lease, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        lease.Validate(config);
        var installDir=config.InstallDir; var installedManifestPath=config.InstalledManifestPath; var installStatePath=config.InstallStatePath;
        if (!Directory.Exists(backupRoot)) throw new DirectoryNotFoundException($"Backup directory does not exist: {backupRoot}");

        var metaDir = Path.Combine(backupRoot, MetaDirName);
        var infoPath = Path.Combine(metaDir, InfoFileName);
        BackupInfo? info = null;
        if (File.Exists(infoPath)) info = await JsonFiles.ReadAsync<BackupInfo>(infoPath, cancellationToken);

        foreach (var addedPath in info?.AddedPaths ?? new List<string>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installedPath = SafePath.ResolveInside(installDir, addedPath);
            if (File.Exists(installedPath))
            {
                File.Delete(installedPath);
                log?.Invoke($"Removed file added by update: {addedPath}");
            }
        }

        foreach (var backupFile in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(backupRoot, backupFile);
            if (relative.StartsWith(MetaDirName + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var target = SafePath.ResolveInside(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backupFile, target, overwrite: true);
        }

        RestoreMetaFile(Path.Combine(metaDir, ManifestFileName), installedManifestPath);
        RestoreMetaFile(Path.Combine(metaDir, StateFileName), installStatePath);
        log?.Invoke($"Restored backup {Path.GetFileName(backupRoot)} (previous version: {info?.PreviousVersion ?? "unknown"}).");
    }

    private static void RestoreMetaFile(string backupCopy, string livePath)
    {
        if (File.Exists(backupCopy))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(livePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(backupCopy, livePath, overwrite: true);
        }
        else if (File.Exists(livePath))
        {
            // The pre-update state had no manifest/state file (fresh install); drop the current one
            // so the next run performs a full repair scan instead of trusting stale metadata.
            File.Delete(livePath);
        }
    }
}
