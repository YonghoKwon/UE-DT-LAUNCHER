using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class VisualDesignTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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

    [Fact]
    public void ProjectVisual_ResolvesValidConfigRelativeImage()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "config", "launcher.config.json");
        var imagePath = Path.Combine(temp.Path, "config", "assets", "hero.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, OnePixelPng);
        var project = new ProjectUiConfig
        {
            ProjectId = "digital-twin",
            DisplayName = "Digital Twin",
            HeroPath = "assets/hero.png"
        };

        var asset = ProjectVisualResolver.Resolve(project, configPath, ProjectVisualKind.Hero);

        Assert.True(asset.HasImage);
        Assert.Equal(imagePath, asset.ResolvedPath);
        Assert.Equal("DT", asset.Initials);
    }

    [Fact]
    public void ProjectVisual_FallsBackForMissingCorruptAndOversizedAssets()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "launcher.config.json");
        var missing = new ProjectUiConfig { ProjectId = "missing", DisplayName = "Missing", HeroPath = "missing.png" };
        var corruptPath = Path.Combine(temp.Path, "corrupt.png");
        File.WriteAllText(corruptPath, "not an image");
        var corrupt = new ProjectUiConfig { ProjectId = "corrupt", DisplayName = "Corrupt", HeroPath = corruptPath };
        var hugePath = Path.Combine(temp.Path, "huge.png");
        using (var stream = File.Create(hugePath)) stream.SetLength(ProjectVisualResolver.MaxAssetBytes + 1);
        var huge = new ProjectUiConfig { ProjectId = "huge", DisplayName = "Huge", HeroPath = hugePath };

        Assert.Equal("missing", ProjectVisualResolver.Resolve(missing, configPath, ProjectVisualKind.Hero).FailureReason);
        Assert.Equal("invalid-image", ProjectVisualResolver.Resolve(corrupt, configPath, ProjectVisualKind.Hero).FailureReason);
        Assert.Equal("too-large", ProjectVisualResolver.Resolve(huge, configPath, ProjectVisualKind.Hero).FailureReason);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-visual-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
