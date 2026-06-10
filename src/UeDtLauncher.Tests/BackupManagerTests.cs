using Xunit;

namespace UeDtLauncher.Tests;

public class BackupManagerTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _backupDir;
    private readonly string _installDir;

    public BackupManagerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "uedt-backup-tests-" + Guid.NewGuid().ToString("N"));
        _backupDir = Path.Combine(_workDir, ".backup");
        _installDir = Path.Combine(_workDir, "app");
        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(_installDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    private string CreateTimestampedBackup(string name)
    {
        var root = Path.Combine(_backupDir, name);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void List_ReturnsNewestFirst_IgnoringForeignDirectories()
    {
        CreateTimestampedBackup("20240101000000");
        CreateTimestampedBackup("20250101000000");
        Directory.CreateDirectory(Path.Combine(_backupDir, "self-update"));

        var backups = BackupManager.List(_backupDir);

        Assert.Equal(2, backups.Count);
        Assert.Equal("20250101000000", Path.GetFileName(backups[0].BackupRoot));
        Assert.Equal("20240101000000", Path.GetFileName(backups[1].BackupRoot));
    }

    [Fact]
    public void Prune_KeepsNewestN()
    {
        CreateTimestampedBackup("20240101000000");
        CreateTimestampedBackup("20240201000000");
        CreateTimestampedBackup("20240301000000");

        BackupManager.Prune(_backupDir, keepCount: 2);

        var remaining = BackupManager.List(_backupDir).Select(b => Path.GetFileName(b.BackupRoot)).ToList();
        Assert.Equal(new[] { "20240301000000", "20240201000000" }, remaining);
    }

    [Fact]
    public async Task Restore_RoundTrips_ChangedAndAddedFiles()
    {
        var manifestPath = Path.Combine(_workDir, "installed-manifest.json");
        var statePath = Path.Combine(_workDir, "install-state.json");

        // Previous installation: one file at version 1.0.0.
        await File.WriteAllTextAsync(Path.Combine(_installDir, "game.exe"), "v1");
        await JsonFiles.WriteAsync(manifestPath, new LauncherManifest { Version = "1.0.0", EntryPoint = "game.exe" });
        await JsonFiles.WriteAsync(statePath, new InstallState { Version = "1.0.0" });

        // Simulate an update that overwrites game.exe and adds newfile.dat.
        var backupRoot = BackupManager.CreateBackupRoot(_backupDir);
        await BackupManager.WriteBackupMetadataAsync(backupRoot, new BackupInfo
        {
            PreviousVersion = "1.0.0",
            NewVersion = "2.0.0",
            AddedPaths = new List<string> { "newfile.dat" }
        }, manifestPath, statePath);
        File.Copy(Path.Combine(_installDir, "game.exe"), Path.Combine(backupRoot, "game.exe"));

        await File.WriteAllTextAsync(Path.Combine(_installDir, "game.exe"), "v2");
        await File.WriteAllTextAsync(Path.Combine(_installDir, "newfile.dat"), "added");
        await JsonFiles.WriteAsync(manifestPath, new LauncherManifest { Version = "2.0.0", EntryPoint = "game.exe" });
        await JsonFiles.WriteAsync(statePath, new InstallState { Version = "2.0.0" });

        await BackupManager.RestoreAsync(backupRoot, _installDir, manifestPath, statePath);

        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(_installDir, "game.exe")));
        Assert.False(File.Exists(Path.Combine(_installDir, "newfile.dat")));
        Assert.Equal("1.0.0", (await JsonFiles.ReadAsync<LauncherManifest>(manifestPath)).Version);
        Assert.Equal("1.0.0", (await JsonFiles.ReadAsync<InstallState>(statePath)).Version);
    }

    [Fact]
    public async Task Restore_FreshInstallBackup_RemovesManifestAndState()
    {
        var manifestPath = Path.Combine(_workDir, "installed-manifest.json");
        var statePath = Path.Combine(_workDir, "install-state.json");

        // Fresh install: no previous manifest/state existed when the backup was taken.
        var backupRoot = BackupManager.CreateBackupRoot(_backupDir);
        await BackupManager.WriteBackupMetadataAsync(backupRoot, new BackupInfo
        {
            NewVersion = "1.0.0",
            AddedPaths = new List<string> { "game.exe" }
        }, manifestPath, statePath);

        await File.WriteAllTextAsync(Path.Combine(_installDir, "game.exe"), "v1");
        await JsonFiles.WriteAsync(manifestPath, new LauncherManifest { Version = "1.0.0", EntryPoint = "game.exe" });
        await JsonFiles.WriteAsync(statePath, new InstallState { Version = "1.0.0" });

        await BackupManager.RestoreAsync(backupRoot, _installDir, manifestPath, statePath);

        Assert.False(File.Exists(Path.Combine(_installDir, "game.exe")));
        Assert.False(File.Exists(manifestPath));
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public async Task Restore_MissingBackup_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            BackupManager.RestoreAsync(Path.Combine(_backupDir, "19990101000000"), _installDir, "m.json", "s.json"));
    }
}
