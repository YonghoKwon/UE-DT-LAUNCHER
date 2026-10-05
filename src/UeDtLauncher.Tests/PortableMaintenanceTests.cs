using Xunit;
namespace UeDtLauncher.Tests;

public class PortableMaintenanceTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void HeldInstallationLeaseRejectsCleanupWithoutTouchingFiles(bool backups)
    {
        using var f=new Fixture();var before=f.Snapshot();
        using(var held=InstallationMutationLease.Acquire(f.Config))
            Assert.Throws<InvalidOperationException>(()=>f.Clean(backups));
        Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData(RuntimeState.Running)][InlineData(RuntimeState.LaunchPending)][InlineData(RuntimeState.Unknown)]
    public void UncertainRuntimeRejectsCleanupAndPreservesPayload(RuntimeState state)
    {
        using var f=new Fixture();
        File.WriteAllText(RuntimeStore.RecordPath(f.Config),"{\"schemaVersion\":1,\"state\":"+(int)state+"}");
        var before=f.Snapshot();Assert.Throws<RuntimeBlockedException>(()=>f.Clean(false));Assert.Equal(before,f.Snapshot());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void PendingJournalBlocksCleanupWithoutRecoveryOrRecordChanges(bool backups)
    {
        using var f=new Fixture();File.WriteAllText(UpdateTransactionManager.JournalPath(f.Config),"incomplete-journal");
        var before=f.Snapshot();Assert.Throws<InvalidOperationException>(()=>f.Clean(backups));Assert.Equal(before,f.Snapshot());
    }
    [Fact]
    public void StagingCleanupPreservesResumeCacheAndBackupRetentionUsesConfiguredCount()
    {
        using var f=new Fixture();f.Clean(false);
        Assert.False(Directory.Exists(f.Config.StagingDir));Assert.Equal("resume",File.ReadAllText(f.Resume));
        f.Clean(true);Assert.Single(BackupManager.List(f.Config.BackupDir));Assert.Equal("old",File.ReadAllText(f.Payload));
    }
    [Fact]
    public void MissingFolderIsNotCreatedByOpenPreparation()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-no-open-create-"+Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(()=>PortableMaintenance.ExistingFolder(Path.Combine(root,"app")));
        Assert.False(Directory.Exists(root));
    }
    private sealed class Fixture:IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"uedt-maintenance-"+Guid.NewGuid().ToString("N"));
        public LauncherConfig Config;public string Payload;public string Resume;
        public Fixture()
        {
            Directory.CreateDirectory(Root);Config=new(){ProjectId="demo",InstallDir=Path.Combine(Root,"apps"),StateRootDir=Path.Combine(Root,"state"),MaxBackupCount=1};
            LauncherPaths.ResolveInPlace(Config,Path.Combine(Root,"config.json"));
            Directory.CreateDirectory(Config.InstallDir);RuntimeTestSupport.Stopped(Config);
            Payload=Path.Combine(Config.InstallDir,"payload");File.WriteAllText(Payload,"old");
            Directory.CreateDirectory(Config.StagingDir);File.WriteAllText(Path.Combine(Config.StagingDir,"download.partial"),"partial");
            var cache=Path.Combine(Path.GetDirectoryName(Config.InstallStatePath)!,"resume-cache");Directory.CreateDirectory(cache);Resume=Path.Combine(cache,"verified");File.WriteAllText(Resume,"resume");
            Directory.CreateDirectory(Path.Combine(Config.BackupDir,"20261005000000"));File.WriteAllText(Path.Combine(Config.BackupDir,"20261005000000","data"),"oldbackup");
            Directory.CreateDirectory(Path.Combine(Config.BackupDir,"20261005000001"));File.WriteAllText(Path.Combine(Config.BackupDir,"20261005000001","data"),"newbackup");
        }
        public void Clean(bool backups){if(backups)PortableMaintenance.PruneBackups(Config);else PortableMaintenance.ClearStaging(Config);}
        public string[] Snapshot()=>Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories).Where(p=>!p.EndsWith("update.lock",StringComparison.Ordinal)).Select(p=>Path.GetRelativePath(Root,p)+":"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).Order(StringComparer.Ordinal).ToArray();
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
