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
        var other=ResumeCache.Open(fixture.Config,new(fixture.Manifest,"second",true));Assert.NotNull(other);
        Assert.NotEqual(ResumeCache.Open(fixture.Config,new(fixture.Manifest,"first",true))!.Partial(fixture.Manifest.Files[0]),other!.Partial(fixture.Manifest.Files[0]));
    }
    [Fact]
    public void TrustedRequesterPartitionsAndParallelReservationsRespectBudget()
    {
        using var fixture=new ClientPerformanceTests.Fixture(2);var config=fixture.Config;
        config.DeploymentMode="managed-agent";config.SelectedRelease=new("demo","prod","stable","windows-x64","1.0.0");config.Performance.ResumeCacheBytes=65536;
        var document=new ManifestDocument(fixture.Manifest,"signed",true);
        Assert.Null(ResumeCache.Open(config,document));
        config.TrustedOperationOwner=RuntimeIdentities.Current();var first=ResumeCache.Open(config,document)!;
        using(var reservation=first.ReservePartial(fixture.Manifest.Files[0]))
        {
            Assert.NotNull(reservation);Assert.Equal(65536,first.AccountedBytes);Assert.Null(first.ReservePartial(fixture.Manifest.Files[1]));
        }
        Assert.Equal(0,first.AccountedBytes);
        var original=first.Partial(fixture.Manifest.Files[0]);config.TrustedOperationOwner=config.TrustedOperationOwner with{Owner="another"};
        Assert.NotEqual(original,ResumeCache.Open(config,document)!.Partial(fixture.Manifest.Files[0]));
    }
    [Fact]
    public async Task CancelledCopyRemovesOwnedTemporaryAndDoesNotConsumeBudget()
    {
        using var fixture=new ClientPerformanceTests.Fixture(1);fixture.Config.SelectedRelease=new("demo","prod","stable","windows-x64","1");fixture.Config.Performance.ResumeCacheBytes=65536;
        var cache=ResumeCache.Open(fixture.Config,new(fixture.Manifest,"signed",true))!;var file=fixture.Manifest.Files[0];
        Directory.CreateDirectory(fixture.Config.StagingDir);var staging=Path.Combine(fixture.Config.StagingDir,file.Path);await File.WriteAllBytesAsync(staging,fixture.Payloads[file.Path]);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cache.StoreAsync(file,staging,cancel.Token));
        Assert.Equal(0,cache.AccountedBytes);Assert.Empty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(fixture.Config.StagingDir)!,"resume-cache"),"*.new",SearchOption.AllDirectories));
    }
}
