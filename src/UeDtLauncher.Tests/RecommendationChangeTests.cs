using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class RecommendationChangeTests
{
    [Theory]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0", "v1.0.0", false)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("build-1", "build-2", false)]
    public void RequiresALowerComparableRecommendation(string version, string? previous, bool expected)
    {
        var hint = previous is null ? null : new PreviousInstallation(Selection with { Version = previous }, "2026-11-26T00:00:00Z");
        Assert.Equal(expected, RecommendationChange.Find("latest", Catalog(version), Status(version, hint)) is not null);
    }

    [Theory]
    [InlineData("another", "prod", "stable", "windows-x64")]
    [InlineData("demo", "dev", "stable", "windows-x64")]
    [InlineData("demo", "prod", "beta", "windows-x64")]
    [InlineData("demo", "prod", "stable", "linux-x64")]
    public void IgnoresHintsForOtherTracksAndPlatforms(string project, string environment, string channel, string platform)
    {
        var hint = new PreviousInstallation(new(project, environment, channel, platform, "2.0.0"), "2026-11-26T00:00:00Z");
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0"), Status("1.0.0", hint)));
    }

    [Fact]
    public void RequiresCatalogRecommendationAndMatchingStatus()
    {
        var hint = new PreviousInstallation(Selection, "2026-11-26T00:00:00Z");
        Assert.Null(RecommendationChange.Find("latest", null, Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0", latest: false), Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("exact", Catalog("1.0.0"), Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0"), Status("1.1.0", hint)));
    }

    private static readonly ReleaseSelection Selection = new("demo", "prod", "stable", "windows-x64", "2.0.0");

    private static CatalogReleaseOption Catalog(string version, bool latest = true) => new()
    {
        ProjectId = Selection.ProjectId, Environment = Selection.Environment, Channel = Selection.Channel,
        Platform = Selection.Platform, Version = version, IsLatest = latest
    };

    private static ManagedProjectStatus Status(string version, PreviousInstallation? hint) =>
        new(false, null, version, true, 0, 0, false, hint);
}
