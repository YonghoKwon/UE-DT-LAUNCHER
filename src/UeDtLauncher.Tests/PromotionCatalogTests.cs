using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class PromotionCatalogTests
{
    private static PublishedRelease Release(string version, string environment = "prod")
    {
        var m = new ReleaseSidecar { ProjectId = "demo", DisplayName = "Demo", Environment = environment, Channel = "stable", Platform = "windows-x64", Version = version };
        return new(m.ReleaseId, version, "unused-without-assets", m);
    }
    private static PromotionEvent Event(long n, PublishedRelease r) => new(n, ReleasePromotions.Track(new(r.Metadata.ProjectId, r.Metadata.Environment, r.Metadata.Channel, r.Metadata.Platform, r.Metadata.Version)), r.ReleaseId, null, "test", "now", "reason", "manual");
    [Fact]
    public void NewCatalogContainsApprovedButRecommendsOnlyLatestAllowedPromotion()
    {
        var a = Release("1"); var b = Release("2"); var c = Release("9"); var snapshot = new PromotionSnapshot(true, [a,b,c], [Event(1,a),Event(2,b)]);
        var all = PromotionCatalog.Build(snapshot, [a,b,c], true, "https://updates", new(), 1, DateTimeOffset.UtcNow);
        Assert.Equal("explicit-promotion-v1", all.SelectionPolicy); Assert.Equal(3, all.Projects[0].Releases.Count);
        Assert.Equal("2", Assert.Single(all.Projects[0].Releases, r => r.IsLatest).Version);
        var limited = PromotionCatalog.Build(snapshot, [a,c], true, "https://updates", new(), 2, DateTimeOffset.UtcNow);
        Assert.Equal("1", Assert.Single(limited.Projects[0].Releases, r => r.IsLatest).Version);
    }
    [Fact]
    public void LegacyCatalogCannotExposeUnpromotedFallback()
    {
        var a = Release("1"); var b = Release("9"); var snapshot = new PromotionSnapshot(true, [a,b], [Event(1,a)]);
        var catalog = PromotionCatalog.Build(snapshot, [a,b], false, "https://updates", new(), 1, DateTimeOffset.UtcNow);
        Assert.Equal("1", Assert.Single(catalog.Projects[0].Releases).Version);
        var none = PromotionCatalog.Build(snapshot, [b], false, "https://updates", new(), 2, DateTimeOffset.UtcNow);
        Assert.Empty(none.Projects);
    }
    [Fact]
    public void NoPromotionDoesNotRecommendOrSelectProjectImage()
    {
        var a = Release("1"); var c = PromotionCatalog.Build(new(true, [a], []), [a], true, "https://updates", new(), 1, DateTimeOffset.UtcNow);
        Assert.False(c.Projects[0].Releases[0].IsLatest); Assert.Null(c.Projects[0].Hero); Assert.Null(c.Projects[0].Thumbnail);
    }
    [Fact]
    public void ExplicitOlderPromotionAndTracksHaveIndependentRecommendations()
    {
        var a = Release("1"); var b = Release("2"); var d = Release("3", "dev");
        var c = PromotionCatalog.Build(new(true, [a,b,d], [Event(1,b),Event(2,d),Event(3,a)]), [a,b,d], true, "https://updates", new(), 1, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "1", "3" }, c.Projects[0].Releases.Where(r => r.IsLatest).Select(r => r.Version).ToArray());
    }
}
