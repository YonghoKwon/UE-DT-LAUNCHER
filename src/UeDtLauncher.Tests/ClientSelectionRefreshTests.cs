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

public class ClientSelectionRefreshTests
{
    public static IEnumerable<object[]> PreparationCases => from managed in new[]{false,true}
        from previous in new[]{LauncherUiOperation.Update,LauncherUiOperation.Repair,LauncherUiOperation.Launch}
        select new object[]{managed,previous};

    [AvaloniaTheory][MemberData(nameof(PreparationCases))]
    public async Task TroubleshootPreparationFailureRetriesItsOwnAction(bool managed,LauncherUiOperation previous)
    {
        using var f=new Fixture(managed);f.FailPreparation=true;
        if(managed)f.Model.Catalog=new();
        f.Previous(previous);await Invoke(f.Window,"TroubleshootAsync");
        Assert.Equal(LauncherUiOperation.Troubleshoot,f.Presentation.Retry?.Operation);
        var calls=f.Preparations;f.PreparationEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button(f.Window,"retry-operation").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await f.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
        Assert.True(f.Preparations>calls);Assert.Equal(0,f.Backend.Mutations);
    }

    [AvaloniaTheory][InlineData(LauncherUiOperation.Update)][InlineData(LauncherUiOperation.Repair)][InlineData(LauncherUiOperation.Launch)]
    public async Task ResumePreparationFailureKeepsResumeButRetryOnlyChecks(LauncherUiOperation previous)
    {
        using var f=new Fixture(false);f.FailPreparation=true;f.Previous(previous);
        var resume=new OperationStatus(1,Guid.NewGuid().ToString("N"),"owner","session",f.Selection,"Cancelled",true,"now",new string('a',64));
        Set(f.Window,"_resumeOperation",resume);await Invoke(f.Window,"ResumeUiOperationAsync");
        Assert.Equal(LauncherUiOperation.Check,f.Presentation.Retry?.Operation);
        Assert.Same(resume,Get(f.Window,"_resumeOperation"));
        f.PreparationEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button(f.Window,"retry-operation").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await f.PreparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
        Assert.Equal(0,f.Backend.Mutations);Assert.Same(resume,Get(f.Window,"_resumeOperation"));
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task ActualCatalogRefreshInvalidatesOldProjectOrPromotedVersionBeforeChecking(bool removeProject)
    {
        using var f=new Fixture(true);f.NextCatalog=Catalog(removeProject?"other":"demo","2.0.0",f.Platform);
        f.Model.ProjectStatus=new(true,"1.0.0","1.0.0",false,0,0,true);f.Model.GeneralState=GeneralLauncherState.Ready;
        Set(f.Window,"_selectedRuntimeConfig",f.Config);f.Backend.HoldCheck=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Button(f.Window,"catalog-refresh").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await f.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
        Assert.Null(f.Model.ProjectStatus);Assert.Null(Get(f.Window,"_selectedRuntimeConfig"));
        Assert.Equal(removeProject?"other":"demo",f.Backend.Context!.ProjectId);
        Assert.Equal("2.0.0",f.Backend.Context.Selection!.Version);Assert.Equal(0,f.Backend.Mutations);
        f.Backend.HoldCheck.SetResult();await f.Backend.Returned.Task;Dispatcher.UIThread.RunJobs();
    }

    private static CatalogSnapshot Catalog(string project,string version,string platform)=>new(){Projects=[new(){ProjectId=project,DisplayName=project}],
        Releases=[new(){ProjectId=project,Version=version,Environment="prod",Channel="stable",Platform=platform,IsLatest=true}]};
    private static Button Button(MainWindow w,string id)=>w.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id);
    private static object? Get(MainWindow w,string name)=>typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(w);
    private static void Set(MainWindow w,string name,object value)=>typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(w,value);
    private static Task Invoke(MainWindow w,string name)=>(Task)typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,null)!;
    private sealed class Fixture:IDisposable
    {
        public string Platform=OperatingSystem.IsWindows()?"windows-x64":"linux-x64";
        public ReleaseSelection Selection;public LauncherConfig Config;public LauncherDashboardViewModel Model;public MainWindow Window;public Backend Backend;
        public bool FailPreparation;public int Preparations;public CatalogSnapshot? NextCatalog;
        public TaskCompletionSource PreparationEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LauncherOperationPresentation Presentation=>(LauncherOperationPresentation)Get(Window,"_presentation")!;
        public Fixture(bool managed)
        {
            Selection=new("demo","prod","stable",Platform,"1.0.0");
            Config=new(){DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo",ClientProfile="developer",DistributionServerUrl="https://fixture.invalid",TargetPlatform=Platform,VersionPolicy="latest",Projects=[new(){ProjectId="demo"}]};
            Model=new(LauncherEdition.Developer){Config=Config,Catalog=Catalog("demo","1.0.0",Platform),GeneralState=GeneralLauncherState.Ready};
            Backend=new();Window=new(new(Path.Combine(Path.GetTempPath(),"sprint-display.json"),LauncherConfigSource.Explicit,false),Model,new(),false,Backend,
                runtimeConfigLoader:_=>{Preparation();return Task.FromResult(Config);},catalogLoader:(_,_,_)=>{Preparation();return Task.FromResult(NextCatalog??Model.Catalog);});Window.Show();
        }
        private void Preparation(){Preparations++;PreparationEntered.TrySetResult();if(FailPreparation)throw new IOException("synthetic config unavailable");}
        public void Previous(LauncherUiOperation kind)=>Presentation.Retry=new(kind,"demo","prod","stable","latest:",Selection);
        public void Dispose(){foreach(var d in ((List<Window>)Get(Window,"_openDialogs")!).ToArray())d.Close();Window.Close();}
    }
    private sealed class Backend:ILauncherUiBackend
    {
        public int Mutations;public LauncherUiOperationContext? Context;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Returned=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HoldCheck;
        public async Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)
        {
            Context=context;Entered.TrySetResult();if(HoldCheck is not null)await HoldCheck.Task;
            context.Pin(config);if(context.Managed)ManagedClientContext.Bind(config,context.Selection!);
            Returned.TrySetResult();return new(config,context.Selection,new(false,null,context.Selection?.Version,true,0,0,false),new(RuntimeState.Quiescent,"stopped",""));
        }
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext c,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)
        {Mutations++;throw new InvalidOperationException("Previous mutation must not execute");}
    }
}
