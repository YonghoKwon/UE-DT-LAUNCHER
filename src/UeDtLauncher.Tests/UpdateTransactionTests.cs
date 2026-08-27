using Xunit;

namespace UeDtLauncher.Tests;

public class UpdateTransactionTests
{
    [Fact]
    public async Task Recover_PreparedTransactionDiscardsBackupWithoutTouchingLiveFiles()
    {
        using var temp = new TempDirectory();
        var config = Config(temp.Path);
        Directory.CreateDirectory(config.InstallDir);
        Directory.CreateDirectory(config.BackupDir);
        var livePath = Path.Combine(config.InstallDir, "game.exe");
        await File.WriteAllTextAsync(livePath, "live");
        var backupRoot = BackupManager.CreateBackupRoot(config.BackupDir);
        await File.WriteAllTextAsync(Path.Combine(backupRoot, "partial.tmp"), "partial");
        _ = await UpdateTransactionManager.BeginAsync(
            config,
            backupRoot,
            "1.0.0",
            "2.0.0",
            ["new.dll"],
            CancellationToken.None);

        await UpdateTransactionManager.RecoverIfNeededAsync(config);

        Assert.Equal("live", await File.ReadAllTextAsync(livePath));
        Assert.False(Directory.Exists(backupRoot));
        Assert.False(File.Exists(UpdateTransactionManager.JournalPath(config)));
    }

    [Fact]
    public async Task Recover_ApplyingTransactionRestoresBackupsAndRemovesAddedFiles()
    {
        using var temp = new TempDirectory();
        var config = Config(temp.Path);
        Directory.CreateDirectory(config.InstallDir);
        Directory.CreateDirectory(config.BackupDir);
        var livePath = Path.Combine(config.InstallDir, "game.exe");
        var addedPath = Path.Combine(config.InstallDir, "new.dll");
        await File.WriteAllTextAsync(livePath, "old-binary");
        await JsonFiles.WriteAsync(config.InstalledManifestPath, new LauncherManifest
        {
            Version = "1.0.0",
            EntryPoint = "game.exe",
            Files = [new ManifestFile { Path = "game.exe", Sha256 = new string('a', 64), Size = 10 }]
        });
        await JsonFiles.WriteAsync(config.InstallStatePath, new InstallState { Version = "1.0.0" });

        var backupRoot = BackupManager.CreateBackupRoot(config.BackupDir);
        var transaction = await UpdateTransactionManager.BeginAsync(
            config,
            backupRoot,
            "1.0.0",
            "2.0.0",
            ["new.dll"],
            CancellationToken.None);
        await BackupManager.WriteBackupMetadataAsync(
            backupRoot,
            new BackupInfo
            {
                PreviousVersion = "1.0.0",
                NewVersion = "2.0.0",
                AddedPaths = ["new.dll"]
            },
            config.InstalledManifestPath,
            config.InstallStatePath);
        File.Copy(livePath, Path.Combine(backupRoot, "game.exe"));
        await transaction.MarkApplyingAsync(CancellationToken.None);

        await File.WriteAllTextAsync(livePath, "new-binary");
        await File.WriteAllTextAsync(addedPath, "new-file");
        await JsonFiles.WriteAsync(config.InstallStatePath, new InstallState { Version = "2.0.0" });

        await UpdateTransactionManager.RecoverIfNeededAsync(config);

        Assert.Equal("old-binary", await File.ReadAllTextAsync(livePath));
        Assert.False(File.Exists(addedPath));
        Assert.Equal("1.0.0", (await JsonFiles.ReadAsync<InstallState>(config.InstallStatePath)).Version);
        Assert.False(File.Exists(UpdateTransactionManager.JournalPath(config)));
    }

    [Fact]
    public async Task Commit_KeepsBackupAndRemovesJournal()
    {
        using var temp = new TempDirectory();
        var config = Config(temp.Path);
        Directory.CreateDirectory(config.BackupDir);
        var backupRoot = BackupManager.CreateBackupRoot(config.BackupDir);
        var transaction = await UpdateTransactionManager.BeginAsync(
            config,
            backupRoot,
            "1.0.0",
            "2.0.0",
            Array.Empty<string>(),
            CancellationToken.None);
        await transaction.MarkApplyingAsync(CancellationToken.None);

        await transaction.CommitAsync(CancellationToken.None);

        Assert.True(Directory.Exists(backupRoot));
        Assert.False(File.Exists(UpdateTransactionManager.JournalPath(config)));
    }

    [Fact]
    public void CreateBackupRoot_AllocatesUniqueNamesWithinTheSameSecond()
    {
        using var temp = new TempDirectory();
        var first = BackupManager.CreateBackupRoot(temp.Path);
        var second = BackupManager.CreateBackupRoot(temp.Path);

        Assert.NotEqual(first, second);
        Assert.Contains(BackupManager.List(temp.Path), item => item.BackupRoot == first);
        Assert.Contains(BackupManager.List(temp.Path), item => item.BackupRoot == second);
    }

    private static LauncherConfig Config(string root)
    {
        var state = Path.Combine(root, "state");
        return new LauncherConfig
        {
            ProjectId = "project-a",
            InstallDir = Path.Combine(root, "install"),
            StagingDir = Path.Combine(state, "staging"),
            BackupDir = Path.Combine(state, "backups"),
            InstalledManifestPath = Path.Combine(state, "installed-manifest.json"),
            InstallStatePath = Path.Combine(state, "install-state.json"),
            AppPidPath = Path.Combine(state, "app.pid")
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-transaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
