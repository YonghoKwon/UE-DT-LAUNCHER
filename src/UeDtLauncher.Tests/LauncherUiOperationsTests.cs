using System.IO.Pipes;
using System.Net.Sockets;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class LauncherUiOperationsTests
{
    [Fact]
    public void KnownRejectedRestoreWithoutSelectionKeepsOriginalErrorAndSupportId()
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","2.0.0");
        var response=new ManagedAgentResponse{Success=false,Status="failed",ErrorCode="backup-preview-changed",CorrelationId="support-preview",Message="preview changed"};
        var error=Assert.Throws<AgentOperationException>(()=>LauncherRestoreResult.FromManaged(response,selection));
        Assert.Equal("backup-preview-changed",LauncherUiError.From(error).Code);Assert.Equal("support-preview",LauncherUiError.From(error).SupportId);
    }
    [Fact]
    public void UnexpectedRestoreFailureOrSuccessForOtherSelectionIsUnknown()
    {
        var selection=new ReleaseSelection("demo","prod","stable","windows-x64","2.0.0");
        Assert.Equal(LauncherRestoreDisposition.Unknown,LauncherRestoreResult.FromManaged(new(){Success=false,Status="failed",ErrorCode="storage-failed"},selection).Disposition);
        Assert.Equal(LauncherRestoreDisposition.Unknown,LauncherRestoreResult.FromManaged(new(){Success=true,SelectedRelease=selection with{Version="1.0.0"}},selection).Disposition);
    }
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviousRuntimeUsesOnlyAuthorizedCatalogAndExactRuntimeRequest(bool allowed)
    {
        var windows = OperatingSystem.IsWindows();
        var endpoint = windows ? "uedt-previous-" + Guid.NewGuid().ToString("N") :
            Path.Combine(Path.GetTempPath(), "up-" + Guid.NewGuid().ToString("N"));
        var selection = new ReleaseSelection("demo", "prod", "stable", windows ? "windows-x64" : "linux-x64", "2.0.0");
        var context = new LauncherUiOperationContext(true, "demo", "prod", "stable", selection.Platform, "exact", selection.Version, selection);
        var config = new LauncherConfig { DeploymentMode = "managed-agent", ProjectId = "demo", ClientProfile = "general" };
        var catalog = new DistributionCatalog
        {
            SelectionPolicy = CatalogRecommendation.ExplicitPolicy,
            Projects = [new() { ProjectId = "demo",
                Releases = [new() { Platform = selection.Platform, Version = allowed ? selection.Version : "1.0.0" }] }]
        };
        var commands = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = windows ? null : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (listener is not null) { listener.Bind(new UnixDomainSocketEndPoint(endpoint)); listener.Listen(2); }

        async Task ServeAsync()
        {
            for (var i = 0; i < (allowed ? 2 : 1); i++)
            {
                await using Stream stream = windows ?
                    new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous) :
                    new NetworkStream(await listener!.AcceptAsync(deadline.Token), ownsSocket: true);
                if (stream is NamedPipeServerStream pipe) await pipe.WaitForConnectionAsync(deadline.Token);
                var request = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream, deadline.Token);
                commands.Add(request.Command);
                if (i == 1) Assert.Equal(selection, request.Selection);
                await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
                {
                    CorrelationId = request.CorrelationId, Success = true, AgentCapabilities = [ManagedAgentProtocol.RuntimeCapability],
                    Catalog = i == 0 ? catalog : null,
                    Runtime = i == 1 ? new(RuntimeState.Running, "fixture", "fixture") : null
                }, deadline.Token);
            }
        }

        var server = ServeAsync();
        try
        {
            var read = LauncherUiOperations.ReadAuthorizedRuntimeAsync(context, config, deadline.Token, new ManagedAgentClient(endpoint));
            if (allowed) Assert.Equal(RuntimeState.Running, (await read)?.State);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => read);
            await server;
            Assert.Equal(allowed ? new[] { "catalog", "runtime-inspect" } : new[] { "catalog" }, commands);
        }
        finally
        {
            deadline.Cancel();
            if (!windows) File.Delete(endpoint);
        }
    }
}
