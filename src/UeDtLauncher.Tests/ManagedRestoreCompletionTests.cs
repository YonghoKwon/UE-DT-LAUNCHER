using Xunit;
namespace UeDtLauncher.Tests;

public class ManagedRestoreCompletionTests
{
    [Fact]
    public async Task InspectionFailureDoesNotTurnCommittedRestoreIntoApplyFailure()
    {
        var path=Path.Combine(Path.GetTempPath(),"uedt-restored-"+Guid.NewGuid().ToString("N"));
        try
        {
            var applies=0;
            var result=await ManagedRestoreCompletion.RunAsync(()=>{applies++;File.WriteAllText(path,"restored");return Task.CompletedTask;},()=>throw new IOException("post-restore read failure"));
            Assert.True(result.InspectionUnavailable);Assert.Null(result.Status);Assert.Equal(1,applies);Assert.Equal("restored",File.ReadAllText(path));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public async Task ApplicationFailureNeverClaimsCommitAndNeverInspects()
    {
        var inspected=false;
        await Assert.ThrowsAsync<IOException>(()=>ManagedRestoreCompletion.RunAsync(()=>throw new IOException("apply failed"),()=>{inspected=true;return Task.FromResult(new ManagedProjectStatus(false,null,null,true,0,0,false));}));
        Assert.False(inspected);
    }
}
