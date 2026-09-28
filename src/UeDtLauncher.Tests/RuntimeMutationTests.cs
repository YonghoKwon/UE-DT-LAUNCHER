using System.Security.Cryptography;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class RuntimeMutationTests
{
    [Theory]
    [InlineData(RuntimeState.Running)]
    [InlineData(RuntimeState.LaunchPending)]
    [InlineData(RuntimeState.Unknown)]
    public async Task BlockedStatesPreserveInstallMetadataBackupAndJournal(RuntimeState state)
    {
        var root = Path.Combine(Path.GetTempPath(), "uedt-runtime-guard-" + Guid.NewGuid().ToString("N"));
        var config = new LauncherConfig { ProjectId="demo", InstallDir=Path.Combine(root,"app"), StagingDir=Path.Combine(root,"staging"), BackupDir=Path.Combine(root,"backups"),
            InstallStatePath=Path.Combine(root,"state","install-state.json"), InstalledManifestPath=Path.Combine(root,"state","installed-manifest.json"), AppPidPath=Path.Combine(root,"state","app.pid"), LaunchAfterUpdate=false };
        try
        {
            Directory.CreateDirectory(config.InstallDir); Directory.CreateDirectory(Path.Combine(config.BackupDir,"saved"));
            await File.WriteAllTextAsync(Path.Combine(config.InstallDir,"game.exe"), "unchanged");
            await File.WriteAllTextAsync(Path.Combine(config.BackupDir,"saved","game.exe"), "backup");
            await JsonFiles.WriteAsync(config.InstallStatePath,new InstallState { Version="1" });
            await JsonFiles.WriteAsync(config.InstalledManifestPath,new LauncherManifest { Version="1" });
            await JsonFiles.WriteAsync(UpdateTransactionManager.JournalPath(config),new UpdateTransactionJournal { Status=UpdateTransactionStatus.Applying,BackupName="saved" });
            RuntimeStore.Write(config,new() { InstallationId=RuntimeStore.InstallationId(config),State=state,Host=RuntimeIdentities.Current() });
            var before = Snapshot(root);
            using var engine = new LauncherEngine(config, null, null, echoToConsole:false);
            await Assert.ThrowsAsync<RuntimeBlockedException>(() => engine.PrepareAsync());
            await Assert.ThrowsAsync<RuntimeBlockedException>(() => BackupManager.RestoreAsync(Path.Combine(config.BackupDir,"saved"),config.InstallDir,config.InstalledManifestPath,config.InstallStatePath));
            await Assert.ThrowsAsync<RuntimeBlockedException>(() => UpdateTransactionManager.RecoverIfNeededAsync(config));
            Assert.Equal(before,Snapshot(root));
            Assert.False(Directory.Exists(config.StagingDir));
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    [Fact]
    public void ServiceCannotSilentlyChangeAnActiveVersion()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-service-selection-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config=new LauncherConfig { ProjectId="demo",InstallDir=Path.Combine(root,"app"),StateRootDir=Path.Combine(root,"state"),InstallStatePath=Path.Combine(root,"state","install.json") };
            RuntimeServiceState.RequireSelection(config,"1.0.0");
            Assert.Throws<RuntimeBlockedException>(()=>RuntimeServiceState.RequireSelection(config,"2.0.0"));
            RuntimeServiceState.RecordFailure(config,null);
            Assert.Throws<RuntimeBlockedException>(()=>RuntimeServiceState.RequireSelection(config,"1.0.0"));
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .Where(p=>!p.EndsWith("update.lock",StringComparison.Ordinal)).OrderBy(p=>p,StringComparer.Ordinal)
        .Select(p=>Path.GetRelativePath(root,p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
}
