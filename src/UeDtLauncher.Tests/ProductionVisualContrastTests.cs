using Avalonia.Media;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class ProductionVisualContrastTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DashboardColorsMeetTextAndControlContrast(bool developer, bool highContrast)
    {
        var surfaces = new[]
        {
            LauncherVisualTokens.PageColor(developer, highContrast),
            LauncherVisualTokens.SurfaceColor(developer, highContrast),
            LauncherVisualTokens.MutedSurfaceColor(developer, highContrast)
        };
        var tones = Enum.GetValues<LauncherStatusTone>()
            .Select(tone => LauncherVisualTokens.StatusColor(tone, developer, highContrast))
            .ToArray();
        Assert.Equal(4, tones.Distinct().Count());

        foreach (var surface in surfaces)
        {
            Meets(LauncherVisualTokens.TextColor(developer, highContrast), surface, 4.5);
            Meets(LauncherVisualTokens.MutedTextColor(developer, highContrast), surface, 4.5);
            foreach (var tone in tones) Meets(tone, surface, 4.5);
            Meets(LauncherVisualTokens.BorderColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.FocusColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.PrimaryBorderColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.SkeletonColor(developer, highContrast), surface, 3);
        }

        Meets(Colors.White, highContrast ? Colors.Black : LauncherVisualTokens.Accent, 4.5);
        for (var variant = 0; variant < 3; variant++)
            Meets(Colors.White, LauncherVisualTokens.ProjectTileColor(variant, highContrast), 4.5);
    }

    private static void Meets(Color foreground, Color background, double minimum)
    {
        var ratio = LauncherVisualTokens.ContrastRatio(foreground, background);
        Assert.True(ratio >= minimum, $"{foreground} on {background} is {ratio:F3}:1, expected at least {minimum}:1.");
    }
}
