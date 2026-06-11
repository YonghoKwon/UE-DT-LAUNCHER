using Xunit;

namespace UeDtLauncher.Tests;

public class ServiceRunnerTests
{
    [Fact]
    public void IsUpdateAvailable_NoState_IsTrue()
    {
        Assert.True(ServiceRunner.IsUpdateAvailable(null, "1.0.0"));
    }

    [Fact]
    public void IsUpdateAvailable_SameVersion_IsFalse()
    {
        Assert.False(ServiceRunner.IsUpdateAvailable(new InstallState { Version = "1.0.0" }, "1.0.0"));
        Assert.False(ServiceRunner.IsUpdateAvailable(new InstallState { Version = "1.0.0" }, "1.0.0".ToUpperInvariant()));
    }

    [Fact]
    public void IsUpdateAvailable_DifferentVersion_IsTrue()
    {
        Assert.True(ServiceRunner.IsUpdateAvailable(new InstallState { Version = "1.0.0" }, "1.1.0"));
        // Service mode follows the catalog even when it points to an older version (intentional downgrade).
        Assert.True(ServiceRunner.IsUpdateAvailable(new InstallState { Version = "2.0.0" }, "1.0.0"));
    }
}
