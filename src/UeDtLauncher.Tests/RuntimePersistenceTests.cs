using Xunit;
namespace UeDtLauncher.Tests;

public sealed class RuntimePersistenceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"runtime-persistence-"+Guid.NewGuid().ToString("N"));
    private LauncherConfig Config(string version="1")
    {
        var c=new LauncherConfig { ProjectId="demo",StateRootDir=Path.Combine(root,"state"), VersionedInstallRoot=Path.Combine(root,"apps") };
        VersionedReleasePaths.Bind(c,new("demo",c.Environment,c.Channel,c.TargetPlatform,version));
        Directory.CreateDirectory(c.InstallDir); File.WriteAllText(Path.Combine(c.InstallDir,"game.exe"),"fixture");
        RuntimeTestSupport.Stopped(c);
        JsonFiles.WriteAsync(c.InstalledManifestPath,new LauncherManifest { AppId="demo",Version=version,Platform=c.TargetPlatform,EntryPoint="game.exe",Files=[new() { Path="game.exe",Size=7,Sha256=new string('a',64) }] }).GetAwaiter().GetResult();
        return c;
    }
    public static IEnumerable<object[]> Failures => from operation in new[] {"begin","attach","started","complete","recover"}
        from boundary in new[] {"write","flush","replace","ack"} select new object[] {operation,boundary};
    [Theory][MemberData(nameof(Failures))]
    public void RuntimeWritesFailClosed(string operation,string boundary)
    {
        var c=Config(); var actor=RuntimeIdentities.Current(); RuntimeLaunchTicket? ticket=null;
        if(operation is "attach" or "started" or "complete") ticket=RuntimeStore.Begin(c,actor,actor.Executable);
        if(operation is "started" or "complete") RuntimeStore.Attach(c,ticket!,actor,actor.Executable);
        if(operation=="complete") RuntimeStore.Report(c,ticket!,actor,false);
        var path=RuntimeStore.RecordPath(c); var before=File.ReadAllBytes(path);
        RuntimeStatePersistence.Boundary.Value=(p,b)=> { if(p==path && b==boundary) throw new IOException("injected"); };
        Assert.Throws<IOException>(()=>
        {
            switch(operation)
            {
                case "begin": RuntimeStore.Begin(c,actor,actor.Executable); break;
                case "attach": RuntimeStore.Attach(c,ticket!,actor,actor.Executable); break;
                case "started": RuntimeStore.Report(c,ticket!,actor,false); break;
                case "complete": RuntimeStore.Report(c,ticket!,actor,true); break;
                default: RuntimeStore.Recover(c,actor,true); break;
            }
        });
        RuntimeStatePersistence.Boundary.Value=null;
        if(boundary!="ack") Assert.Equal(before,File.ReadAllBytes(path));
        var observed=RuntimeStore.Observe(c).State;
        if(operation is "attach" or "started" || operation=="begin" && boundary=="ack" || operation=="complete" && boundary!="ack")
            Assert.NotEqual(RuntimeState.Quiescent,observed);
        if(operation=="complete" && boundary=="ack") RuntimeStore.Report(c,ticket!,actor,true);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.tmp"));
    }
    [Theory][InlineData("write")][InlineData("flush")][InlineData("replace")][InlineData("ack")]
    public void HealthCommitFailureLeavesBarrier(string boundary)
    {
        var c=Config(); RuntimeServiceState.RequireSelection(c,"1"); RuntimeServiceState.Starting(c,"backup");
        var path=RuntimeServiceState.SnapshotPath(c); var before=File.ReadAllBytes(path);
        RuntimeStatePersistence.Boundary.Value=(p,b)=> { if(p==path && b==boundary) throw new IOException(); };
        Assert.Throws<IOException>(()=>RuntimeServiceState.Healthy(c));
        RuntimeStatePersistence.Boundary.Value=null;
        if(boundary!="ack") { Assert.Equal(before,File.ReadAllBytes(path)); Assert.Throws<RuntimeBlockedException>(()=>RuntimeServiceState.RequireSelection(c,"1")); }
    }
    [Fact]
    public void PreviousRunningVersionBlocksSelectionAndNewLaunch()
    {
        var first=Config(); RuntimeServiceState.RequireSelection(first,"1"); var next=Config("2");
        RuntimeStore.Write(first,RuntimeTestSupport.Active(first,RuntimeState.Running));
        var before=File.ReadAllBytes(RuntimeServiceState.SnapshotPath(first));
        Assert.Throws<RuntimeBlockedException>(()=>RuntimeServiceState.ConfirmSelection(next,RuntimeIdentities.Current(),"2"));
        Assert.Equal(before,File.ReadAllBytes(RuntimeServiceState.SnapshotPath(first)));
        RuntimeTestSupport.Stopped(first);
        RuntimeServiceState.ConfirmSelection(next,RuntimeIdentities.Current(),"2");
        Assert.Throws<RuntimeBlockedException>(()=>RuntimeStore.Begin(first,RuntimeIdentities.Current(),Environment.ProcessPath!));
        Assert.Equal("2",RuntimeServiceState.Read(next)!.Version);
    }
    [Fact]
    public void SelectionFinalWriteFailureKeepsReviewBarrier()
    {
        var c=Config(); RuntimeServiceState.RequireSelection(c,"1"); RuntimeServiceState.Starting(c,"backup");
        var writes=0;
        RuntimeStatePersistence.Boundary.Value=(p,b)=> { if(p==RuntimeServiceState.SnapshotPath(c) && b=="replace" && ++writes==2) throw new IOException(); };
        Assert.Throws<IOException>(()=>RuntimeServiceState.ConfirmSelection(c,RuntimeIdentities.Current(),"1"));
        RuntimeStatePersistence.Boundary.Value=null;
        Assert.Equal(ServiceRuntimePhase.NeedsReview,RuntimeServiceState.Read(c)!.Phase);
        Assert.Throws<RuntimeBlockedException>(()=>RuntimeStore.Begin(c,RuntimeIdentities.Current(),Environment.ProcessPath!));
    }
    public void Dispose() { RuntimeStatePersistence.Boundary.Value=null; if(Directory.Exists(root)) Directory.Delete(root,true); }
}
