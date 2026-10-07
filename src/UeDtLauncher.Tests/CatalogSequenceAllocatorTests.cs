using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class CatalogSequenceAllocatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sequence-reservation-" + Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    public CatalogSequenceAllocatorTests() => store = new(new() { Root=root });
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    private long HighWater()
    { using var db=store.Open(); using var q=db.CreateCommand(); q.CommandText="SELECT value FROM sequence WHERE id=1"; return (long)q.ExecuteScalar()!; }

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
        using(var db=store.Open())using(var q=db.CreateCommand())
        { q.CommandText="CREATE TRIGGER fail_sequence BEFORE UPDATE ON sequence BEGIN SELECT RAISE(ABORT,'synthetic failure'); END";q.ExecuteNonQuery(); }
        var allocator = new CatalogSequenceAllocator(store);
        await Assert.ThrowsAsync<SqliteException>(()=>allocator.NextAsync(default)); Assert.Equal(0,HighWater());
        using(var db=store.Open())using(var q=db.CreateCommand()){q.CommandText="DROP TRIGGER fail_sequence";q.ExecuteNonQuery();}
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
        using(var db=store.Open())using(var q=db.CreateCommand()){q.CommandText="UPDATE sequence SET value=$value";q.Parameters.AddWithValue("$value",long.MaxValue);q.ExecuteNonQuery();}
        await Assert.ThrowsAsync<InvalidDataException>(()=>allocator.NextAsync(default));Assert.Equal(long.MaxValue,HighWater());
        using(var db=store.Open())using(var q=db.CreateCommand()){q.CommandText="UPDATE sequence SET value=1.5";q.ExecuteNonQuery();}
        await Assert.ThrowsAsync<InvalidDataException>(()=>allocator.NextAsync(default));
    }
}
