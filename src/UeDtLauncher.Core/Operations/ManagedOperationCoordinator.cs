namespace UeDtLauncher;

public static class ManagedOperationCoordinator
{
    public const string Capability="operation-registration-v1";
    internal static async Task<ManagedAgentResponse> RunAsync(string id,
        Func<Action<ManagedAgentProgress>,Task<ManagedAgentResponse>> dispatch,
        Func<Task<ManagedAgentResponse>> cancel,Action<ManagedAgentProgress> progress,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var registered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription=token.Register(()=>requested.TrySetResult());
        var running=dispatch(value=>
        {
            if(value.Stage=="OperationRegistered")
            {
                if(value.Message!=id)throw new InvalidDataException("Operation registration identity mismatch.");
                registered.TrySetResult();
            }
            else progress(value);
        });
        Exception? cancelError=null;
        if(await Task.WhenAny(running,requested.Task)==requested.Task && !running.IsCompleted)
        {
            if(await Task.WhenAny(running,registered.Task)==registered.Task && !running.IsCompleted)
            {
                try{var acknowledgement=await cancel();acknowledgement.ThrowIfFailed();}
                catch(Exception error){cancelError=error;}
            }
        }
        var result=await running; // Workers/recovery must finish before a terminal result is reported.
        if(cancelError is not null && result.Operation?.Phase is not ("Completed" or "Cancelled"))
            throw new AgentOperationException("cancellation-unacknowledged",result.CorrelationId,"작업 취소 상태를 확인할 수 없습니다. 상태를 다시 확인해 주세요.");
        return result;
    }
}
