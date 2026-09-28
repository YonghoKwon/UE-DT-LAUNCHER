using Xunit;
namespace UeDtLauncher.Tests;

public sealed class RuntimeRaceTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PendingWriteOwnsMutationLock(bool rollback)
    {
        var root=Path.Combine(Path.GetTempPath(),"runtime-race-"+Guid.NewGuid().ToString("N"));
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var config=new LauncherConfig { ProjectId="demo",StateRootDir=Path.Combine(root,"state"),InstallDir=Path.Combine(root,"app"),InstallStatePath=Path.Combine(root,"state","install.json"),InstalledManifestPath=Path.Combine(root,"state","manifest.json") };
        Task? launch=null;
        try
        {
            Directory.CreateDirectory(config.InstallDir); File.WriteAllText(Path.Combine(config.InstallDir,"game"),"payload");
            Directory.CreateDirectory(Path.Combine(root,"backup"));
            RuntimeTestSupport.Stopped(config);
            await JsonFiles.WriteAsync(config.InstalledManifestPath,new LauncherManifest { AppId="demo",Version="1",Platform=config.TargetPlatform,EntryPoint="game",Files=[new() {Path="game",Size=7,Sha256=new string('a',64)}] });
            launch=Task.Run(()=>
            {
                RuntimeStatePersistence.Boundary.Value=(p,b)=> { if(p==RuntimeStore.RecordPath(config) && b=="replace") { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
                try { RuntimeStore.Begin(config,RuntimeIdentities.Current(),Environment.ProcessPath!); }
                finally { RuntimeStatePersistence.Boundary.Value=null; }
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if(rollback) await Assert.ThrowsAsync<InvalidOperationException>(()=>BackupManager.RestoreAsync(Path.Combine(root,"backup"),config.InstallDir,config.InstalledManifestPath,config.InstallStatePath));
            else Assert.Throws<InvalidOperationException>(()=>InstallationMutationLease.Acquire(config));
            Assert.Equal("payload",File.ReadAllText(Path.Combine(config.InstallDir,"game")));
            release.Set(); await launch;
            Assert.Equal(RuntimeState.LaunchPending,RuntimeStore.Observe(config).State);
            Assert.Throws<RuntimeBlockedException>(()=>InstallationMutationLease.Acquire(config));
        }
        finally { release.Set(); if(launch is not null) await launch; if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
