using Xunit;

namespace UeDtLauncher.Tests;
public sealed class ManagedOperationCoordinatorTests
{
    [Fact]
    public async Task PreCancelledNeverDispatches()
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();var calls=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ManagedOperationCoordinator.RunAsync("id",_=>{calls++;return Task.FromResult(new ManagedAgentResponse());},()=>Task.FromResult(new ManagedAgentResponse()),_=>{},cancel.Token));
        Assert.Equal(0,calls);
    }
    [Fact]
    public async Task CancellationWaitsForRegistrationAndThenForSafeTerminal()
    {
        using var cancel=new CancellationTokenSource();Action<ManagedAgentProgress>? receive=null;
        var terminal=new TaskCompletionSource<ManagedAgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var work=ManagedOperationCoordinator.RunAsync("id",callback=>{receive=callback;return terminal.Task;},()=>{calls++;acknowledged.SetResult();return Task.FromResult(new ManagedAgentResponse{Success=true});},_=>{},cancel.Token);
        cancel.Cancel();Assert.Equal(0,calls);Assert.False(work.IsCompleted);
        receive!(new("OperationRegistered","id",0));await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(work.IsCompleted);
        terminal.SetResult(new(){Status="Cancelled",Operation=new(1,"id","owner","session",null,"Cancelled",true,"now")});
        Assert.Equal("Cancelled",(await work).Status);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task RejectedCancelIsNotReportedAsAcknowledged()
    {
        using var cancel=new CancellationTokenSource();var terminal=new TaskCompletionSource<ManagedAgentResponse>();
        var started=new TaskCompletionSource();
        var work=ManagedOperationCoordinator.RunAsync("id",callback=>{callback(new("OperationRegistered","id",0));return terminal.Task;},()=>{started.SetResult();return Task.FromResult(new ManagedAgentResponse{Success=false,Status="operation-rejected"});},_=>{},cancel.Token);
        cancel.Cancel();await started.Task.WaitAsync(TimeSpan.FromSeconds(3));terminal.SetResult(new(){CorrelationId="support",Status="unknown"});
        await Assert.ThrowsAsync<AgentOperationException>(()=>work);
    }
}
