using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class VisualDesignTests
{
    [Fact]
    public void LightAndDarkTextTokensMeetNormalTextContrast()
    {
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.LightText,
            LauncherVisualTokens.LightSurface) >= 4.5);
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.LightMutedText,
            LauncherVisualTokens.LightSurface) >= 4.5);
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.DarkText,
            LauncherVisualTokens.DarkSurface) >= 4.5);
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.DarkMutedText,
            LauncherVisualTokens.DarkSurface) >= 4.5);
    }

    [Fact]
    public void AccentAndStatusTokensMeetControlContrast()
    {
        Assert.True(LauncherVisualTokens.ContrastRatio(
            Avalonia.Media.Colors.White,
            LauncherVisualTokens.Accent) >= 3);
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.Success,
            LauncherVisualTokens.SuccessSoft) >= 3);
        Assert.True(LauncherVisualTokens.ContrastRatio(
            LauncherVisualTokens.Danger,
            LauncherVisualTokens.DangerSoft) >= 3);
    }
}
