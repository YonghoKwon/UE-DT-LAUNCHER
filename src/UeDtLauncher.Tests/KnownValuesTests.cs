using Xunit;

namespace UeDtLauncher.Tests;

public class KnownValuesTests
{
    [Theory]
    [InlineData("windows-x64")]
    [InlineData("linux-x64")]
    [InlineData("WINDOWS-X64")]
    public void ValidatePlatform_AcceptsKnown(string platform)
    {
        KnownValues.ValidatePlatform(platform);
    }

    [Theory]
    [InlineData("windows-64")]
    [InlineData("windows")]
    [InlineData("win-x64")]
    [InlineData("osx-x64")]
    public void ValidatePlatform_RejectsTypos(string platform)
    {
        var ex = Assert.Throws<ArgumentException>(() => KnownValues.ValidatePlatform(platform));
        Assert.Contains("windows-x64", ex.Message);
        Assert.Contains("linux-x64", ex.Message);
    }

    [Fact]
    public void ValidateReleaseTuple_RejectsBadEnvironmentAndChannel()
    {
        Assert.Throws<ArgumentException>(() => KnownValues.ValidateEnvironment("production"));
        Assert.Throws<ArgumentException>(() => KnownValues.ValidateChannel("release"));
        KnownValues.ValidateReleaseTuple("linux-x64", "dev", "beta");
    }
}
