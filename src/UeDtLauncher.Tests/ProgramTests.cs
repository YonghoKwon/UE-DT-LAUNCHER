using Xunit;

namespace UeDtLauncher.Tests;

public class ProgramTests
{
    [Theory]
    [InlineData(true, null, null, true)]      // Linux, no display at all -> unavailable
    [InlineData(true, ":0", null, false)]     // Linux, X11 display present -> available
    [InlineData(true, null, "wayland-0", false)] // Linux, Wayland present -> available
    [InlineData(false, null, null, false)]    // non-Linux -> always available
    public void GuiUnavailable_ReflectsDisplayEnvironment(bool isLinux, string? display, string? wayland, bool expected)
    {
        Assert.Equal(expected, Program.GuiUnavailable(isLinux, display, wayland));
    }

    [Fact]
    public void CliArgs_WhenNotCli_ReturnsArgsUnchanged()
    {
        var args = new[] { "run", "--config", "launcher.config.json" };
        Assert.Equal(args, Program.CliArgs(args, wantsCli: false));
    }

    [Fact]
    public void CliArgs_WithCliAndNoSubcommand_PrependsRun()
    {
        var args = new[] { "--cli", "--config", "launcher.config.json" };
        Assert.Equal(new[] { "run", "--config", "launcher.config.json" }, Program.CliArgs(args, wantsCli: true));
    }

    [Fact]
    public void CliArgs_WithCliAndKnownSubcommand_UsesArgsAsIs()
    {
        var args = new[] { "--cli", "service", "--once" };
        Assert.Equal(new[] { "service", "--once" }, Program.CliArgs(args, wantsCli: true));
    }

    [Fact]
    public void GuiArgs_StripsLeadingGuiTokenAndGuiFlag()
    {
        var args = new[] { "gui", "--gui", "--something" };
        Assert.Equal(new[] { "--something" }, Program.GuiArgs(args));
    }

    [Fact]
    public void GuiArgs_StripsGuiFlagWithoutLeadingGuiToken()
    {
        var args = new[] { "--gui", "extra" };
        Assert.Equal(new[] { "extra" }, Program.GuiArgs(args));
    }
}
