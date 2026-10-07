using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class LauncherOperationControlTests
{
    public static IEnumerable<object[]> Cases => from managed in new[]{false,true}
        from developer in new[]{false,true} from outcome in new[]{"success","cancel","early-cancel","failure","committed"}
        select new object[]{managed,developer,outcome};

    [AvaloniaTheory][MemberData(nameof(Cases))]
    public async Task ControlsFollowActualBackendLifetimeAndDiscardLateProgress(bool managed,bool developer,string outcome)
    {
        using var fixture=new Fixture(managed,developer);
        var window=fixture.Window;window.Show();
        try
        {
            var operation=Invoke(window,"RunAsync",true,false,null);
            await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(Button(window,"cancel-operation"));
            Assert.True(fixture.Model.Running);
            if(outcome is "cancel" or "early-cancel")
            {
                Button(window,"cancel-operation")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Assert.True(fixture.Backend.Token.IsCancellationRequested);
                Assert.False(operation.IsCompleted);
                Assert.False(Button(window,"cancel-operation")!.IsEnabled);
                Assert.Contains("취소 요청 중",Presentation(window).Title);
                fixture.Backend.Terminal.SetException(new LauncherUiCancelledException(fixture.Record(outcome=="cancel")));
            }
            else if(outcome=="failure")fixture.Backend.Terminal.SetException(new IOException("synthetic failure"));
            else fixture.Backend.Terminal.SetResult(fixture.Result(outcome=="committed"?LauncherUiCompletion.CommittedLaunchSkipped:LauncherUiCompletion.Completed));
            await operation;
            Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.Model.Running);
            Assert.Null(Button(window,"cancel-operation"));
            Assert.Equal(outcome=="cancel",Button(window,"resume-operation") is not null);
            if(outcome=="early-cancel")Assert.DoesNotContain("이어받",Presentation(window).Title);
            if(outcome=="committed")Assert.Contains("실행은 생략",Presentation(window).Title);
            var completed=Presentation(window).Title;
            await Task.Run(()=>fixture.Backend.Progress!(new("DownloadProgress","late bytes",12)));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(completed,Presentation(window).Title);
            if(outcome=="cancel")
            {
                fixture.Backend.Terminal=new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Backend.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
                var oldProgress=fixture.Backend.Progress!;
                var resumed=Invoke(window,"ResumeUiOperationAsync");
                await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var title=Presentation(window).Title;
                await Task.Run(()=>oldProgress(new("Download","old operation",5)));
                Dispatcher.UIThread.RunJobs();Assert.Equal(title,Presentation(window).Title);
                Assert.False(fixture.Backend.Launch);
                fixture.Backend.Terminal.SetResult(fixture.Result());await resumed;
                Assert.Null(Button(window,"resume-operation"));Assert.Null(Button(window,"cancel-operation"));
                Assert.Contains("재개 및 검증 완료",Presentation(window).Title);
            }
        }
        finally{foreach(var dialog in (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows.ToArray()??[])if(dialog!=window)dialog.Close();window.Close();}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task NormalRunningBlocksMutationWithoutCreatingAnError(bool managed)
    {
        using var fixture=new Fixture(managed,false);fixture.Window.Show();
        var operation=Invoke(fixture.Window,"RunAsync",false,false,null);await fixture.Backend.Started.Task;
        fixture.Backend.Terminal.SetResult(fixture.Result() with{Runtime=new(RuntimeState.Running,"tracked","실행 중")});await operation;
        Assert.Equal(GeneralLauncherState.RuntimeBlocked,fixture.Model.GeneralState);
        Assert.False(Button(fixture.Window,"primary-action")!.IsEnabled);
        Assert.Null(Presentation(fixture.Window).ErrorCode);Assert.Null(Presentation(fixture.Window).SupportId);
        Assert.Null(Button(fixture.Window,"cancel-operation"));fixture.Window.Close();
    }

    [AvaloniaTheory][InlineData(false,"cancel")][InlineData(false,"escape")][InlineData(false,"close")]
    [InlineData(true,"cancel")][InlineData(true,"escape")][InlineData(true,"close")]
    public async Task DeveloperConfirmationDismissalNeverStartsBackend(bool managed,string dismissal)
    {
        using var fixture=new Fixture(managed,true);fixture.Window.Show();
        var original=Presentation(fixture.Window).Title;var state=fixture.Model.GeneralState;
        var task=Invoke(fixture.Window,"RunAsync",false,true,null);
        Dispatcher.UIThread.RunJobs();
        var dialogs=(List<Window>)typeof(MainWindow).GetField("_openDialogs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture.Window)!;
        var dialog=Assert.Single(dialogs);
        Assert.Null(Button(fixture.Window,"cancel-operation"));
        if(dismissal=="cancel")dialog.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="dialog-cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        else if(dismissal=="escape")dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape,Avalonia.Input.RawInputModifiers.None);
        else dialog.Close();
        await task;Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.Backend.Started.Task.IsCompleted);Assert.False(fixture.Model.Running);
        Assert.Equal(state,fixture.Model.GeneralState);Assert.Equal(original,Presentation(fixture.Window).Title);
        Assert.Null(Button(fixture.Window,"cancel-operation"));
    }

    private static Button? Button(MainWindow window,string id)=>window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b=>AutomationProperties.GetAutomationId(b)==id);
    private static LauncherOperationPresentation Presentation(MainWindow w)=>(LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(w)!;
    private static Task Invoke(MainWindow w,string name,params object?[] args)=>(Task)typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(w,args)!;
    private sealed class Backend:ILauncherUiBackend
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LauncherUiOperationResult> Terminal=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<LauncherProgress>? Progress;public CancellationToken Token;public bool Launch;
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)
        {Progress=progress;Token=token;Launch=launch;Started.TrySetResult();return Terminal.Task;}
        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)=>throw new InvalidOperationException("Unexpected query");
    }
    private sealed class Fixture:IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"uedt-controls-"+Guid.NewGuid().ToString("N"));
        public ReleaseSelection Selection=new("demo","prod","stable",OperatingSystem.IsWindows()?"windows-x64":"linux-x64","1.0.0");
        public LauncherConfig Config;public LauncherDashboardViewModel Model;public Backend Backend=new();public MainWindow Window;
        public Fixture(bool managed,bool developer)
        {
            Directory.CreateDirectory(Root);
            Config=new(){DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo",DistributionServerUrl="https://fixture.invalid",Environment="prod",Channel="stable",TargetPlatform=Selection.Platform,VersionPolicy="exact",RequestedVersion="1.0.0",InstallDir=Path.Combine(Root,"apps"),StateRootDir=Path.Combine(Root,"state"),LogDir=Path.Combine(Root,"logs"),Projects=[new(){ProjectId="demo",DisplayName="synthetic"}]};
            var path=Path.Combine(Root,"config.json");File.WriteAllText(path,JsonSerializer.Serialize(Config,JsonFiles.Options));
            Model=new(developer?LauncherEdition.Developer:LauncherEdition.General){Config=Config,GeneralState=GeneralLauncherState.Ready,Catalog=new(){Projects=[new(){ProjectId="demo",DisplayName="synthetic"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=Selection.Platform,Version="1.0.0",IsLatest=true}]}};
            Window=new(new(path,LauncherConfigSource.Explicit,false),Model,new(),false,Backend,runtimeConfigLoader:_=>LauncherPaths.LoadResolvedAsync(path));
        }
        public OperationStatus Record(bool digest)=>new(1,Guid.NewGuid().ToString("N"),"owner","session",Selection,"Cancelled",true,DateTimeOffset.UtcNow.ToString("O"),digest?new string('a',64):null);
        public LauncherUiOperationResult Result(LauncherUiCompletion completion=LauncherUiCompletion.Completed)
        {
            var config=JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(Config,JsonFiles.Options),JsonFiles.Options)!;
            LauncherPaths.ResolveInPlace(config,Path.Combine(Root,"config.json"));
            config.SelectedRelease=Selection;VersionedReleasePaths.Bind(config,Selection);
            return new(config,Selection,new(true,"1.0.0","1.0.0",false,0,0,true),new(RuntimeState.Quiescent,"stopped",""),completion);
        }
        public void Dispose(){Window.Close();Directory.Delete(Root,true);}
    }
}
