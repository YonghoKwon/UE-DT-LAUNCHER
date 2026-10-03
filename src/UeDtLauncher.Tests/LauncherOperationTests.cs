using Xunit;

namespace UeDtLauncher.Tests;

public sealed class LauncherOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "operations-" + Guid.NewGuid().ToString("N"));
    private readonly RuntimeIdentity owner = new(1, "created", "host", "owner", "session", false);
    private static ReleaseSelection Release => new("demo", "prod", "stable", "windows-x64", "1.0.0");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public void StatusIsReadOnlyAndUnknownIdsDoNotCreateDirectories()
    {
        var registry = new OperationRegistry(root);
        Assert.Throws<InvalidDataException>(() => registry.Inspect(Guid.NewGuid().ToString("N"), owner));
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void CancelRequiresActualOwnerSessionAndDoesNotMeanFinished()
    {
        var registry = new OperationRegistry(root); var id = Guid.NewGuid().ToString("N");
        using var operation = registry.Begin(id, owner, Release);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Cancel(id, owner with { Owner = "another" }));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Cancel(id, owner with { Session = "another" }));
        Assert.False(operation.Token.IsCancellationRequested);
        Assert.Equal("Cancelling", registry.Cancel(id, owner).Phase);
        Assert.True(operation.Token.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => registry.Discard(id, owner));
        operation.Finish(false);
        Assert.Equal("Cancelled", registry.Cancel(id, owner).Phase);
    }
    [Fact]
    public void CommittedCancellationRemainsCompletedAndNeverTerminatesPayload()
    {
        var registry = new OperationRegistry(root); var id = Guid.NewGuid().ToString("N");
        using var operation = registry.Begin(id, owner, Release);
        registry.Cancel(id, owner); operation.Finish(true);
        Assert.Equal("Completed", registry.Inspect(id, owner).Phase);
    }
    [Fact]
    public void RestartNeverConvertsActiveRecordsIntoSuccessAndSelectionIsPinned()
    {
        var registry = new OperationRegistry(root); var id = Guid.NewGuid().ToString("N");
        using (var operation = registry.Begin(id, owner, Release))
        {
            operation.Phase("Applying");
            Assert.Throws<InvalidDataException>(() => operation.Bind(Release with { Version = "2.0.0" }));
            Assert.Throws<InvalidOperationException>(() => registry.Begin(id, owner, Release));
        }
        Assert.Equal("Interrupted", new OperationRegistry(root).Inspect(id, owner).Phase);
        Assert.Equal("Discarded", registry.Discard(id, owner).Phase);
    }
    [Fact]
    public void SaveFailureKeepsPreviousRecordAndDoesNotAcknowledgeCancellation()
    {
        var registry = new OperationRegistry(root); var id = Guid.NewGuid().ToString("N");
        using var operation = registry.Begin(id, owner, Release);
        var record = Path.Combine(root, id + ".json");
        using var held = new FileStream(record, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (OperatingSystem.IsWindows())
        {
            var error = Record.Exception(() => registry.Cancel(id, owner));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(operation.Token.IsCancellationRequested);
            Assert.Equal("Pending", operation.Status.Phase);
        }
    }
    [Theory]
    [InlineData("../secret")]
    [InlineData("not-a-guid")]
    public void ControlRejectsUnboundedOrPathInputs(string id)
    {
        Assert.NotNull(ManagedAgentProtocol.Validate(new() { Command = "operation-cancel", OperationId = id }));
    }
    [Fact]
    public void MissingOrDuplicateFieldsNeverAuthorizeResumeOrNormalStatus()
    {
        var registry=new OperationRegistry(root);var id=Guid.NewGuid().ToString("N");
        using(var operation=registry.Begin(id,owner,Release)){operation.Finish(true);}
        var path=Path.Combine(root,id+".json");var original=File.ReadAllText(path);
        File.WriteAllText(path,original.Replace("\"command\": \"update\"","\"command\": null"));
        Assert.Throws<InvalidDataException>(()=>registry.Inspect(id,owner));
        File.WriteAllText(path,original.Replace("\"schemaVersion\": 1","\"schemaVersion\": 1, \"schemaVersion\": 1"));
        Assert.Throws<InvalidDataException>(()=>registry.Inspect(id,owner));
    }
    [Fact]
    public async Task ReadersDoNotBlockAtomicCompletionReplacement()
    {
        var registry=new OperationRegistry(root);var id=Guid.NewGuid().ToString("N");using var operation=registry.Begin(id,owner,Release);
        operation.Bind(Release,new string('a',64));
        Assert.Throws<InvalidDataException>(()=>operation.Bind(Release,new string('b',64)));
        using var stop=new CancellationTokenSource();
        var reader=Task.Run(()=>{while(!stop.IsCancellationRequested)registry.Inspect(id,owner);});
        try{for(var i=0;i<50;i++)operation.Phase(i%2==0?"Downloading":"Applying");operation.Finish(true);}
        finally{stop.Cancel();await reader;}
        Assert.Equal("Completed",registry.Inspect(id,owner).Phase);
    }
    [Fact]
    public void ExplicitDiscardArchivesHistoryAndRestoresCapacityWithoutReusingIds()
    {
        var registry=new OperationRegistry(root){RecordLimit=1};var first=Guid.NewGuid().ToString("N");
        using(var operation=registry.Begin(first,owner,Release)){operation.Finish(true);}
        Assert.Throws<IOException>(()=>registry.Begin(Guid.NewGuid().ToString("N"),owner,Release));
        registry.Discard(first,owner);Assert.Equal("Discarded",registry.Inspect(first,owner).Phase);
        Assert.Throws<InvalidOperationException>(()=>registry.Begin(first,owner,Release));
        using var next=registry.Begin(Guid.NewGuid().ToString("N"),owner,Release);
        Assert.Throws<InvalidOperationException>(()=>registry.Discard(next.Status.Id,owner));
    }
}
