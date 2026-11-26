using System.ComponentModel;
using System.Text.Json;
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

    [Fact]
    public void CompletionPublishesWhileRunningSnapshotIsOpen()
    {
        var config = Config();
        var actor = RuntimeIdentities.Current();
        var ticket = RuntimeStore.Begin(config, actor, actor.Executable);
        RuntimeStore.Attach(config, ticket, actor, actor.Executable);
        RuntimeStore.Report(config, ticket, actor, completed: false);

        using var previous = Assert.IsAssignableFrom<FileStream>(RuntimeStatePersistence.OpenSnapshot(RuntimeStore.RecordPath(config)));
        RuntimeStore.Report(config, ticket, actor, completed: true);

        using var oldSnapshot = JsonDocument.Parse(previous);
        Assert.Equal((int)RuntimeState.Running, oldSnapshot.RootElement.GetProperty("state").GetInt32());
        Assert.Equal(ticket.AttemptId, oldSnapshot.RootElement.GetProperty("attemptId").GetString());
        var current = RuntimeStore.Observe(config);
        Assert.Equal(RuntimeState.Quiescent, current.State);
        Assert.Equal(ticket.AttemptId, current.AttemptId);
    }

    [Fact]
    public void HealthyPublishesWhileStartupSnapshotIsOpen()
    {
        var config = Config();
        RuntimeServiceState.RequireSelection(config, "1");
        RuntimeServiceState.Starting(config, backup: null);

        using var previous = Assert.IsAssignableFrom<FileStream>(RuntimeStatePersistence.OpenSnapshot(RuntimeServiceState.SnapshotPath(config)));
        RuntimeServiceState.Healthy(config);

        using var oldSnapshot = JsonDocument.Parse(previous);
        Assert.Equal((int)ServiceRuntimePhase.StartupHealthPending, oldSnapshot.RootElement.GetProperty("phase").GetInt32());
        Assert.Equal(ServiceRuntimePhase.Ready, RuntimeServiceState.Read(config)!.Phase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(87)]
    [InlineData(120)]
    public void UnsupportedWindowsReplacePreservesRunningSnapshot(int nativeError)
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = Config();
        var actor = RuntimeIdentities.Current();
        var ticket = RuntimeStore.Begin(config, actor, actor.Executable);
        RuntimeStore.Attach(config, ticket, actor, actor.Executable);
        RuntimeStore.Report(config, ticket, actor, completed: false);
        var path = RuntimeStore.RecordPath(config);
        var previous = File.ReadAllBytes(path);

        RuntimeStatePersistence.WindowsRenameError.Value = nativeError;
        try
        {
            // No reader is held here. A fallback to File.Move could succeed and publish completion.
            var error = Assert.Throws<IOException>(() => RuntimeStore.Report(config, ticket, actor, completed: true));
            Assert.Equal(nativeError, Assert.IsType<Win32Exception>(error.InnerException).NativeErrorCode);
        }
        finally { RuntimeStatePersistence.WindowsRenameError.Value = null; }

        Assert.Equal(previous, File.ReadAllBytes(path));
        var retained = RuntimeStore.Observe(config);
        Assert.Equal(RuntimeState.Running, retained.State);
        Assert.Equal(ticket.AttemptId, retained.AttemptId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void LinuxDirectoryFlushFailureDoesNotAcknowledgePublication()
    {
        if (!OperatingSystem.IsLinux()) return;
        var config = Config();
        var actor = RuntimeIdentities.Current();
        var ticket = RuntimeStore.Begin(config, actor, actor.Executable);
        RuntimeStore.Attach(config, ticket, actor, actor.Executable);
        RuntimeStore.Report(config, ticket, actor, completed: false);
        var path = RuntimeStore.RecordPath(config);
        var boundaries = new List<string>();
        RuntimeStatePersistence.Boundary.Value = (target, point) =>
        {
            if (target != path) return;
            boundaries.Add(point);
            if (point == "directory-flush") throw new IOException("Directory flush failed.");
        };
        try
        {
            var error = Assert.Throws<IOException>(() => RuntimeStore.Report(config, ticket, actor, completed: true));
            Assert.Equal("Directory flush failed.", error.Message);
        }
        finally { RuntimeStatePersistence.Boundary.Value = null; }

        Assert.Equal(new[] { "write", "flush", "replace", "directory-flush" }, boundaries);
        // The rename is visible even though the caller received no durable completion acknowledgement.
        var visible = RuntimeStore.Observe(config);
        Assert.Equal(RuntimeState.Quiescent, visible.State);
        Assert.Equal(ticket.AttemptId, visible.AttemptId);
    }

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
    public async Task SelectionBarrierSerializesLaunchAndRepeatedRecovery()
    {
        var c=Config(); RuntimeServiceState.RequireSelection(c,"1");
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var pending=Task.Run(()=>
        {
            RuntimeStatePersistence.Boundary.Value=(p,b)=> { if(p==RuntimeServiceState.SnapshotPath(c) && b=="replace") { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
            try { RuntimeServiceState.ConfirmSelection(c,RuntimeIdentities.Current(),"1"); }
            finally { RuntimeStatePersistence.Boundary.Value=null; }
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<InvalidOperationException>(()=>RuntimeStore.Begin(c,RuntimeIdentities.Current(),Environment.ProcessPath!));
            Assert.Throws<InvalidOperationException>(()=>RuntimeServiceState.ConfirmSelection(c,RuntimeIdentities.Current(),"1"));
        }
        finally { release.Set(); await pending; }
        Assert.Equal(ServiceRuntimePhase.Ready,RuntimeServiceState.Read(c)!.Phase);
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
