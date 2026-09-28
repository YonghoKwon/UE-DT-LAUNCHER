using Xunit;

namespace UeDtLauncher.Tests;

public sealed class RuntimeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uedt-runtime-store-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherConfig config;
    public RuntimeStoreTests()
    {
        config = new() { InstallDir=Path.Combine(root,"app"), InstallStatePath=Path.Combine(root,"state","install.json"), InstalledManifestPath=Path.Combine(root,"state","manifest.json"), AppPidPath=Path.Combine(root,"state","app.pid"), ProjectId="demo" };
    }
    [Fact]
    public void FreshInstallIsQuiescent_LegacyIsUnknown_AndLeasePreservesLockInode()
    {
        Assert.Equal(RuntimeState.Quiescent, RuntimeStore.Observe(config).State);
        using (InstallationMutationLease.Acquire(config)) { }
        Assert.True(File.Exists(LauncherPaths.UpdateLockPath(config)));
        Assert.Equal(RuntimeState.Quiescent, RuntimeStore.Observe(config).State);
        File.Delete(RuntimeStore.RecordPath(config));
        File.WriteAllText(config.AppPidPath, "{}");
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(config).State);
        Assert.Throws<RuntimeBlockedException>(() => InstallationMutationLease.Acquire(config));
    }
    [Fact]
    public async Task TicketAndPeerAreBound_AndRunningBlocksMutationUntilCompletion()
    {
        using (InstallationMutationLease.Acquire(config)) { }
        Directory.CreateDirectory(config.InstallDir); File.WriteAllText(Path.Combine(config.InstallDir,"game.exe"), "fixture");
        await JsonFiles.WriteAsync(config.InstalledManifestPath, new LauncherManifest { AppId="demo", Version="1", Platform=config.TargetPlatform, EntryPoint="game.exe", Files=[new() { Path="game.exe",Size=7,Sha256=new string('a',64) }] });
        var identity = RuntimeIdentities.Current();
        var ticket = RuntimeStore.Begin(config, identity, identity.Executable);
        Assert.Equal(RuntimeState.LaunchPending, RuntimeStore.Observe(config).State);
        Assert.Throws<RuntimeBlockedException>(() => InstallationMutationLease.Acquire(config));
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeStore.Attach(config, ticket with { Token=new string('0',64) }, identity, identity.Executable));
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeStore.Attach(config, ticket, identity with { Owner="wrong" }, identity.Executable));
        _ = RuntimeStore.Attach(config, ticket, identity, identity.Executable);
        Assert.Throws<InvalidOperationException>(() => RuntimeStore.Report(config,ticket,identity,true));
        RuntimeStore.Report(config,ticket,identity,false,123);
        Assert.Equal(RuntimeState.Running, RuntimeStore.Observe(config).State);
        Assert.Throws<RuntimeBlockedException>(() => InstallationMutationLease.Acquire(config));
        RuntimeStore.Report(config,ticket,identity,true);
        RuntimeStore.Report(config,ticket,identity,true); // same completion ACK replay is harmless
        using var lease = InstallationMutationLease.Acquire(config);
    }
    [Fact]
    public void MissingOrReusedHostNeverMeansStopped()
    {
        using (InstallationMutationLease.Acquire(config)) { }
        var identity = RuntimeIdentities.Current() with { CreationId="not-this-process" };
        RuntimeStore.Write(config,new() { InstallationId=RuntimeStore.InstallationId(config),State=RuntimeState.Running,Host=identity });
        Assert.Equal(RuntimeState.Unknown,RuntimeStore.Observe(config).State);
        Assert.Throws<RuntimeBlockedException>(()=>InstallationMutationLease.Acquire(config));
        var record=RuntimeStore.Read(config)!; record.PayloadIdentity=RuntimeIdentities.Current(); RuntimeStore.Write(config,record);
        Assert.Throws<RuntimeBlockedException>(()=>RuntimeStore.Recover(config,RuntimeIdentities.Current(),true));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
