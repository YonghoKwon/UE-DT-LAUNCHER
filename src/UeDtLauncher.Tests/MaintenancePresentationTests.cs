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
public class MaintenancePresentationTests
{
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task SuccessfulCleanupRetryClearsOwnErrorAndOnlyRechecksInstallation(bool failCheck)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-cleanup-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var platform=OperatingSystem.IsWindows()?"windows-x64":"linux-x64";var selection=new ReleaseSelection("demo","prod","stable",platform,"1.0.0");
        var config=new LauncherConfig{ProjectId="demo",ClientProfile="developer",DistributionServerUrl="https://fixture.invalid",VersionPolicy="exact",RequestedVersion="1.0.0",TargetPlatform=platform,
            InstallDir=Path.Combine(root,"apps"),StateRootDir=Path.Combine(root,"state"),LogDir=Path.Combine(root,"logs"),Projects=[new(){ProjectId="demo"}]};
        LauncherPaths.ResolveInPlace(config,Path.Combine(root,"config.json"));VersionedReleasePaths.Bind(config,selection);RuntimeTestSupport.Stopped(config);
        Directory.CreateDirectory(config.StagingDir);File.WriteAllText(Path.Combine(config.StagingDir,"owned.partial"),"partial");
        var model=new LauncherDashboardViewModel(LauncherEdition.Developer){Config=config,Catalog=new(){Projects=[new(){ProjectId="demo"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=platform,Version="1.0.0",IsLatest=true}]},GeneralState=GeneralLauncherState.Ready};
        var failConfig=true;var backend=new Backend{Fail=failCheck};
        var window=new MainWindow(new(Path.Combine(root,"config.json"),LauncherConfigSource.Explicit,false),model,new(),false,backend,
            runtimeConfigLoader:_=>failConfig?throw new IOException("owned config unavailable"):Task.FromResult(config));window.Show();
        try
        {
            await (Task)typeof(MainWindow).GetMethod("RunMaintenanceAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{false})!;
            var presentation=(LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            Assert.NotNull(presentation.ErrorCode);failConfig=false;
            window.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="retry-operation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await backend.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
            Assert.False(Directory.Exists(config.StagingDir));Assert.Equal(1,backend.Checks);Assert.Equal(0,backend.Mutations);
            if(failCheck){Assert.Contains("정리 완료",presentation.Title);Assert.Equal(LauncherUiOperation.Check,presentation.Retry?.Operation);}
            else {Assert.Null(presentation.ErrorCode);Assert.Null(presentation.SupportId);Assert.Null(presentation.Retry);Assert.Contains("정리 완료",presentation.Title);}
        }
        finally{window.Close();Directory.Delete(root,true);}
    }
    private sealed class Backend:ILauncherUiBackend
    {
        public bool Fail;public int Checks,Mutations;public TaskCompletionSource Completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext c,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)
        {
            Checks++;Completed.TrySetResult();if(Fail)throw new IOException("owned status unavailable");
            return Task.FromResult(new LauncherUiOperationResult(config,c.Selection,new(false,null,c.Selection?.Version,true,0,0,false),new(RuntimeState.Quiescent,"stopped","")));
        }
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext c,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)
        {Mutations++;throw new InvalidOperationException("Cleanup cannot update or launch");}
    }
}
