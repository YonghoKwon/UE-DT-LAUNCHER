namespace UeDtLauncher;

public sealed class BackupInfo
{
    public string? PreviousVersion { get; set; }
    public string? NewVersion { get; set; }
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    // Paths that the update introduced; rollback deletes them so the install matches the previous file set.
    public List<string> AddedPaths { get; set; } = new();
}

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
    {
        var backups = List(backupDir);
        foreach (var (backupRoot, _) in backups.Skip(Math.Max(0, keepCount)))
        {
            try
            {
                Directory.Delete(backupRoot, recursive: true);
                log?.Invoke($"Removed old backup: {Path.GetFileName(backupRoot)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Could not remove old backup {Path.GetFileName(backupRoot)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Restores the installation to the state captured in <paramref name="backupRoot"/>:
    /// deletes files the corresponding update added, copies backed-up files back, and restores
    /// the installed manifest and install state files.
    /// </summary>
    public static async Task RestoreAsync(string backupRoot, string installDir, string installedManifestPath, string installStatePath, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
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
