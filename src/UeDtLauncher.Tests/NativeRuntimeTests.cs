using Xunit;

namespace UeDtLauncher.Tests;

public class NativeRuntimeTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("x\\", "x\\")]
    [InlineData("/c", "/c")]
    public void WindowsArgumentQuotingPreservesBoundaries(string input, string expected) => Assert.Equal(expected, NativeProcessFamily.QuoteWindows(input));

    [Fact]
    public void InvalidLaunchCannotStartAnything() => Assert.Throws<InvalidDataException>(() => NativeProcessFamily.Run(new("relative", ".", []), _ => throw new Exception("must not start")));
}
