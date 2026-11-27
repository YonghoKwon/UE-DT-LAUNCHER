namespace UeDtLauncher;

public sealed class BackupInfo
{
    public string? PreviousVersion { get; set; }
    public string? NewVersion { get; set; }
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    // Paths that the update introduced; rollback deletes them so the install matches the previous file set.
    public List<string> AddedPaths { get; set; } = new();
}
public sealed record BackupPruneResult(int RemovedCount,int FailedCount,int RemainingCount,IReadOnlyList<string> FailedBackupIds);
public sealed record BackupListEntry(string Id, string? PreviousVersion, string? NewVersion, string? CreatedAtUtc);

public static class BackupManager
{
    public const string MetaDirName = ".uedt-meta";
    private const string InfoFileName = "backup-info.json";
    private const string ManifestFileName = "installed-manifest.json";
    private const string StateFileName = "install-state.json";

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

    public static async Task WriteBackupMetadataAsync(string backupRoot, BackupInfo info, string installedManifestPath, string installStatePath, CancellationToken cancellationToken = default)
    {
        var metaDir = Path.Combine(backupRoot, MetaDirName);
        Directory.CreateDirectory(metaDir);
        await JsonFiles.WriteAsync(Path.Combine(metaDir, InfoFileName), info, cancellationToken);
        if (File.Exists(installedManifestPath)) File.Copy(installedManifestPath, Path.Combine(metaDir, ManifestFileName), overwrite: true);
        if (File.Exists(installStatePath)) File.Copy(installStatePath, Path.Combine(metaDir, StateFileName), overwrite: true);
    }

    public static IReadOnlyList<(string BackupRoot, BackupInfo? Info)> List(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return Array.Empty<(string, BackupInfo?)>();

        var results = new List<(string, BackupInfo?)>();
        foreach (var directory in Directory.EnumerateDirectories(backupDir).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var timestamp = name.Length >= 14 ? name[..14] : string.Empty;
            var suffixValid = name.Length == 14
                              || (name.Length == 17
                                  && name[14] == '-'
                                  && name[15..].All(char.IsAsciiDigit));
            if (!suffixValid || !timestamp.All(char.IsAsciiDigit)) continue;

            BackupInfo? info = null;
            var infoPath = Path.Combine(directory, MetaDirName, InfoFileName);
            if (File.Exists(infoPath))
            {
                try
                {
                    info = JsonFiles.ReadAsync<BackupInfo>(infoPath).GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Unreadable metadata still allows a file-level restore.
                }
            }

            results.Add((directory, info));
        }

        return results;
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
        ReadRollbackEntries(config).Select(backup => new BackupListEntry(Path.GetFileName(backup.BackupRoot),
            backup.Info?.PreviousVersion, backup.Info?.NewVersion, backup.Info?.CreatedAtUtc)).ToArray();

    private static IReadOnlyList<(string BackupRoot, BackupInfo? Info)> ReadRollbackEntries(LauncherConfig config)
    {
        if (File.Exists(UpdateTransactionManager.JournalPath(config)))
            throw new InvalidOperationException("Backup is changing; check again after the installation operation.");
        return List(config.BackupDir);
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

        var selected = ReadRollbackEntries(config).FirstOrDefault(backup =>
            backupId is null || string.Equals(Path.GetFileName(backup.BackupRoot), backupId, StringComparison.Ordinal));
        if (selected.BackupRoot is null)
        {
            if (expectedFingerprint is not null) throw new RollbackPreviewChangedException();
            return false;
        }
        var root = SafePath.ResolveInsideChecked(config.BackupDir, Path.GetFileName(selected.BackupRoot));
        if (config.SelectedRelease is { } release)
            await ValidateReleaseBackupAsync(root, selected.Info, config, release, cancellationToken);
        await RestoreUnderLeaseAsync(root, config, lease, log, cancellationToken);
        return true;
    }

    private static async Task ValidateReleaseBackupAsync(string root, BackupInfo? info, LauncherConfig config,
        ReleaseSelection release, CancellationToken token)
    {
        if (info is null || info.AddedPaths is null || info.NewVersion != release.Version)
            throw new InvalidDataException("Backup metadata is missing, invalid, or belongs to another release.");
        var manifestPath = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{ManifestFileName}");
        var statePath = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{StateFileName}");
        _ = SafePath.ResolveInsideChecked(root, $"{MetaDirName}/{InfoFileName}");
        var removed = info.AddedPaths.Select(path => SafePath.ResolveInsideChecked(config.InstallDir, path))
            .ToHashSet(SafePath.FileSystemComparer);

        // Scan without descending through reparse points. Check restore destinations before any mutation.
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Backup contains a linked entry.");
                var relative = Path.GetRelativePath(root, entry);
                if (SafePath.FileSystemComparer.Equals(relative, MetaDirName)) continue;
                _ = SafePath.ResolveInsideChecked(config.InstallDir, relative);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }

        if (File.Exists(statePath))
        {
            var state = await JsonFiles.ReadAsync<InstallState>(statePath, token);
            if (state.Version != release.Version || (state.Channel is not null && state.Channel != release.Channel) ||
                (state.Environment is not null && state.Environment != release.Environment) ||
                (state.Platform is not null && state.Platform != release.Platform))
                throw new InvalidDataException("Backup installation state belongs to another release.");
        }
        if (!File.Exists(manifestPath))
        {
            if (info.PreviousVersion is null && !File.Exists(statePath) && removed.Count > 0) return;
            throw new InvalidDataException("Backup manifest is missing.");
        }
        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(manifestPath, token);
        LauncherEngine.ValidateManifest(manifest);
        if (info.PreviousVersion != release.Version || manifest.Version != release.Version ||
            manifest.AppId != release.ProjectId || manifest.Channel != release.Channel || manifest.Platform != release.Platform)
            throw new InvalidDataException("Backup manifest belongs to another release.");
        foreach (var file in manifest.Files)
        {
            var installed = SafePath.ResolveInsideChecked(config.InstallDir, file.Path);
            var saved = SafePath.ResolveInsideChecked(root, file.Path);
            if (!File.Exists(saved) && removed.Contains(installed))
                throw new InvalidDataException("Backup would delete a required installation file.");
            var source = File.Exists(saved) ? saved : installed;
            var details = new FileInfo(source);
            if (!details.Exists || details.Length != file.Size || !await Hashing.Sha256MatchesAsync(source, file.Sha256, token))
                throw new InvalidDataException("Backup payload does not match the previous installation manifest.");
        }
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
