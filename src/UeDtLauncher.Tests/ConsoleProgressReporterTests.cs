using Xunit;

namespace UeDtLauncher.Tests;

public class ConsoleProgressReporterTests
{
    [Fact]
    public void RenderBar_Empty_Half_Full()
    {
        Assert.Equal(new string('-', 20), ConsoleProgressReporter.RenderBar(0, 20));
        Assert.Equal(new string('#', 20), ConsoleProgressReporter.RenderBar(100, 20));

        var half = ConsoleProgressReporter.RenderBar(50, 20);
        Assert.Equal(20, half.Length);
        Assert.Equal(10, half.Count(c => c == '#'));
        Assert.Equal(10, half.Count(c => c == '-'));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(150)]
    public void RenderBar_ClampsOutOfRange(double percent)
    {
        var bar = ConsoleProgressReporter.RenderBar(percent, 20);
        Assert.Equal(20, bar.Length);
        Assert.All(bar, c => Assert.True(c == '#' || c == '-'));
    }

    [Fact]
    public void RenderBar_ZeroWidth_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConsoleProgressReporter.RenderBar(50, 0));
    }

    [Fact]
    public void FormatProgressLine_ContainsPercentFilesSpeedAndSizes()
    {
        // 34 MB of 74 MB at ~12 MB/s, file 2 of 3.
        long mb = 1024 * 1024;
        var line = ConsoleProgressReporter.FormatProgressLine(46, 2, 3, 12 * mb, 34 * mb, 74 * mb);

        Assert.Contains("46%", line);
        Assert.Contains("2/3", line);
        Assert.Contains("/s", line);          // speed token
        Assert.Contains("MB", line);          // size scale via DiskSpace.FormatBytes
        Assert.Contains("/", line);           // "done / total" pair
        Assert.Contains("[", line);           // bar wrapper
    }

    [Fact]
    public void FormatProgressLine_ClampsPercentInBar()
    {
        var line = ConsoleProgressReporter.FormatProgressLine(150, 1, 1, 0, 0, 0);
        Assert.Contains("100%", line);        // clamped
    }
}
