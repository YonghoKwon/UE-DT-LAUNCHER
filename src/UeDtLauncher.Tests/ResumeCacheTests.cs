using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ResumeCacheTests
{
    [Fact]
    public async Task CopiesAreIndependentVerifiedAndCorruptionFallsBack()
    {
        using var fixture = new ClientPerformanceTests.Fixture(1);
        var config = fixture.Config;
        config.SelectedRelease = new("demo", "prod", "stable", "windows-x64", "1.0.0");
        config.Performance.ResumeCacheBytes = 131072;
        var document = new ManifestDocument(fixture.Manifest, JsonSerializer.Serialize(fixture.Manifest, JsonFiles.Options), true);
        var cache = ResumeCache.Open(config, document)!;
        var file = fixture.Manifest.Files[0];
        var staging = Path.Combine(config.StagingDir, file.Path); Directory.CreateDirectory(config.StagingDir);
        await File.WriteAllBytesAsync(staging, fixture.Payloads[file.Path]);
        await cache.StoreAsync(file, staging, default); File.Delete(staging);
        Assert.True(await cache.TryCopyAsync(file, staging, default));
        await File.WriteAllTextAsync(staging, "corrupt-target");
        File.Delete(staging);
        Assert.True(await cache.TryCopyAsync(file, staging, default));
        var stored = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(config.StagingDir)!, "resume-cache"), "*.verified", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(stored, "corrupt-cache"); File.Delete(staging);
        Assert.False(await cache.TryCopyAsync(file, staging, default));
    }
    [Theory]
    [InlineData(null, true)] [InlineData(0L, true)] [InlineData(1L, true)] [InlineData(131072L, false)]
    public void MissingBudgetUnsignedOrTooSmallNeverEnablesPersistentCache(long? budget, bool signed)
    {
        using var fixture = new ClientPerformanceTests.Fixture(1);
        fixture.Config.SelectedRelease = new("demo", "prod", "stable", "windows-x64", "1.0.0");
        fixture.Config.Performance.ResumeCacheBytes = budget;
        Assert.Null(ResumeCache.Open(fixture.Config, new(fixture.Manifest, "{}", signed)));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.Config.StagingDir)!, "resume-cache")));
    }
    [Fact]
    public void DifferentManifestIsNotSilentlyReusedOrEvicted()
    {
        using var fixture = new ClientPerformanceTests.Fixture(1);
        fixture.Config.SelectedRelease = new("demo", "prod", "stable", "windows-x64", "1.0.0");
        fixture.Config.Performance.ResumeCacheBytes = 131072;
        Assert.NotNull(ResumeCache.Open(fixture.Config, new(fixture.Manifest, "first", true)));
        Assert.Null(ResumeCache.Open(fixture.Config, new(fixture.Manifest, "second", true)));
    }
}
