using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Reflection;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class ManagedDisplayTemplateTests
{
    [AvaloniaTheory][InlineData(LauncherEdition.General,"primary-action")][InlineData(LauncherEdition.Developer,"primary-action")][InlineData(LauncherEdition.General,"status-check")][InlineData(LauncherEdition.Developer,"status-check")]
    public async Task ActualConfigurationRecoveryButtonsReloadAndOnlyQuery(LauncherEdition edition,string action)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-ui-config-retry-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"client.json");var queried=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model=new LauncherDashboardViewModel(edition);var backend=new ConfigurationBackend(queried);
        var window=new MainWindow(new(path,LauncherConfigSource.Explicit,false),model,new(),false,backend,
            catalogLoader:(c,_,_)=>{Assert.Equal("demo",c.ProjectId);return Task.FromResult(new CatalogSnapshot{Projects=[new(){ProjectId="demo"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=OperatingSystem.IsWindows()?"windows-x64":"linux-x64",Version="1.0.0",IsLatest=true}]});},
            runtimeConfigLoader:p=>{var c=new LauncherConfig{DeploymentMode="portable",ProjectId="demo",DistributionServerUrl="https://fixture.invalid",InstallDir=Path.Combine(root,"app"),StateRootDir=Path.Combine(root,"state"),Projects=[new(){ProjectId="demo"}]};LauncherPaths.ResolveInPlace(c,p);return Task.FromResult(c);});
        try
        {
            typeof(MainWindow).GetMethod("LoadConfig",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[]);
            typeof(MainWindow).GetMethod("Build",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[]);window.Show();
            var controls=window.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Contains(controls.Single(b=>AutomationProperties.GetAutomationId(b)=="primary-action").GetLogicalDescendants().OfType<TextBlock>(),t=>t.Text=="설정 다시 확인");
            if(edition==LauncherEdition.Developer)Assert.False(controls.Single(b=>AutomationProperties.GetAutomationId(b)=="update").IsEnabled);
            await JsonFiles.WriteAsync(path,new LauncherConfig{DeploymentMode="portable",ProjectId="demo",DistributionServerUrl="https://fixture.invalid",Projects=[new(){ProjectId="demo"}]});
            controls.Single(b=>AutomationProperties.GetAutomationId(b)==action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await queried.Task.WaitAsync(TimeSpan.FromSeconds(5));Dispatcher.UIThread.RunJobs();
            Assert.Equal("demo",model.Config.ProjectId);Assert.False(Directory.Exists(Path.Combine(root,"app")));
            Assert.Equal(0,backend.Mutations);Assert.Equal(1,backend.Checks);
            var dialogs=(List<Window>)typeof(MainWindow).GetField("_openDialogs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Empty(dialogs);
        }
        finally{window.Close();Directory.Delete(root,true);}
    }
    private sealed class ConfigurationBackend(TaskCompletionSource queried):ILauncherUiBackend
    {
        public int Mutations;public int Checks;
        public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)
        {
            Checks++;context.Pin(config);VersionedReleasePaths.Bind(config,context.Selection!);queried.TrySetResult();
            return Task.FromResult(new LauncherUiOperationResult(config,context.Selection,new(false,null,context.Selection!.Version,false,0,0,false),new(RuntimeState.Quiescent,"new-install","")));
        }
        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default){Mutations++;throw new InvalidOperationException("Settings retry must not install or launch");}
    }
    [Theory][InlineData("windows-x64")][InlineData("linux-x64")]
    public void TemplateContainsOnlyDisplaySelectionAndLoadsAsManagedClient(string platform)
    {
        var template=LauncherConfigurationTemplates.ManagedClient("demo",platform);
        var json=JsonSerializer.Serialize(template,JsonFiles.Options);
        using var parsed=JsonDocument.Parse(json);
        Assert.Equal(new[]{"channel","deploymentMode","environment","manifestUrl","projectId","schemaVersion","targetPlatform","versionPolicy"},parsed.RootElement.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal));
        var config=JsonSerializer.Deserialize<LauncherConfig>(json,JsonFiles.Options)!;
        var context=ManagedClientContext.Create(config);
        Assert.Equal("managed-agent",context.DeploymentMode);Assert.Equal("demo",context.ProjectId);
        Assert.Null(context.DistributionServerUrl);Assert.Equal("",context.ManifestUrl);
    }
    [Fact]
    public async Task CliExactOutputPreservesExistingFilesAndNeverWritesOperationalFields()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-managed-display-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"display.json");
        try
        {
            var args=new[]{"sample-config","--mode","managed-client","--project-id","demo","--platform","linux-x64","--environment","dev","--channel","beta","--version-policy","exact","--version","2.0.0","--output",path};
            Assert.Equal(0,await Program.MainAsync(args));
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("2.0.0",doc.RootElement.GetProperty("requestedVersion").GetString());
            foreach(var name in new[]{"security","credentialName","installDir","stateRootDir","distributionServerUrl","clientProfile"})Assert.False(doc.RootElement.TryGetProperty(name,out _));
            await File.WriteAllTextAsync(path,"user-owned");
            Assert.Equal(1,await Program.MainAsync(args));Assert.Equal("user-owned",await File.ReadAllTextAsync(path));
            Assert.Equal(0,await Program.MainAsync([..args,"--FORCE"]));
            Assert.Empty(Directory.EnumerateFiles(root,"*.tmp"));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory][InlineData("--profile","developer")][InlineData("--server-url","https://example.invalid")][InlineData("--version-policy","exact")]
    public async Task InvalidOrOperationalOptionsDoNotCreateConfiguration(string option,string value)
    {
        var path=Path.Combine(Path.GetTempPath(),"uedt-invalid-display-"+Guid.NewGuid().ToString("N"),"display.json");
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--project-id","demo",option,value,"--output",path]));
        Assert.False(File.Exists(path));Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
    [Fact]
    public async Task MissingProjectAndInconsistentVersionDoNotGenerateAFile()
    {
        var path=Path.Combine(Path.GetTempPath(),"uedt-missing-display-"+Guid.NewGuid().ToString("N")+".json");
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--output",path]));
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--project-id","demo","--version","2.0.0","--output",path]));
        Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task AtomicNoOverwriteDoesNotReplaceAConcurrentWritersConfiguration()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-display-race-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"display.json");
        try
        {
            async Task<bool> Write(string id){try{await JsonFiles.WriteAsync(path,LauncherConfigurationTemplates.ManagedClient(id),overwrite:false);return true;}catch(IOException){return false;}}
            var results=await Task.WhenAll(Write("first"),Write("second"));
            Assert.Single(results,v=>v);
            var value=JsonSerializer.Deserialize<LauncherConfig>(await File.ReadAllTextAsync(path),JsonFiles.Options)!;
            Assert.Contains(value.ProjectId,new[]{"first","second"});
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [AvaloniaTheory][InlineData(LauncherEdition.General)][InlineData(LauncherEdition.Developer)]
    public void ConfigurationRequiredScreenIncludesAccessibleCommandHelp(LauncherEdition edition)
    {
        var model=new LauncherDashboardViewModel(edition){GeneralState=GeneralLauncherState.ConfigurationRequired};
        var window=new MainWindow(new(Path.Combine(Path.GetTempPath(),"missing-display.json"),LauncherConfigSource.Missing,false),model,new(),false);
        try
        {
            window.Show();
            var command=window.GetLogicalDescendants().OfType<TextBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="configuration-example");
            Assert.True(command.IsReadOnly);Assert.Contains("--mode managed-client",command.Text);
            Assert.Equal(edition==LauncherEdition.Developer,model.IsDeveloper);
        }
        finally{window.Close();}
    }
}
