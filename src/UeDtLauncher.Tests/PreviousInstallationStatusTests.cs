using System.Text.Json;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class PreviousInstallationStatusTests
{
    [Theory][InlineData("normal")][InlineData("unauthenticated")][InlineData("not-allowed")][InlineData("wrong-track")][InlineData("pending")][InlineData("large")][InlineData("too-many")]
    public async Task HintIsBoundedAndAuthorized(string scenario)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-previous-status-"+Guid.NewGuid().ToString("N"));
        try
        {
            var selection=new ReleaseSelection("demo","prod","stable","windows-x64","2.0.0");
            var old=selection with {Version="1.0.0"};
            var config=new LauncherConfig {ProjectId="demo",VersionedInstallRoot=Path.Combine(root,"apps"),StateRootDir=Path.Combine(root,"state"),SelectedRelease=selection,CatalogAuthenticated=true,
                AuthenticatedCatalog=new(){Projects=[new(){ProjectId="demo",Releases=[new(){Version="1.0.0",Platform="windows-x64"},new(){Version="2.0.0",Platform="windows-x64"}]}]}};
            var state=Path.Combine(config.StateRootDir,old.ReleaseId);Directory.CreateDirectory(state);
            var app=Path.Combine(config.VersionedInstallRoot,old.ReleaseId);Directory.CreateDirectory(app);File.WriteAllText(Path.Combine(app,"game.exe"),"test");
            await JsonFiles.WriteAsync(Path.Combine(state,"install-state.json"),new InstallState {Version=old.Version,Environment="prod",Channel="stable",Platform=old.Platform,ManifestSha256=new string('a',64),InstalledAtUtc="2026-09-28T00:00:00Z"});
            await JsonFiles.WriteAsync(Path.Combine(state,"installed-manifest.json"),new LauncherManifest {AppId="demo",Version=old.Version,Platform=old.Platform,Channel="stable",EntryPoint="game.exe",Files=[new(){Path="game.exe",Size=4,Sha256=new string('a',64)}]});
            if(scenario=="unauthenticated")config.CatalogAuthenticated=false;
            if(scenario=="not-allowed")config.AuthenticatedCatalog.Projects[0].Releases.RemoveAt(0);
            if(scenario=="wrong-track")config.AuthenticatedCatalog.Projects[0].Releases[0].Environment="dev";
            if(scenario=="pending")await JsonFiles.WriteAsync(Path.Combine(state,"transaction.json"),new UpdateTransactionJournal());
            if(scenario=="large")await File.WriteAllTextAsync(Path.Combine(state,"install-state.json"),new string(' ',65537));
            if(scenario=="too-many")for(var i=0;i<257;i++)Directory.CreateDirectory(Path.Combine(config.StateRootDir,"demo","prod","stable","extra"+i));
            var hint=await PreviousInstallationStatus.FindAsync(config);
            if(scenario=="normal")Assert.Equal(old,hint!.Release);else Assert.Null(hint);
            Assert.Equal(selection,config.SelectedRelease);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void OptionalHintPreservesLegacyAndSelectedInstallationMeaning()
    {
        var old=JsonSerializer.Deserialize<ManagedProjectStatus>("{\"isInstalled\":false,\"updateRequired\":true}",JsonFiles.Options)!;
        Assert.Null(old.PreviousInstallation);
        var model=new LauncherDashboardViewModel();model.ApplyProjectStatus(old);Assert.Equal(PrimaryActionKind.InstallAndLaunch,model.PrimaryAction);
        var hint=new PreviousInstallation(new("demo","prod","stable","windows-x64","1"),"2026-09-28T00:00:00Z");
        model.ApplyProjectStatus(old with {PreviousInstallation=hint});Assert.Equal(PrimaryActionKind.UpdateAndLaunch,model.PrimaryAction);
        model.ApplyProjectStatus(old with {IsInstalled=true,InstalledVersion="2",UpdateRequired=false,PreviousInstallation=hint});Assert.Equal(PrimaryActionKind.Launch,model.PrimaryAction);
        model.Config.ClientProfile="developer";model.ApplyProjectStatus(old with {PreviousInstallation=hint});Assert.Equal(GeneralLauncherState.NotInstalled,model.GeneralState);
    }
}
