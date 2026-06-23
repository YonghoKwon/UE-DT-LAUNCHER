using Xunit;

namespace UeDtLauncher.Tests;

public class SingleInstanceLockTests
{
    [Fact]
    public void TryAcquire_SecondAcquireFails_UntilDisposed()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "uedt-lock-" + Guid.NewGuid().ToString("N"));

        Assert.True(SingleInstanceLock.TryAcquire(lockPath, out var first));
        Assert.False(SingleInstanceLock.TryAcquire(lockPath, out var second));
        Assert.Null(second);

        first!.Dispose();
        Assert.True(SingleInstanceLock.TryAcquire(lockPath, out var third));
        third!.Dispose();
    }

    [Fact]
    public void Acquire_WhenHeld_ThrowsFriendlyError()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), "uedt-lock-" + Guid.NewGuid().ToString("N"));
        using var held = SingleInstanceLock.Acquire(lockPath);
        var ex = Assert.Throws<InvalidOperationException>(() => SingleInstanceLock.Acquire(lockPath));
        Assert.Contains("Another launcher instance", ex.Message);
    }

    [Fact]
    public void LockPathFor_DerivesFromInstallDir()
    {
        var path = SingleInstanceLock.LockPathFor(Path.Combine(Path.GetTempPath(), "apps", "demo") + Path.DirectorySeparatorChar);
        Assert.EndsWith("demo.launcher-lock", path);
    }
}
