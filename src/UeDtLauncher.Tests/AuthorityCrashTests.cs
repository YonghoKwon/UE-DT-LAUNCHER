using System.Globalization;
using System.Text.Json;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using Xunit.Abstractions;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class AuthorityCrashTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> CrashCases()
    {
        var boundaries = new[]
        {
            AuthorityBoundary.GenerationWrite, AuthorityBoundary.GenerationFlush, AuthorityBoundary.GenerationRename,
            AuthorityBoundary.GenerationDirectoryFlush, AuthorityBoundary.HeadWrite, AuthorityBoundary.HeadRename, AuthorityBoundary.AuditAppend
        };
        foreach (var boundary in boundaries)
            foreach (var kind in new[] { "mixed", "event", "area" })
                yield return [boundary.ToString(), kind];
    }

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task G2_ForcedStopThenNewProcessObservesExactGenerationAndAudit(string boundary, string kind)
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        Committed(store.Commit(snapshot => SetCounters(snapshot, 1, Event("before"))));
        var oldHead = File.ReadAllBytes(store.DataPath("HEAD"));
        var oldSnapshot = store.ReadSnapshot();
        var oldAudit = Assert.Single(store.ReadAudit(oldSnapshot));

        using (var stopped = Start(fixture.Root, "crash", boundary, kind))
        {
            Assert.Equal("BOUNDARY", await stopped.ReadLine());
            stopped.Process.Kill();
            var exit = await stopped.Finish();
            Assert.NotEqual(0, exit.ExitCode);
        }

        using var fresh = Start(fixture.Root, "reopen");
        var result = await fresh.Finish();
        Assert.True(result.ExitCode == 0, result.Error);
        using var data = JsonDocument.Parse(result.Output);
        var root = data.RootElement;
        var published = boundary is nameof(AuthorityBoundary.HeadRename) or nameof(AuthorityBoundary.AuditAppend);
        var generation = published ? 2 : 1;
        Assert.Equal(generation, root.GetProperty("generation").GetInt64());
        if (!published) Assert.Equal(oldHead, File.ReadAllBytes(store.DataPath("HEAD")));

        var areas = root.GetProperty("areas");
        foreach (var area in AuthorityDocuments.Areas)
        {
            var previous = oldSnapshot.Get(area);
            var expected = (published, kind, area) switch
            {
                (true, "mixed", AuthorityArea.Catalog or AuthorityArea.Jobs) => Set(previous, "counter", 42),
                (true, "area", AuthorityArea.Sequence) => Set(previous, "highWater", 42),
                _ => previous
            };
            Assert.Equal(expected.GetRawText(), areas.GetProperty(AuthorityDocuments.Name(area)).GetRawText());
        }
        var events = root.GetProperty("events").Deserialize<StoredAuthorityEvent[]>(AuthorityDocuments.Json)!;
        Assert.Equal(published && kind != "area" ? new[] { "before", kind } : ["before"], events.Select(entry => entry.Job));
        Assert.Equal(oldAudit, events[0]);
        if (events.Length == 2)
        {
            Assert.Equal("crash-commit", events[1].Action);
            Assert.Equal(DateTimeOffset.UnixEpoch, events[1].At);
            Assert.NotEqual(oldAudit.TxId, events[1].TxId);
        }
        Assert.Equal(events.Length, events.Select(entry => (entry.Generation, entry.Ordinal)).Distinct().Count());
        Assert.Equal(events.Select(entry => (entry.Generation, entry.Ordinal)),
            events.Select((_, index) => ((long)index + 1, 0)));
        Assert.All(Directory.EnumerateFiles(store.DirectoryPath, "*", SearchOption.AllDirectories),
            path => Assert.DoesNotContain(".tmp-", Path.GetFileName(path)));
        Assert.Equal(published, File.Exists(store.DataPath("events/000002.json")));
        Assert.Equal(generation + 1, Committed(store.Commit(_ => CommitPlan.Audit(Event("after-reopen")))).Generation);
        output.WriteLine($"{boundary}/{kind}: killed owned process, reopened generation {generation}; {events.Length} event(s).");
    }

    [Theory]
    [InlineData("GenerationWrite", 1)]
    [InlineData("GenerationFlush", 1)]
    [InlineData("GenerationRename", 1)]
    [InlineData("GenerationDirectoryFlush", 1)]
    [InlineData("GenerationWrite", 2)]
    [InlineData("GenerationFlush", 2)]
    [InlineData("GenerationRename", 2)]
    [InlineData("GenerationDirectoryFlush", 2)]
    [InlineData("HeadWrite", 1)]
    [InlineData("HeadFlush", 1)]
    [InlineData("BeforeHeadRename", 1)]
    public async Task G3_PrepublicationFaultLeavesHeadAndConcurrentReadersUnchanged(string point, int occurrence)
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        Committed(store.Commit(_ => CommitPlan.Audit(Event("before"))));
        var head = File.ReadAllBytes(store.DataPath("HEAD"));
        var boundary = Enum.Parse<AuthorityBoundary>(point);
        using var reached = new ManualResetEventSlim();
        using var readDone = new ManualResetEventSlim();
        var writer = Task.Factory.StartNew(() =>
        {
            AuthorityDisk.Boundary.Value = step =>
            {
                if (step != boundary || --occurrence != 0) return;
                reached.Set();
                if (!readDone.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Concurrent reader did not finish.");
                throw new IOException("Injected authority failure.");
            };
            try
            {
                var error = Assert.Throws<IOException>(() => store.Commit(snapshot => new(
                    new Dictionary<AuthorityArea, JsonElement>
                    {
                        [AuthorityArea.Catalog] = Set(snapshot.Get(AuthorityArea.Catalog), "counter", 42)
                    }, [Event("must-not-publish")])));
                Assert.Equal("Injected authority failure.", error.Message);
            }
            finally { AuthorityDisk.Boundary.Value = null; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(15)));
            for (var index = 0; index < 1000; index++)
            {
                var snapshot = store.ReadSnapshot();
                Assert.Equal(1, snapshot.Generation);
                Assert.False(snapshot.Get(AuthorityArea.Catalog).TryGetProperty("counter", out _));
                Assert.False(store.HasAudit(snapshot, "audit", "must-not-publish"));
            }
        }
        finally
        {
            readDone.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(head, File.ReadAllBytes(store.DataPath("HEAD")));
        Assert.Equal("before", Assert.Single(store.ReadAudit(store.ReadSnapshot())).Job);
        Assert.Equal("before", Assert.Single(ReadProjection(store)).Job);
        output.WriteLine($"{point}: observed old snapshot and audit 1,000 times while writer was paused.");
    }

    [Fact]
    public async Task G4_TwoWritersEachPublish500WithoutLostUpdates()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        using var left = Start(fixture.Root, "writer", "left");
        using var right = Start(fixture.Root, "writer", "right");
        Assert.NotEqual(left.Process.Id, right.Process.Id);
        Assert.All(await Task.WhenAll(left.ReadLine(), right.ReadLine()), line => Assert.Equal("READY", line));
        left.Process.StandardInput.WriteLine("START");
        right.Process.StandardInput.WriteLine("START");
        var outcomes = await Task.WhenAll(left.Finish(), right.Finish());
        var generations = new List<long[]>();
        foreach (var result in outcomes)
        {
            Assert.True(result.ExitCode == 0, result.Error);
            var sequence = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => long.Parse(line, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(500, sequence.Length);
            Assert.True(sequence.Zip(sequence.Skip(1)).All(pair => pair.First < pair.Second));
            generations.Add(sequence);
        }
        Assert.Equal(Enumerable.Range(1, 1000).Select(generation => (long)generation), generations.SelectMany(value => value).Order());

        var final = store.ReadSnapshot();
        Assert.Equal(1000, final.Generation);
        Assert.Equal(1000, final.Get(AuthorityArea.Catalog).GetProperty("counter").GetInt32());
        var events = store.ReadAudit(final).ToArray();
        Assert.Equal(Enumerable.Range(1, 1000).Select(generation => ((long)generation, 0)),
            events.Select(entry => (entry.Generation, entry.Ordinal)));
        Assert.Equal(events, ReadProjection(store));
        var expected = new[] { "left", "right" }.SelectMany(writerId => Enumerable.Range(0, 500).Select(index => writerId + "/" + index));
        Assert.Equal(expected.Order(), events.Select(entry => entry.Job).Order());
        output.WriteLine($"Writers {left.Process.Id}/{right.Process.Id} published 500 each. Ranges: " +
            $"{generations[0][0]}–{generations[0][^1]}/{generations[1][0]}–{generations[1][^1]}. Final counter and event count: 1,000.");
    }
}
