using Xunit;

namespace UeDtLauncher.Tests;
public sealed class ScheduledCheckTests
{
    [Theory]
    [InlineData("server-unavailable",2)]
    [InlineData("service-unavailable",2)]
    [InlineData("authentication-failed",1)]
    [InlineData("access-denied",1)]
    [InlineData("integrity-failed",1)]
    public async Task FailedScheduleIsBoundedAndNeverCreatesInstallation(string code,int expectedAttempts)
    {
        var root=Path.Combine(Path.GetTempPath(),"check-fault-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"config.json");await JsonFiles.WriteAsync(path,new LauncherConfig{InstallDir="app",LogDir="logs",StateRootDir="state",ScheduledCheck=new(){Enabled=true,IntervalSeconds=3600}});
            var attempts=0;var inspections=0;var delays=0;var hash=await Hashing.Sha256FileAsync(path);
            var result=await ScheduledChecks.RunCoreAsync(path,(_,_)=>{attempts++;throw new AgentOperationException(code,"support","owned failure");},
                (_,_,_)=>{inspections++;throw new InvalidOperationException("Unexpected inspection");},_=>{delays++;return Task.CompletedTask;});
            Assert.Equal(expectedAttempts,attempts);Assert.Equal(expectedAttempts-1,delays);Assert.Equal(0,inspections);
            Assert.Equal("failed",result.Status);Assert.Equal(code,result.FailureCode);Assert.Equal(hash,await Hashing.Sha256FileAsync(path));
            Assert.False(Directory.Exists(Path.Combine(root,"app")));Assert.False(Directory.Exists(Path.Combine(root,"state")));
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public async Task CancellationDuringBoundedRetryPreservesPriorObservationAndReleasesLease()
    {
        var root=Path.Combine(Path.GetTempPath(),"check-cancel-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"config.json");await JsonFiles.WriteAsync(path,new LauncherConfig{InstallDir="app",LogDir="logs",StateRootDir="state",ScheduledCheck=new(){Enabled=true,IntervalSeconds=3600}});
            Directory.CreateDirectory(Path.Combine(root,"logs"));var report=Path.Combine(root,"logs/scheduled-check.json");await File.WriteAllTextAsync(report,"previous observation");
            using var cancel=new CancellationTokenSource();var delayed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var attempts=0;
            var pending=ScheduledChecks.RunCoreAsync(path,(_,_)=>{attempts++;throw new AgentOperationException("server-unavailable","support","owned failure");},
                (_,_,_)=>throw new InvalidOperationException("Unexpected inspection"),async ct=>{delayed.SetResult();await Task.Delay(Timeout.Infinite,ct);},cancel.Token);
            await delayed.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.Equal(1,attempts);Assert.Equal("previous observation",await File.ReadAllTextAsync(report));
            Assert.False(Directory.Exists(Path.Combine(root,"app")));Assert.False(Directory.Exists(Path.Combine(root,"state")));
            Assert.True(SingleInstanceLock.TryAcquire(Path.Combine(root,"logs/scheduled-check.lock"),out var lease));lease!.Dispose();
        }finally{Directory.Delete(root,true);}
    }
    [Theory][InlineData("action-required",true,"action-required")][InlineData("verification-pending",true,"verification-pending")][InlineData("checks-passed",false,"verification-pending")][InlineData("checks-passed",true,"checked")]
    public void HealthyDoesNotReplacePreparationOrInspection(string preparation,bool inspected,string expected)
    {
        var report=new DoctorReport("now",true,"1","test",[]){PreparationState=preparation};Assert.Equal(expected,ScheduledChecks.Outcome(report,inspected));
    }
    [Fact]
    public async Task DisabledDefaultDoesNotCreateInstallationOrLogs()
    {
        var root=Path.Combine(Path.GetTempPath(),"check-only-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"config.json");await JsonFiles.WriteAsync(path,new LauncherConfig{InstallDir="app",LogDir="logs",StateRootDir="state"});
            var before=Directory.GetFileSystemEntries(root);var result=await ScheduledChecks.RunOnceAsync(path);
            Assert.Equal("disabled",result.Status);Assert.Equal(before,Directory.GetFileSystemEntries(root));
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void EnabledWithoutExplicitIntervalIsRejected()
    {
        var config=new LauncherConfig{ScheduledCheck=new(){Enabled=true}};
        Assert.Throws<InvalidOperationException>(()=>LauncherConfigValidator.Validate(config));
    }
    [Fact]
    public async Task OverlappingCheckIsSkippedAndNeverCreatesPayload()
    {
        var root=Path.Combine(Path.GetTempPath(),"check-busy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"config.json");await JsonFiles.WriteAsync(path,new LauncherConfig{InstallDir="app",LogDir="logs",StateRootDir="state",ScheduledCheck=new(){Enabled=true,IntervalSeconds=3600}});
            using var held=SingleInstanceLock.Acquire(Path.Combine(root,"logs/scheduled-check.lock"));
            Assert.Equal("skipped-busy",(await ScheduledChecks.RunOnceAsync(path)).Status);Assert.False(Directory.Exists(Path.Combine(root,"app")));
            Assert.False(Directory.Exists(Path.Combine(root,"state")));
        }finally{Directory.Delete(root,true);}
    }
}
