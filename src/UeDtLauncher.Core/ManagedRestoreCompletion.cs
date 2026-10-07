namespace UeDtLauncher;

public sealed record ManagedRestoreCompletion(ManagedProjectStatus? Status,bool InspectionUnavailable)
{
    public static async Task<ManagedRestoreCompletion> RunAsync(Func<Task> restore,Func<Task<ManagedProjectStatus>> inspect)
    {
        await restore(); // Only this boundary decides whether payload restoration has committed.
        try{return new(await inspect(),false);}
        catch(Exception){return new(null,true);}
    }
}
