using System.Text.Json.Nodes;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed partial class RuntimeDataTests
{
    [Fact]
    public void OverlappingDataRejectsLeaseBeforeCreatingStateOrLock()
    {
        var c = Config(); c.RuntimeData!.RootDirectory = c.BackupDir;
        var before = Directory.GetFileSystemEntries(root);
        Assert.Throws<RuntimeDataException>(() => InstallationMutationLease.Acquire(c));
        Assert.Equal(before, Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void DataPlanIsFrozenWithAttemptAndInstallation()
    {
        var c = Config(); var original = RuntimeTestSupport.Active(c, RuntimeState.LaunchPending);
        original.RuntimeData = RuntimeDataPolicy.Plan(c, original.Requester!, original.AttemptId!);
        RuntimeStore.Write(c, original);
        Assert.Equal(RuntimeState.LaunchPending, RuntimeStore.Observe(c).State);
        original.RuntimeData = original.RuntimeData! with { InstallationId = new string('a', 64) };
        RuntimeStore.Write(c, original);
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(c).State);
    }

    [Theory]
    [InlineData("adapter")][InlineData("policy")][InlineData("releaseId")]
    [InlineData("attemptId")][InlineData("owner")][InlineData("protectedDirectories")][InlineData("installationId")]
    public void IncompleteOptionalDataPlanFailsClosed(string field)
    {
        var c = Config(); var r = RuntimeTestSupport.Active(c, RuntimeState.Running);
        r.RuntimeData = RuntimeDataPolicy.Plan(c, r.Requester!, r.AttemptId!); RuntimeStore.Write(c, r);
        var json = JsonNode.Parse(File.ReadAllText(RuntimeStore.RecordPath(c)))!;
        json["runtimeData"]!.AsObject().Remove(field); File.WriteAllText(RuntimeStore.RecordPath(c), json.ToJsonString());
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(c).State);
    }

    [Theory]
    [InlineData(RuntimeState.Running)][InlineData(RuntimeState.LaunchPending)][InlineData(RuntimeState.Unknown)]
    public void BlockedMaintenanceDoesNotChangeExternalData(RuntimeState state)
    {
        var c = Config(); var plan = Plan(c);
        var launch = RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], plan), RuntimeIdentities.Current());
        var data = Path.Combine(RuntimeDataPolicy.Resolve(plan).UserDirectory, "save.json"); File.WriteAllText(data, "latest user data");
        RuntimeStore.Write(c, RuntimeTestSupport.Active(c, state));
        Assert.Throws<RuntimeBlockedException>(() => InstallationMutationLease.Acquire(c));
        Assert.Equal("latest user data", File.ReadAllText(data));
    }

    [Theory]
    [InlineData(UpdateTransactionStatus.Prepared)][InlineData(UpdateTransactionStatus.Applying)]
    public async Task PayloadRollbackRecoveryAndPrunePreserveLatestUserData(UpdateTransactionStatus status)
    {
        var c = Config(); var plan = Plan(c);
        RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], plan), RuntimeIdentities.Current());
        var data = Path.Combine(RuntimeDataPolicy.Resolve(plan).UserDirectory, "save.json"); File.WriteAllText(data, "latest save");
        Directory.CreateDirectory(c.InstallDir); Directory.CreateDirectory(c.BackupDir);
        var backup = BackupManager.CreateBackupRoot(c.BackupDir);
        File.WriteAllText(Path.Combine(c.InstallDir, "game.dat"), "updated payload");
        File.WriteAllText(Path.Combine(backup, "game.dat"), "old normal payload");
        RuntimeTestSupport.Stopped(c);
        await BackupManager.WriteBackupMetadataAsync(backup, new() { PreviousVersion = "v1", NewVersion = "v2" }, c.InstalledManifestPath, c.InstallStatePath);
        await JsonFiles.WriteAsync(UpdateTransactionManager.JournalPath(c), new UpdateTransactionJournal { Status = status, BackupName = Path.GetFileName(backup) });
        await UpdateTransactionManager.RecoverIfNeededAsync(c);
        if (status == UpdateTransactionStatus.Applying) Assert.Equal("old normal payload", File.ReadAllText(Path.Combine(c.InstallDir, "game.dat")));
        else Assert.Equal("updated payload", File.ReadAllText(Path.Combine(c.InstallDir, "game.dat")));
        Assert.Equal("latest save", File.ReadAllText(data));
        BackupManager.Prune(c.BackupDir, 0); Assert.Equal("latest save", File.ReadAllText(data));
    }

    [Fact]
    public void ReadOnlyPermissionInspectionCreatesNothing()
    {
        var c = Config(); var before = Directory.GetFileSystemEntries(root);
        RuntimeDataPolicy.InspectUserRootReadOnly(c);
        Assert.Equal(before, Directory.GetFileSystemEntries(root));
    }
}
