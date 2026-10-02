using Xunit;

namespace UeDtLauncher.Tests;
public sealed class ScheduledCheckTests
{
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
