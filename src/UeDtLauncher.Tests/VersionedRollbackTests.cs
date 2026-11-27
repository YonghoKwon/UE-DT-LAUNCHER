using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
        await MakeBackupAsync(requested, Latest, "different newer backup");
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
    [InlineData("extra-file")]
    [InlineData("extra-deletion")]
    [InlineData("missing-deletion-property")]
    public async Task DamagedBackupIsRejectedBeforeDeletingOrReplacingLiveFiles(string damage)
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        if (damage == "payload")
            await File.WriteAllTextAsync(Path.Combine(backup, "app.bin"), new string('x', Restored.Length));
        else if (damage == "metadata")
            File.Delete(Path.Combine(backup, BackupManager.MetaDirName, "installed-manifest.json"));
        else if (damage == "extra-file")
            await File.WriteAllTextAsync(Path.Combine(backup, "unrelated.bin"), "unexpected payload");
        else
        {
            var infoPath = Path.Combine(backup, BackupManager.MetaDirName, "backup-info.json");
            var info = JsonNode.Parse(await File.ReadAllTextAsync(infoPath))!.AsObject();
            if (damage == "missing-deletion-property")
                info.Remove("addedPaths");
            else
            {
                await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "unrelated.bin"), "keep this");
                info["addedPaths"]!.AsArray().Add("unrelated.bin");
            }
            await File.WriteAllTextAsync(infoPath, info.ToJsonString());
        }
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

    [Fact]
    public async Task RecordedPackageFilesCanBeRestoredWithoutPermittingUnrecordedPaths()
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        var infoPath = Path.Combine(backup, BackupManager.MetaDirName, "backup-info.json");
        var info = await JsonFiles.ReadAsync<BackupInfo>(infoPath);
        info.AdditionalBackupHashes = new() { ["package.bin"] = Hash("prior-package") };
        info.PackageAddedPaths = ["introduced.bin"];
        info.AddedPaths.Add("introduced.bin");
        await JsonFiles.WriteAsync(infoPath, info);
        await BackupManager.WriteRollbackInventoryAsync(Path.GetDirectoryName(infoPath)!);
        await File.WriteAllTextAsync(Path.Combine(backup, "package.bin"), "prior-package");
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "package.bin"), "current-package");
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "introduced.bin"), "added package file");

        Assert.True(await BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal("prior-package", await File.ReadAllTextAsync(Path.Combine(config.InstallDir, "package.bin")));
        Assert.False(File.Exists(Path.Combine(config.InstallDir, "introduced.bin")));
    }

    [Fact]
    public async Task PartialFirstInstallDeletionListCannotClearLiveManifestOrState()
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        var metadata = Path.Combine(backup, BackupManager.MetaDirName);
        File.Delete(Path.Combine(metadata, "installed-manifest.json"));
        File.Delete(Path.Combine(metadata, "install-state.json"));
        File.Delete(Path.Combine(backup, "app.bin"));
        await JsonFiles.WriteAsync(Path.Combine(metadata, "backup-info.json"),
            new BackupInfo { NewVersion = "1.0.0", AddedPaths = ["app.bin"] });
        var before = Snapshot();

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ChangedVersionedInventoryDigestBlocksRestore()
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        var metadata = Path.Combine(backup, BackupManager.MetaDirName);
        await BackupManager.WriteRollbackInventoryAsync(metadata);
        await File.AppendAllTextAsync(Path.Combine(metadata, "backup-info.json"), " ");
        var before = Snapshot();

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task FirstInstallRollbackPreservesMatchingPreexistingFile()
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        var metadata = Path.Combine(backup, BackupManager.MetaDirName);
        File.Delete(Path.Combine(metadata, "installed-manifest.json"));
        File.Delete(Path.Combine(metadata, "install-state.json"));
        File.Delete(Path.Combine(backup, "app.bin"));
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "app.bin"), Restored);
        await JsonFiles.WriteAsync(Path.Combine(metadata, "backup-info.json"),
            new BackupInfo { NewVersion = "1.0.0", AddedPaths = ["added.bin"], UnchangedPreexistingPaths = ["app.bin"] });
        await BackupManager.WriteRollbackInventoryAsync(metadata);

        Assert.True(await BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(Restored, await File.ReadAllTextAsync(Path.Combine(config.InstallDir, "app.bin")));
        Assert.False(File.Exists(Path.Combine(config.InstallDir, "added.bin")));
        Assert.False(File.Exists(config.InstalledManifestPath));
        Assert.False(File.Exists(config.InstallStatePath));
    }

    [Fact]
    public async Task MissingSavedStateCannotDeleteLiveInstallationState()
    {
        var config = Bind("1.0.0");
        var backup = await MakeBackupAsync(config, Latest);
        var metadata = Path.Combine(backup, BackupManager.MetaDirName);
        await BackupManager.WriteRollbackInventoryAsync(metadata);
        File.Delete(Path.Combine(metadata, "install-state.json"));
        var before = Snapshot();

        await Assert.ThrowsAsync<InvalidDataException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task InterruptedInstallationBlocksListingAndRestore()
    {
        var config = Bind("1.0.0");
        await MakeBackupAsync(config, Latest);
        await File.WriteAllTextAsync(UpdateTransactionManager.JournalPath(config), "{}");
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() => BackupManager.ListForRollback(config));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupManager.RestoreSelectionAsync(config, null));
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void DirectoryLimitCountsEvenInvalidBackupNames()
    {
        var config = Bind("1.0.0");
        for (var index = 0; index <= 4096; index++)
            Directory.CreateDirectory(Path.Combine(config.BackupDir, $"invalid-{index}"));
        Assert.Throws<InvalidDataException>(() => BackupManager.ListForRollback(config));
    }

    [Fact]
    public void BackupListAndSelectedRestoreHaveDistinctProtocolRequests()
    {
        var request = new ManagedAgentRequest { Command = "rollback-list" };
        Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.RollbackTarget = new("prod", "stable", "windows-x64", "exact", "1.0.0");
        Assert.Null(ManagedAgentProtocol.Validate(request));
        request.Command = "rollback";
        Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.RollbackTarget = null;
        request.RollbackBackupId = Latest;
        Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.Command = "rollback-backup";
        Assert.Null(ManagedAgentProtocol.Validate(request));
        Assert.True(ManagedAgentProtocol.RequiresRuntimeCapability(request.Command));
        request.RollbackBackupId = "../other";
        Assert.NotNull(ManagedAgentProtocol.Validate(request));
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

    private static async Task<string> MakeBackupAsync(LauncherConfig config, string id, string saved = Restored)
    {
        var backup = Path.Combine(config.BackupDir, id);
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(config.InstallDir);
        await File.WriteAllTextAsync(Path.Combine(backup, "app.bin"), saved);
        var manifest = new LauncherManifest
        {
            AppId = "demo", Version = config.SelectedRelease!.Version, Platform = config.TargetPlatform, EntryPoint = "app.bin",
            Files = [new() { Path = "app.bin", Size = saved.Length, Sha256 = Hash(saved) }]
        };
        await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
        await JsonFiles.WriteAsync(config.InstallStatePath, new InstallState { Version = manifest.Version });
        await BackupManager.WriteBackupMetadataAsync(backup, new BackupInfo
        {
            PreviousVersion = manifest.Version, NewVersion = manifest.Version, AddedPaths = ["added.bin"]
        }, config.InstalledManifestPath, config.InstallStatePath);
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "app.bin"), Current);
        await File.WriteAllTextAsync(Path.Combine(config.InstallDir, "added.bin"), "added");
        // This manifest belongs to the post-update state, where the update introduced added.bin.
        manifest.Files.Add(new() { Path = "added.bin", Size = 5, Sha256 = Hash("added") });
        await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
        RuntimeTestSupport.Stopped(config);
        return backup;
    }

    private static string Hash(string contents) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents)));

    private string[] Snapshot() => Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "update.lock")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{path}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}").ToArray();

    public void Dispose() => root.Delete(recursive: true);
}
