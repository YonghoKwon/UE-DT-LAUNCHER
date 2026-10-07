using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class PromotedSelectionTests
{
    private static DistributionCatalog Catalog(bool promoted) => new() { SelectionPolicy = CatalogRecommendation.ExplicitPolicy, Projects = [new()
    { ProjectId="demo", Releases=[new(){Version="1.0.0",IsLatest=promoted,AllowedClientProfiles=["general","developer"]},new(){Version="9.0.0",AllowedClientProfiles=["general","developer"]}] }] };
    [Fact]
    public void LatestNeverFallsBackToUnpromotedButExactCanSelectIt()
    {
        var config = new LauncherConfig { ProjectId="demo" }; var catalog = Catalog(false);
        Assert.Throws<NoPromotedReleaseException>(()=>CatalogResolver.SelectRelease(catalog,config));
        config.VersionPolicy="exact"; config.RequestedVersion="9.0.0"; Assert.Equal("9.0.0",CatalogResolver.SelectRelease(catalog,config).Version);
        config.VersionPolicy="latest"; Assert.Equal("1.0.0",CatalogResolver.SelectRelease(Catalog(true),config).Version);
    }
    [Fact]
    public void LegacyFallbackRemainsAndUnknownOrConflictingPolicyFails()
    {
        var c = Catalog(false); c.SelectionPolicy=null; Assert.Equal("9.0.0",CatalogResolver.SelectRelease(c,new(){ProjectId="demo"}).Version);
        c.SelectionPolicy="unknown-policy"; Assert.Throws<InvalidDataException>(()=>CatalogResolver.SelectRelease(c,new(){ProjectId="demo"}));
        c=Catalog(true);c.Projects[0].Releases[1].IsLatest=true;Assert.Throws<InvalidDataException>(()=>CatalogResolver.SelectRelease(c,new(){ProjectId="demo"}));
    }
    [Fact]
    public void PolicyQueryIsOnlyUsedForDistributionAndDoesNotDuplicate()
    {
        var c=new LauncherConfig {CatalogUrl="https://updates/api/v1/catalog",DistributionServerUrl="https://updates"};
        Assert.EndsWith("?selectionPolicy=explicit-promotion-v1",CatalogRecommendation.RequestUrl(c));
        c.CatalogUrl=CatalogRecommendation.RequestUrl(c);Assert.Equal(c.CatalogUrl,CatalogRecommendation.RequestUrl(c));
        c.DistributionServerUrl=null;Assert.Equal(c.CatalogUrl,CatalogRecommendation.RequestUrl(c));
    }
    [AvaloniaFact]
    public async Task GeneralWaitingStateHasNoInstallActionOrSelectedVersion()
    {
        var root=Path.Combine(Path.GetTempPath(),"promotion-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var config=new LauncherConfig {ProjectId="demo",CatalogUrl="https://updates/api/v1/catalog",DistributionServerUrl="https://updates",ClientProfile="general",Projects=[new(){ProjectId="demo",DisplayName="Demo"}]};
        var path=Path.Combine(root,"client.json");await JsonFiles.WriteAsync(path,config);
        var snapshot=new CatalogSnapshot {SelectionPolicy=CatalogRecommendation.ExplicitPolicy,Projects=[new(){ProjectId="demo",DisplayName="Demo",ReleaseCount=1}],Releases=[new(){ProjectId="demo",Version="9.0.0",Environment="prod",Channel="stable",Platform=config.TargetPlatform}]};
        var model=new LauncherDashboardViewModel {Config=config,SelectedProject=config.Projects[0],Catalog=snapshot};
        var window=new MainWindow(new(path,LauncherConfigSource.Explicit,true),model,new(),false);
        try
        {
            window.Show();var refresh=typeof(MainWindow).GetMethod("RefreshInstallStatusAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            await (Task)refresh.Invoke(window,[true])!;
            Assert.Equal(GeneralLauncherState.AwaitingPromotion,model.GeneralState);Assert.Equal(PrimaryActionKind.Disabled,model.PrimaryAction);
            var primary=window.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="primary-action");Assert.False(primary.IsEnabled);
            Assert.Contains("관리자가 실행 버전을 지정하지 않았습니다.",string.Join('\n',window.GetLogicalDescendants().OfType<TextBlock>().Select(t=>t.Text)));
            Assert.False(Directory.Exists(Path.Combine(root,"app")));Assert.Null(model.ProjectStatus);
        }
        finally{window.Close();Directory.Delete(root,true);}
    }
}
