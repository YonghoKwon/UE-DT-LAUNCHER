using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class VersionedRollbackTests : IDisposable
{
    private const string Older = "20261127010000";
    private const string Latest = "20261127010100";
    private const string Restored = "original release payload";
    private const string Current = "updated release payload";
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("uedt-versioned-rollback-");

    [Fact]
    public async Task SelectedReleaseListsItsBackupsAndRestoresAnExplicitId()
    {
        var requested = Bind("1.0.0");
        var other = Bind("2.0.0");
        await MakeBackupAsync(requested, Older);
        await MakeBackupAsync(requested, Latest);
        var untouched = await MakeBackupAsync(other, "20261128010000");
        var preview = await RollbackPreviewService.ReadAsync(requested);

        Assert.Equal(Latest, preview!.BackupId);
        Assert.Equal(new[] { Latest, Older }, BackupManager.ListForRollback(requested).Select(backup => backup.Id));
        Assert.True(await BackupManager.RestoreSelectionAsync(requested, Older));
        Assert.Equal(Restored, await File.ReadAllTextAsync(Path.Combine(requested.InstallDir, "app.bin")));
        Assert.False(File.Exists(Path.Combine(requested.InstallDir, "added.bin")));
        Assert.Equal(Current, await File.ReadAllTextAsync(Path.Combine(other.InstallDir, "app.bin")));
        Assert.True(File.Exists(Path.Combine(untouched, "app.bin")));
    }

    [Fact]
    public async Task MissingBackupLeavesTheSelectedInstallationUntouched()
    {
        var config = Bind("1.0.0");
        var other = Bind("2.0.0");
        await MakeBackupAsync(other, Latest);
        var before = Snapshot();

        Assert.Empty(BackupManager.ListForRollback(config));
        Assert.False(await BackupManager.RestoreSelectionAsync(config, null));
        Assert.False(await BackupManager.RestoreSelectionAsync(config, Latest));
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("metadata")]
    public async Task DamagedBackupIsRejectedBeforeDeletingOrReplacingLiveFiles(string damage)
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        if (damage == "payload")
            await File.WriteAllTextAsync(Path.Combine(backup, "app.bin"), new string('x', Restored.Length));
        else
            File.Delete(Path.Combine(backup, BackupManager.MetaDirName, "installed-manifest.json"));
        var before = Snapshot();

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task RunningReleaseRejectsRollbackWithoutChangingItsFiles()
    {
        var config = Bind("1.0.0");
        await MakeBackupAsync(config, Latest);
        RuntimeStore.Write(config, RuntimeTestSupport.Active(config, RuntimeState.Running));
        var before = Snapshot();

        await Assert.ThrowsAsync<RuntimeBlockedException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    private LauncherConfig Bind(string version)
    {
        var config = new LauncherConfig
        {
            ProjectId = "demo",
            VersionedInstallRoot = Path.Combine(root.FullName, "app"),
            StateRootDir = Path.Combine(root.FullName, "state")
        };
        VersionedReleasePaths.Bind(config, new("demo", "prod", "stable", config.TargetPlatform, version));
        return config;
    }

    private static async Task<string> MakeBackupAsync(LauncherConfig config, string id)
    {
        var backup = Path.Combine(config.BackupDir, id);
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(config.InstallDir);
        await File.WriteAllTextAsync(Path.Combine(backup, "app.bin"), Restored);
        var manifest = new LauncherManifest
        {
            AppId = "demo", Version = config.SelectedRelease!.Version, Platform = config.TargetPlatform, EntryPoint = "app.bin",
            Files = [new() { Path = "app.bin", Size = Restored.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Restored))) }]
        };
        await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
        await JsonFiles.WriteAsync(config.InstallStatePath, new InstallState { Version = manifest.Version });
        await BackupManager.WriteBackupMetadataAsync(backup, new BackupInfo
        {
            PreviousVersion = manifest.Version, NewVersion = manifest.Version, AddedPaths = ["added.bin"]
        }, config.InstalledManifestPath, config.InstallStatePath);
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "app.bin"), Current);
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "added.bin"), "added");
        RuntimeTestSupport.Stopped(config);
        return backup;
    }

    private string[] Snapshot() => Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "update.lock")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{path}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();

    public void Dispose() => root.Delete(recursive: true);
}
