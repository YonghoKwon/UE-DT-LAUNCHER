using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class ClientActionRetryTests
{
    public static IEnumerable<object[]> PreviewCases=>from managed in new[]{false,true} from previous in new[]{LauncherUiOperation.Update,LauncherUiOperation.Repair,LauncherUiOperation.Launch} select new object[]{managed,previous};
    [AvaloniaTheory][MemberData(nameof(PreviewCases))]
    public async Task PreviewFailureRetryNeverRepeatsPreviousMutation(bool managed,LauncherUiOperation previous)
    {
        using var f=new Fixture(managed);f.SetPrevious(previous);
        await Invoke(f.Window,"RollbackLatestAsync");
        Assert.Equal(LauncherUiOperation.Rollback,f.Presentation.Retry!.Operation);
        f.NextCall=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button(f.Window,"retry-operation").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await f.NextCall.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
        Assert.Equal(2,f.Calls);Assert.Equal(0,f.Backend.Mutations);
        Assert.Equal(LauncherUiOperation.Rollback,f.Presentation.Retry!.Operation);
    }
    [AvaloniaTheory]
    [InlineData(false,LauncherUiOperation.Update)][InlineData(false,LauncherUiOperation.Repair)][InlineData(false,LauncherUiOperation.Launch)]
    [InlineData(true,LauncherUiOperation.Update)][InlineData(true,LauncherUiOperation.Repair)][InlineData(true,LauncherUiOperation.Launch)]
    public async Task MaintenanceFailureRetryIsOnlyTheRequestedCleanup(bool backups,LauncherUiOperation previous)
    {
        using var f=new Fixture(false);f.SetPrevious(previous);
        await Invoke(f.Window,"RunMaintenanceAsync",backups);
        var expected=backups?LauncherUiOperation.PruneBackups:LauncherUiOperation.ClearStaging;
        Assert.Equal(expected,f.Presentation.Retry!.Operation);
        f.NextCall=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button(f.Window,"retry-operation").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await f.NextCall.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
        Assert.Equal(2,f.Calls);Assert.Equal(0,f.Backend.Mutations);Assert.Equal(expected,f.Presentation.Retry!.Operation);
    }
    [AvaloniaFact]
    public async Task CancelledConfirmationPreservesPreviousPresentationAndResume()
    {
        using var f=new Fixture(true);f.PreviewFails=false;f.SetPrevious(LauncherUiOperation.Update);
        f.Presentation.Title="이전 작업 표시";f.Presentation.ErrorCode="previous-error";f.Presentation.SupportId="previous-support";
        var snapshot=f.Presentation.Capture();
        var resume=new OperationStatus(1,Guid.NewGuid().ToString("N"),"owner","session",f.Selection,"Cancelled",true,DateTimeOffset.UtcNow.ToString("O"),new string('a',64));
        typeof(MainWindow).GetField("_resumeOperation",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(f.Window,resume);
        var task=Invoke(f.Window,"RollbackLatestAsync");Dispatcher.UIThread.RunJobs();
        var dialog=((List<Window>)typeof(MainWindow).GetField("_openDialogs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Window)!).Single();
        dialog.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="dialog-cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await task;
        Assert.Equal(snapshot,f.Presentation.Capture());
        Assert.Same(resume,typeof(MainWindow).GetField("_resumeOperation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Window));
        Assert.Equal(0,f.Backend.Mutations);
    }
    [Fact]
    public void ManagedPreviewRequiresTheAgentConfirmedSelection()
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","1.0.0");
        var preview=new RollbackPreview("20261005000000",new string('a',64),"2026-10-05T00:00:00Z","1.0.0","1.0.0",false,true);
        var response=new ManagedAgentResponse{Success=true,SelectedRelease=selection,RollbackPreview=preview};
        Assert.Same(preview,MainWindow.ValidateManagedRollbackPreview(response,selection));
        Assert.Throws<InvalidDataException>(()=>MainWindow.ValidateManagedRollbackPreview(response,selection with{Version="2.0.0"}));
        response.SelectedRelease=null;
        Assert.Throws<InvalidDataException>(()=>MainWindow.ValidateManagedRollbackPreview(response,selection));
    }
    private static Button Button(MainWindow window,string id)=>window.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id);
    private static Task Invoke(MainWindow window,string method,params object[] args)=>(Task)typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args)!;
    private sealed class Backend:ILauncherUiBackend
    {
        public int Mutations;
        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)=>throw new InvalidOperationException("Unexpected check");
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default){Mutations++;throw new InvalidOperationException("Previous mutation was repeated");}
    }
    private sealed class Fixture:IDisposable
    {
        public MainWindow Window;public Backend Backend=new();public int Calls;public bool PreviewFails=true;
        public TaskCompletionSource NextCall=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReleaseSelection Selection=new("demo","prod","stable",OperatingSystem.IsWindows()?"windows-x64":"linux-x64","1.0.0");
        public LauncherOperationPresentation Presentation=>(LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Window)!;
        public Fixture(bool managed)
        {
            var config=new LauncherConfig{DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo",TargetPlatform=Selection.Platform,VersionPolicy="exact",RequestedVersion="1.0.0",DistributionServerUrl="https://fixture.invalid",Projects=[new(){ProjectId="demo"}]};
            var model=new LauncherDashboardViewModel(LauncherEdition.Developer){Config=config,GeneralState=GeneralLauncherState.Ready,Catalog=new(){Projects=[new(){ProjectId="demo"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=Selection.Platform,Version="1.0.0",IsLatest=true}]}};
            Window=new(new(Path.Combine(Path.GetTempPath(),"retry-display.json"),LauncherConfigSource.Explicit,false),model,new(),false,Backend,
                runtimeConfigLoader:_=>{Calls++;NextCall.TrySetResult();throw new IOException("Config unavailable");},
                managedRollbackPreview:_=>{Calls++;NextCall.TrySetResult();if(PreviewFails)throw new IOException("Preview unavailable");return Task.FromResult<RollbackPreview?>(new("20261005000000",new string('a',64),"2026-10-05T00:00:00Z","1.0.0","1.0.0",false,true));});
            Window.Show();
        }
        public void SetPrevious(LauncherUiOperation previous)=>Presentation.Retry=new(previous,"demo","prod","stable","exact:1.0.0",Selection);
        public void Dispose(){foreach(var dialog in ((List<Window>)typeof(MainWindow).GetField("_openDialogs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Window)!).ToArray())dialog.Close();Window.Close();}
    }
}
