using System.Text.Json.Nodes;
using Xunit;
namespace UeDtLauncher.Tests;

public sealed class RuntimeValidationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"runtime-validation-"+Guid.NewGuid().ToString("N"));
    private LauncherConfig Config => new() { InstallDir=Path.Combine(root,"app"), InstallStatePath=Path.Combine(root,"state","install.json"), InstalledManifestPath=Path.Combine(root,"state","manifest.json"), AppPidPath=Path.Combine(root,"state","app.pid") };
    [Theory]
    [InlineData("schemaVersion")][InlineData("installationId")][InlineData("state")][InlineData("origin")]
    public void MissingOrNullFieldIsUnknown(string field)
    {
        var config=Config; RuntimeTestSupport.Stopped(config);
        var path=RuntimeStore.RecordPath(config); var json=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove(field); File.WriteAllText(path,json.ToJsonString());
        Assert.Equal(RuntimeState.Unknown,RuntimeStore.Observe(config).State);
        Assert.Throws<RuntimeBlockedException>(()=>InstallationMutationLease.Acquire(config));
        json[field]=null; File.WriteAllText(path,json.ToJsonString());
        Assert.Equal(RuntimeState.Unknown,RuntimeStore.Observe(config).State);
    }
    [Theory]
    [InlineData("\"state\":0,\"state\":0")][InlineData("\"state\":0,\"State\":0")]
    [InlineData("\"state\":4")][InlineData("\"state\":\"0\"")]
    public void InvalidOrDuplicateStateFailsClosed(string state)
    {
        var config=Config; RuntimeTestSupport.Stopped(config);
        File.WriteAllText(RuntimeStore.RecordPath(config),"{\"schemaVersion\":1,\"installationId\":\""+RuntimeStore.InstallationId(config)+"\",\"origin\":\"new-install\","+state+"}");
        Assert.Equal(RuntimeState.Unknown,RuntimeStore.Observe(config).State);
    }
    [Fact]
    public void DryRunAndRejectedOwnerDoNotCreateDirectories()
    {
        var config=Config;
        Directory.CreateDirectory(root); // Own the parent; /tmp itself belongs to root on Linux.
        RuntimeStore.Recover(config,RuntimeIdentities.Current(),false);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.Throws<UnauthorizedAccessException>(()=>RuntimeStore.Recover(config,RuntimeIdentities.Current() with { Owner="foreign",Administrator=false },true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Fact]
    public void IncompletePeerIdentityFailsClosed()
    {
        var config=Config;
        foreach(var field in new[] {"creationId","executable","owner","session"})
        {
            RuntimeStore.Write(config,RuntimeTestSupport.Active(config,RuntimeState.Running));
            var path=RuntimeStore.RecordPath(config);
            var json=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["requester"]![field]=null; File.WriteAllText(path,json.ToJsonString());
            Assert.Equal(RuntimeState.Unknown,RuntimeStore.Observe(config).State);
        }
    }
    [Fact]
    public void InvalidSelectionDoesNotTouchRuntime()
    {
        var config=Config;
        Assert.Throws<InvalidOperationException>(()=>RuntimeRecoveryRequest.Validate(config,true,"2.0.0"));
        Assert.Throws<InvalidOperationException>(()=>RuntimeRecoveryRequest.Validate(config,false,"2.0.0"));
        Assert.False(Directory.Exists(root));
    }
    public void Dispose() { if(Directory.Exists(root)) Directory.Delete(root,true); }
}
