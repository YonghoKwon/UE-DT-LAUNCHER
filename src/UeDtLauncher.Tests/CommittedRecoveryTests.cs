using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class CommittedRecoveryTests
{
    [AvaloniaTheory]
    [InlineData(false,"success")][InlineData(true,"success")]
    [InlineData(false,"network")][InlineData(true,"network")]
    [InlineData(false,"damaged")][InlineData(true,"damaged")]
    [InlineData(false,"committed")][InlineData(true,"committed")]
    public async Task SuccessfulRepairNeverOffersRollbackAfterItsReadOnlyRecheckFails(bool managed,string outcome)
    {
        using var f=new Fixture(managed);
        f.Backend.AfterRepair=outcome;
        f.Window.Show();f.SetResume();
        await Invoke(f.Window,"TroubleshootAsync");Dispatcher.UIThread.RunJobs();
        Assert.Equal(1,f.Backend.Executions);Assert.Equal("good",File.ReadAllText(f.Payload));
        Assert.Null(Button(f.Window,"resume-operation"));Assert.Null(Button(f.Window,"cancel-operation"));
        Assert.Empty(f.Dialogs);Assert.Equal(0,f.Restores);
        if(outcome!="success")
        {
            Assert.Equal("파일 복구 완료 · 상태 재확인 필요",f.Presentation.Title);
            Assert.Equal(LauncherUiOperation.Check,f.Presentation.Retry?.Operation);
            Assert.Equal(f.Selection,f.Presentation.Retry?.Selection);
            Assert.False(string.IsNullOrWhiteSpace(f.Presentation.SupportId));
            if(outcome=="network")Assert.Equal("synthetic-support",f.Presentation.SupportId);
            var checks=f.Backend.Checks;f.Backend.AfterRepair="success";
            await Invoke(f.Window,"RetryCurrentAsync");
            Assert.Equal(checks+1,f.Backend.Checks);Assert.Equal(1,f.Backend.Executions);
            Assert.Null(f.Presentation.ErrorCode);Assert.Null(Button(f.Window,"resume-operation"));
        }
    }

    [AvaloniaTheory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task RestoreEndsDownloadCancellationAndClearsResumeEvenIfRecheckFails(bool managed,bool recheckFails)
    {
        using var f=new Fixture(managed);
        f.Backend.AfterRepair=recheckFails?"network":"success";f.Backend.AlreadyApplied=true;
        f.Window.Show();f.SetResume();
        Set(f.Window,"_operationCancellation",new CancellationTokenSource());Set(f.Window,"_running",true);
        var preview=new RollbackPreview("20261005000000",new string('b',64),"2026-10-05T00:00:00Z","1.0.0","1.0.0",false,true);
        var restoring=Invoke(f.Window,"RestoreUiPreviewAsync",f.Context,f.Config,preview);
        await f.RestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(Button(f.Window,"cancel-operation"));Assert.False(restoring.IsCompleted);
        f.RestoreFinish.SetResult();await restoring;await Invoke(f.Window,"FinishUiOperation");Dispatcher.UIThread.RunJobs();
        Assert.Equal(1,f.Restores);Assert.Equal("good",File.ReadAllText(f.Payload));
        Assert.Null(Button(f.Window,"resume-operation"));Assert.Null(Button(f.Window,"cancel-operation"));
        if(recheckFails)
        {
            Assert.Equal("백업 복원 완료 · 상태 재확인 필요",f.Presentation.Title);
            Assert.Equal(LauncherUiOperation.Check,f.Presentation.Retry?.Operation);
            f.Backend.AfterRepair="success";await Invoke(f.Window,"RetryCurrentAsync");
            Assert.Equal(1,f.Restores);Assert.Equal(0,f.Backend.Executions);
        }
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task FailedRestoreApplicationPreservesCancelledDownloadHint(bool managed)
    {
        using var f=new Fixture(managed);f.RestoreError=new IOException("synthetic apply failure");
        f.Window.Show();f.SetResume();
        var preview=new RollbackPreview("20261005000000",new string('b',64),"2026-10-05T00:00:00Z","1.0.0","1.0.0",false,true);
        var restoring=Invoke(f.Window,"RestoreUiPreviewAsync",f.Context,f.Config,preview);
        await f.RestoreStarted.Task;f.RestoreFinish.SetResult();
        await Assert.ThrowsAsync<IOException>(()=>restoring);
        Assert.Equal("bad",File.ReadAllText(f.Payload));Assert.Equal(0,f.Backend.Checks);
        Assert.NotNull(Button(f.Window,"resume-operation"));
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task QueryOnlyDoesNotDiscardAValidCancelledDownload(bool managed)
    {
        using var f=new Fixture(managed);f.Backend.AlreadyApplied=true;
        f.Window.Show();f.SetResume();
        await Invoke(f.Window,"TroubleshootAsync");
        Assert.Equal(0,f.Backend.Executions);Assert.Equal(0,f.Restores);
        Assert.NotNull(Button(f.Window,"resume-operation"));
    }

    private static Button? Button(MainWindow w,string id)=>w.GetVisualDescendants().OfType<Button>().SingleOrDefault(b=>AutomationProperties.GetAutomationId(b)==id);
    private static Task Invoke(MainWindow w,string name,params object?[] args)
    {
        var method=typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
        return method.Invoke(w,args) as Task ?? Task.CompletedTask;
    }
    private static void Set(MainWindow w,string name,object? value)
    {
        var field=typeof(MainWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
        if(field is not null)field.SetValue(w,value);
        else typeof(MainWindow).GetProperty(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(w,value);
    }
    private sealed class Backend(Fixture f):ILauncherUiBackend
    {
        public int Checks;public int Executions;public string AfterRepair="success";public bool AlreadyApplied;
        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext c,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)
        {
            Checks++;
            if((Executions>0||AlreadyApplied)&&AfterRepair=="network")throw new AgentOperationException("server-unavailable","synthetic-support","synthetic network failure");
            return Task.FromResult(f.Result(config,(Executions==0&&!AlreadyApplied)||AfterRepair=="damaged"));
        }
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext c,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)
        {
            Assert.True(repair);Assert.False(launch);Executions++;File.WriteAllText(f.Payload,"good");
            return Task.FromResult(f.Result(config,false,AfterRepair=="committed"?LauncherUiCompletion.CommittedRefreshRequired:LauncherUiCompletion.Completed));
        }
    }
    private sealed class Fixture:IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"uedt-committed-recovery-"+Guid.NewGuid().ToString("N"));
        public string Payload=>Path.Combine(Root,"payload.txt");
        public ReleaseSelection Selection=new("demo","prod","stable",OperatingSystem.IsWindows()?"windows-x64":"linux-x64","1.0.0");
        public LauncherConfig Config;public LauncherUiOperationContext Context;public Backend Backend;public MainWindow Window;
        public int Restores;public TaskCompletionSource RestoreStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? RestoreError;
        public TaskCompletionSource RestoreFinish=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LauncherOperationPresentation Presentation=>(LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Window)!;
        public List<Window> Dialogs=>(List<Window>)typeof(MainWindow).GetField("_openDialogs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Window)!;
        public Fixture(bool managed)
        {
            Directory.CreateDirectory(Root);File.WriteAllText(Payload,"bad");
            Config=new(){DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo",DistributionServerUrl="https://fixture.invalid",Environment="prod",Channel="stable",TargetPlatform=Selection.Platform,VersionPolicy="exact",RequestedVersion=Selection.Version,InstallDir=Path.Combine(Root,"apps"),StateRootDir=Path.Combine(Root,"state"),LogDir=Path.Combine(Root,"logs"),Projects=[new(){ProjectId="demo"}]};
            var path=Path.Combine(Root,"config.json");File.WriteAllText(path,JsonSerializer.Serialize(Config,JsonFiles.Options));
            Context=new(managed,"demo","prod","stable",Selection.Platform,"exact",Selection.Version,Selection);
            Backend=new(this);
            var model=new LauncherDashboardViewModel{Config=Config,GeneralState=GeneralLauncherState.Ready,Catalog=new(){Projects=[new(){ProjectId="demo"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=Selection.Platform,Version=Selection.Version,IsLatest=true}]}};
            Window=new(new(path,LauncherConfigSource.Explicit,false),model,new(),false,Backend,runtimeConfigLoader:_=>LauncherPaths.LoadResolvedAsync(path),restorePreview:async(_,_)=>{Restores++;RestoreStarted.SetResult();await RestoreFinish.Task;if(RestoreError is not null)throw RestoreError;File.WriteAllText(Payload,"good");});
        }
        public LauncherUiOperationResult Result(LauncherConfig config,bool damaged,LauncherUiCompletion completion=LauncherUiCompletion.Completed)
        {
            Context.Pin(config);
            if(config.IsManagedDeployment)ManagedClientContext.Bind(config,Selection);
            else VersionedReleasePaths.Bind(config,Selection);
            return new(config,Selection,new(true,Selection.Version,Selection.Version,damaged,0,damaged?1:0,true),new(RuntimeState.Quiescent,"stopped",""),completion);
        }
        public void SetResume()=>Set(Window,"_resumeOperation",new OperationStatus(1,Guid.NewGuid().ToString("N"),"owner","session",Selection,"Cancelled",true,DateTimeOffset.UtcNow.ToString("O"),new string('a',64)));
        public void Dispose(){foreach(var dialog in Dialogs.ToArray())dialog.Close();Window.Close();Directory.Delete(Root,true);}
    }
}
