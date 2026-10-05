using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class ManagedClientBoundaryTests
{
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)]
    public void RequestsAndShortcutProfilesNeverCarryOperationalSettings(int schema)
    {
        var selection=new ReleaseSelection("demo","prod","stable",OperatingSystem.IsWindows()?"windows-x64":"linux-x64","1.0.0");
        var display=new LauncherConfig{SchemaVersion=schema,DeploymentMode="managed-agent",ProjectId="demo",TargetPlatform=selection.Platform,
            InstallDir="PRIVATE-INSTALL",StateRootDir="PRIVATE-STATE",ManifestPublicKeyPath="SECRET-KEY",DistributionServerUrl="https://SECRET-SERVER",LaunchArguments=["SECRET-ARG"],Security=new(){CredentialName="SECRET-CREDENTIAL"}};
        var client=ManagedClientContext.Create(display);ManagedClientContext.Bind(client,selection);
        Assert.DoesNotContain("SECRET",JsonSerializer.Serialize(client,JsonFiles.Options));
        Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(client,JsonFiles.Options));
        Assert.Equal(selection,client.SelectedRelease);Assert.Null(client.DistributionServerUrl);Assert.Null(client.LaunchArguments);
        var root=Path.Combine(Path.GetTempPath(),"uedt-managed-profile-"+Guid.NewGuid().ToString("N"));
        try
        {
            var presentation=new ManagedClientPresentation(selection,new string('a',64),Path.GetFullPath(root),"demo",new(false,false,false,"demo","UE-DT",null));
            var path=WindowsIntegration.CreateManagedLaunchProfile(presentation,root);
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var names=document.RootElement.EnumerateObject().Select(p=>p.Name).ToArray();
            Assert.DoesNotContain("security",names);Assert.DoesNotContain("installDir",names);Assert.DoesNotContain("stateRootDir",names);
            var saved=JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path),JsonFiles.Options)!;
            Assert.True(saved.IsManagedDeployment);Assert.Equal("exact",saved.VersionPolicy);Assert.Equal("1.0.0",saved.RequestedVersion);
            File.WriteAllText(path,"user-owned-change");
            Assert.Throws<InvalidDataException>(()=>WindowsIntegration.CreateManagedLaunchProfile(presentation,root));
            Assert.Equal("user-owned-change",File.ReadAllText(path));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public void ServiceOnlyConfigurationIsNotAutomaticallyLoadedByGui()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-managed-discovery-"+Guid.NewGuid().ToString("N"));
        try
        {
            var layout=new ManagedLauncherPathLayout(root,root,root,root,root,root);
            Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"launcher.config.json"),"service-only-invalid-json");
            var options=LauncherStartupOptions.Discover([],layout,Path.Combine(root,"client"),root);
            Assert.Equal(LauncherConfigSource.Missing,options.ConfigSource);Assert.False(options.ConfigExists);
        }
        finally{Directory.Delete(root,true);}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task ManagedGuiBuildsRequestsWithoutInvokingOperationalConfigLoader(bool developer)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-managed-ui-"+Guid.NewGuid().ToString("N"));
        var platform=OperatingSystem.IsWindows()?"windows-x64":"linux-x64";
        var config=new LauncherConfig{DeploymentMode="managed-agent",ProjectId="demo",TargetPlatform=platform,VersionPolicy="exact",RequestedVersion="1.0.0",InstallDir="PRIVATE-INSTALL",StateRootDir="PRIVATE-STATE",Projects=[new(){ProjectId="demo",DisplayName="demo"}]};
        var model=new LauncherDashboardViewModel(developer?LauncherEdition.Developer:LauncherEdition.General){Config=config,Catalog=new(){Projects=[new(){ProjectId="demo"}],Releases=[new(){ProjectId="demo",Environment="prod",Channel="stable",Platform=platform,Version="1.0.0",IsLatest=true}]}};
        var reads=0;
        var window=new MainWindow(new(Path.Combine(root,"client.json"),LauncherConfigSource.Explicit,false),model,new(),false,runtimeConfigLoader:_=>{reads++;throw new UnauthorizedAccessException("protected operational read");});
        try
        {
            var request=await (Task<LauncherConfig>)typeof(MainWindow).GetMethod("RunConfig",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[false,false])!;
            Assert.True(request.IsManagedDeployment);Assert.Equal(0,reads);Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(request));
            Assert.Null(request.CatalogUrl);Assert.Null(request.DistributionServerUrl);
        }
        finally{window.Close();}
    }

    [Fact]
    public void PresentationCannotSubstituteAnotherReleaseOrShortcutPath()
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","1.0.0");
        var value=new ManagedClientPresentation(selection,new string('a',64),null,"demo",new(false,false,false,"demo","UE-DT",null));
        Assert.Throws<InvalidDataException>(()=>value.Validate(selection with{Version="2.0.0"}));
        Assert.Throws<InvalidDataException>(()=>(value with{Shortcuts=value.Shortcuts with{Publisher="../outside"}}).Validate());
    }
}
