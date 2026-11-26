using System.Text;
using System.Text.Json;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using Xunit.Abstractions;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class AuthorityReadAndAuditTests(ITestOutputHelper output)
{
    [Fact]
    public async Task G5_EightReadersReadTenThousandConsistentSnapshotsEach()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        Committed(store.Commit(snapshot => SetCounters(snapshot, 1)));
        using var ready = new CountdownEvent(9);
        using var start = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var writer = Worker(() =>
        {
            var count = 0;
            while (!stop.IsCancellationRequested)
            {
                var committed = Committed(store.Commit(snapshot => SetCounters(snapshot, checked((int)snapshot.Generation + 1))));
                Assert.Equal(++count + 1, committed.Generation);
            }
            return count;
        });
        var readers = Enumerable.Range(0, 8).Select(_ => Worker(() =>
        {
            var observedNew = 0;
            for (var index = 0; index < 10_000; index++)
            {
                var snapshot = store.ReadSnapshot();
                var catalog = snapshot.Get(AuthorityArea.Catalog).GetProperty("counter").GetInt32();
                var jobs = snapshot.Get(AuthorityArea.Jobs).GetProperty("counter").GetInt32();
                Assert.Equal(snapshot.Generation, catalog);
                Assert.Equal(catalog, jobs);
                if (snapshot.Generation > 1) observedNew++;
            }
            return observedNew;
        })).ToArray();
        int commits;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(20)));
            start.Set();
            var observations = await Task.WhenAll(readers).WaitAsync(TimeSpan.FromMinutes(5));
            Assert.All(observations, count => Assert.True(count > 0));
        }
        finally
        {
            stop.Cancel();
            start.Set();
            commits = await writer.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.True(commits > AuthorityStore.PriorGenerationsToKeep);
        output.WriteLine($"8 × 10,000 successful reads, zero mixed generations, {commits} concurrent commits.");

        Task<int> Worker(Func<int> work) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Concurrent workers did not start.");
            return work();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    [Theory]
    [InlineData("torn-tail")]
    [InlineData("middle-corruption")]
    [InlineData("n-plus-one")]
    [InlineData("partial-batch")]
    [InlineData("idempotent")]
    public void G6_JsonlProjectsAuthorityExactly(string scenario)
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        for (var index = 0; index < 12; index++)
            Committed(store.Commit(_ => CommitPlan.Audit(Event($"single-{index}"))));
        var result = Committed(store.Commit(_ => CommitPlan.Audit(Event("batch-a"), Event("batch-b"), Event("batch-c"))));
        var path = store.DataPath("audit.jsonl");
        var expected = File.ReadAllBytes(path);
        var lines = File.ReadAllLines(path);
        switch (scenario)
        {
            case "torn-tail":
                File.AppendAllText(path, "{\"generation\":13");
                break;
            case "middle-corruption":
                lines[5] = "[invalid JSON";
                File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
                Assert.True(store.HasAudit(store.ReadSnapshot(), "audit", "single-5"));
                break;
            case "partial-batch":
                var partial = string.Join('\n', lines[..^2]) + "\n" + lines[^2][..20];
                File.WriteAllText(path, partial, new UTF8Encoding(false));
                Assert.Equal(3, store.ReadAudit(store.ReadSnapshot()).Count(entry => entry.Generation == result.Generation));
                break;
            case "idempotent":
                lines[5] = lines[2]; // Valid JSON at a regressing position.
                File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
                break;
        }
        var reopened = fixture.Open();
        Assert.Null(reopened.OpenMaintenanceError);
        Assert.Equal(expected, File.ReadAllBytes(path));
        var projected = ReadProjection(reopened);
        Assert.Equal(reopened.ReadAudit(reopened.ReadSnapshot()), projected);
        Assert.Equal(15, projected.Length);
        var batch = projected.Where(entry => entry.Generation == result.Generation).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, batch.Select(item => item.Ordinal));
        Assert.Single(batch.Select(item => item.TxId).Distinct());
        if (scenario == "idempotent")
        {
            using var writer = reopened.Lock();
            for (var recovery = 0; recovery < 3; recovery++)
            {
                Assert.Null(reopened.Recover(writer));
                Assert.Equal(expected, File.ReadAllBytes(path));
            }
        }
        output.WriteLine($"{scenario}: 15 events projected exactly, generation 13 has ordinals 0–2.");
    }

    [Fact]
    public void G7_OpenHeadReaderDoesNotBlockReplacementAndDeferredCleanup()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        var oldHead = File.ReadAllBytes(store.DataPath("HEAD"));
        var dataPath = store.DataPath("catalog/000000.json");
        using (var headReader = AuthorityDisk.OpenReader(store.DataPath("HEAD")))
        using (var cleanupBlocker = File.Open(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (var generation = 1; generation <= 9; generation++)
                Assert.Equal(generation, Committed(store.Commit(snapshot => SetCounters(snapshot, generation))).Generation);
            Assert.Equal(9, store.ReadSnapshot().Generation);
            var pinned = new byte[headReader.Length];
            headReader.ReadExactly(pinned);
            Assert.Equal(oldHead, pinned);
            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(dataPath));
        }
        Committed(store.Commit(snapshot => SetCounters(snapshot, 10)));
        Assert.False(File.Exists(dataPath));
        Assert.True(File.Exists(store.DataPath("catalog/000002.json")));
        Assert.True(File.Exists(store.DataPath("events/000000.json")));
        output.WriteLine("The old HEAD handle retained generation 0 while new readers observed generation 9. Generation 0 data was gone after the following commit.");
    }

    [Fact]
    public async Task G7_ReaderRereadsHeadAfterOldGenerationIsCollected()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        using var pinned = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var seen = new List<long>();
        var reader = Task.Factory.StartNew(() =>
        {
            AuthorityStore.AfterReadHead.Value = generation =>
            {
                seen.Add(generation);
                if (seen.Count == 1)
                {
                    pinned.Set();
                    if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Writer did not finish.");
                }
            };
            try { return store.ReadSnapshot(); }
            finally { AuthorityStore.AfterReadHead.Value = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(pinned.Wait(TimeSpan.FromSeconds(20)));
            for (var generation = 1; generation <= 9; generation++)
                Committed(store.Commit(snapshot => SetCounters(snapshot, generation)));
            Assert.False(File.Exists(store.DataPath("catalog/000000.json")));
        }
        finally { resume.Set(); }
        Assert.Equal(9, (await reader).Generation);
        Assert.Equal(new long[] { 0, 9 }, seen);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(87)]
    public void G7_UnsupportedWindowsRenameLeavesHeadUnchanged(int nativeError)
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        var head = File.ReadAllBytes(store.DataPath("HEAD"));
        AuthorityDisk.WindowsRenameError.Value = nativeError;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var error = Assert.Throws<IOException>(() => store.Commit(_ => CommitPlan.Audit(Event("not-published"))));
                Assert.Contains("requires FileRenameInfoEx", error.Message);
                Assert.Contains($"OS error {nativeError}", error.Message);
                Assert.Equal(head, File.ReadAllBytes(store.DataPath("HEAD")));
                Assert.Empty(store.ReadAudit(store.ReadSnapshot()));
                Assert.Empty(ReadProjection(store));
            }
            else
            {
                // The Windows error seam does not change the Linux rename path.
                Assert.Equal(1, Committed(store.Commit(_ => CommitPlan.Audit(Event("linux")))).Generation);
            }
        }
        finally { AuthorityDisk.WindowsRenameError.Value = null; }
        output.WriteLine($"Windows rename error {nativeError}: Windows HEAD remains unchanged, Linux uses its own rename path.");
    }

    [Fact]
    public void PostRenameDirectoryFailureReportsPublishedGeneration()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        AuthorityDisk.Boundary.Value = point =>
        {
            if (point == AuthorityBoundary.HeadRename) throw new IOException("Injected directory flush failure.");
        };
        try
        {
            var failure = Assert.Throws<AuthorityCommitDurabilityException>(() =>
                store.Commit(_ => CommitPlan.Audit(Event("already-published"))));
            Assert.Equal(1, failure.PublishedGeneration);
            Assert.True(store.HasAudit(store.ReadSnapshot(), "audit", "already-published"));
            Assert.Empty(ReadProjection(store));
        }
        finally { AuthorityDisk.Boundary.Value = null; }
        var recovery = fixture.Open();
        Assert.Null(recovery.OpenMaintenanceError);
        Assert.Equal("already-published", Assert.Single(ReadProjection(recovery)).Job);
    }
}
