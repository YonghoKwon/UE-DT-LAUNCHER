using UeDtLauncher.Distribution.Authority;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class CatalogSequenceAllocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sequence-reservation-" + Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    public CatalogSequenceAllocatorTests() => store = new(new() { Root=root });
    public void Dispose() { Directory.Delete(root,true); }
    private long HighWater() => FileAuthorityFixtures.HighWater(store);

    [Fact]
    public async Task ConcurrentNumbersAreUniqueAndDurablyReservedBeforeUse()
    {
        var allocator = new CatalogSequenceAllocator(store);
        var values = await Task.WhenAll(Enumerable.Range(0,300).Select(_=>Task.Run(()=>allocator.NextAsync(default))));
        Assert.Equal(300, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1,300).Select(i=>(long)i),values.Order());
        Assert.True(HighWater()>=values.Max()); Assert.InRange(HighWater()-values.Max(),0,63);
        var restarted = new CatalogSequenceAllocator(new(store.Settings));
        Assert.True(await restarted.NextAsync(default)>values.Max());
    }
    [Fact]
    public async Task FailedCommitDoesNotIssueOrCacheAnUnreservedNumber()
    {
        var allocator = new CatalogSequenceAllocator(store);
        AuthorityDisk.Boundary.Value = FileAuthorityFixtures.FailBeforeHead;
        try { await Assert.ThrowsAsync<IOException>(() => allocator.NextAsync(default)); }
        finally { AuthorityDisk.Boundary.Value = null; }
        Assert.Equal(0, HighWater());
        Assert.Equal(1,await allocator.NextAsync(default));
    }
    [Fact]
    public async Task CrashBoundaryAfterCommitLosesOnlyUnusedNumbers()
    {
        var failures = 0;
        var allocator = new CatalogSequenceAllocator(store,64,()=>{if(Interlocked.Increment(ref failures)==1)throw new IOException("synthetic after-commit failure");});
        await Assert.ThrowsAsync<IOException>(()=>allocator.NextAsync(default)); Assert.Equal(64,HighWater());
        Assert.Equal(65,await allocator.NextAsync(default));
        var restarted = new CatalogSequenceAllocator(new(store.Settings));
        Assert.Equal(129,await restarted.NextAsync(default));
    }
    [Fact]
    public async Task CancellationAndOverflowNeverResetTheSequence()
    {
        var allocator = new CatalogSequenceAllocator(store);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>allocator.NextAsync(cancelled.Token));Assert.Equal(0,HighWater());
        FileAuthorityFixtures.SetSequence(store, long.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(()=>allocator.NextAsync(default));Assert.Equal(long.MaxValue,HighWater());
        FileAuthorityFixtures.SetSequence(store, 1.5);
        await Assert.ThrowsAsync<InvalidDataException>(()=>allocator.NextAsync(default));
    }
}
