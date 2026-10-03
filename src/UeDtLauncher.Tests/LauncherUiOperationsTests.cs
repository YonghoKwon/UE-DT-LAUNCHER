using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class LauncherUiOperationsTests
{
    [Fact]
    public void ResumeProtocolAllowsOnlyValidatedExpectedSelectionAndNoLaunchInput()
    {
        var request=new ManagedAgentRequest{Command="operation-resume",OperationId=Guid.NewGuid().ToString("N"),ProjectId="demo",Selection=new("demo","prod","stable","windows-x64","1")};
        Assert.Null(ManagedAgentProtocol.Validate(request));
        request.ProjectId="another";Assert.NotNull(ManagedAgentProtocol.Validate(request));request.ProjectId="demo";
        request.Selection=request.Selection with{Version="../bad"};Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.Selection=new("demo","prod","stable","windows-x64","1");
        request.Command="operation-cancel";Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.Command="operation-status";Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.Command="operation-discard";Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.Command="operation-resume";request.Selection=null;request.ProjectId=null;Assert.Null(ManagedAgentProtocol.Validate(request));
    }
    [Fact]
    public void ResumePinsManifestAndDoesNotLaunchOrAcceptAnotherSelection()
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","1");
        var context=new LauncherUiOperationContext(false,"demo","prod","stable","windows-x64","exact","1",selection);
        var record=new OperationStatus(1,Guid.NewGuid().ToString("N"),"owner","session",selection,"Cancelled",true,DateTimeOffset.UtcNow.ToString("O"),new string('a',64));
        var config=new LauncherConfig{UiResumeOperation=record,LaunchAfterUpdate=true};
        Assert.Equal(record,LauncherUiOperations.BindResume(context,config));
        Assert.False(config.LaunchAfterUpdate);Assert.Equal(record.ManifestSha256,config.ExpectedResumeManifestSha256);
        config.UiResumeOperation=record with{Selection=selection with{Version="2"}};
        Assert.Throws<InvalidDataException>(()=>LauncherUiOperations.BindResume(context,config));
        config.UiResumeOperation=record with{ManifestSha256=null};
        Assert.Throws<InvalidDataException>(()=>LauncherUiOperations.BindResume(context,config));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ContextPinsTheConfirmedVersionWithoutChangingMode(bool managed)
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","1.0.0");
        var context=new LauncherUiOperationContext(managed,"demo","prod","stable","windows-x64","latest",null,selection);
        var config=new LauncherConfig {DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo"};
        context.Pin(config);
        Assert.Equal("exact",config.VersionPolicy);Assert.Equal("1.0.0",config.RequestedVersion);Assert.Equal(managed,config.IsManagedDeployment);
        Assert.Throws<InvalidDataException>(()=>context.Pin(new LauncherConfig {ProjectId="another",DeploymentMode=config.DeploymentMode}));
        Assert.Throws<InvalidDataException>(()=>context.Pin(new LauncherConfig {ProjectId="demo",DeploymentMode=managed?"portable":"managed-agent"}));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void ResultMustBelongToThePinnedRelease(bool managed)
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","1");
        var context=new LauncherUiOperationContext(managed,"demo","prod","stable","windows-x64","exact","1",selection);
        var config=new LauncherConfig {DeploymentMode=managed?"managed-agent":"portable",ProjectId="demo",TargetPlatform="windows-x64",SelectedRelease=selection};
        var result=new LauncherUiOperationResult(config,selection,new(true,"1","1",false,0,0,false),new(RuntimeState.Quiescent,"stopped","stopped"));
        context.Validate(result);
        Assert.Throws<InvalidDataException>(()=>context.Validate(result with {Selection=selection with {Version="2"}}));
        config.SelectedRelease=selection with {Version="2"};
        Assert.Throws<InvalidDataException>(()=>context.Validate(result));
    }

    [Theory][InlineData(false,true,LauncherTroubleshootAction.OfferInstall)][InlineData(true,false,LauncherTroubleshootAction.Complete)][InlineData(true,true,LauncherTroubleshootAction.Repair)]
    internal void TroubleshootOnlyRepairsAnExistingInstallation(bool installed,bool required,LauncherTroubleshootAction expected)
    {
        Assert.Equal(expected,LauncherUiOperations.TroubleshootAction(new(installed,installed?"1":null,"2",required,1,0,true)));
    }

    [Theory][InlineData(RuntimeState.Running)][InlineData(RuntimeState.LaunchPending)][InlineData(RuntimeState.Unknown)]
    public void BothModesBlockAfterObservedRuntime(RuntimeState state)
    {
        foreach(var mode in new[]{"portable","managed-agent"})
        {
            var model=new LauncherDashboardViewModel {Config=new(){DeploymentMode=mode}};
            model.ApplyProjectStatus(new(true,"1","1",false,0,0,false));
            Assert.False(model.ApplyRuntimeObservation(new(state,"tracked","tracked")));
            Assert.Equal(PrimaryActionKind.Disabled,model.PrimaryAction);
        }
    }
}
