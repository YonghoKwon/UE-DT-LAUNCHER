using System.Security.Cryptography;
using System.Text.Json;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class IntakeAuthorityTests : IDisposable
{
    private readonly AuthorityStoreFixture fixture = new();
    private string Root => fixture.Root;

    public static IEnumerable<object[]> CrashCases() => AuthorityCrashTests.CrashCases()
        .Select(entry => entry[0]).Distinct()
        .SelectMany(boundary => new[] { "X1", "X2", "X3" }.Select(operation => new[] { boundary, operation }));

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task IntakeWritesAreAtomicAtSevenCrashBoundaries(string boundary, string operation)
    {
        var store = Open();
        var old = Job(operation == "X2" ? "failed" : "waiting");
        store.Save(old);
        var before = store.Authority.ReadSnapshot();
        var oldAudit = Assert.Single(store.Authority.ReadAudit(before));
        using (var child = Start(Root, "intake-crash", boundary, operation))
        {
            Assert.Equal("BOUNDARY", await child.ReadLine());
            child.Process.Kill();
            Assert.NotEqual(0, (await child.Finish()).ExitCode);
        }
        using var reopened = Start(Root, "intake-reopen");
        var outcome = await reopened.Finish();
        Assert.True(outcome.ExitCode == 0, outcome.Error);
        using var json = JsonDocument.Parse(outcome.Output);
        var result = json.RootElement;
        var afterPublication = boundary is nameof(AuthorityBoundary.HeadRename) or nameof(AuthorityBoundary.AuditAppend);
        Assert.Equal(before.Generation + (afterPublication ? 1 : 0), result.GetProperty("generation").GetInt64());
        var actual = result.GetProperty("jobs").Deserialize<IntakeJobs>(AuthorityDocuments.Json)!;
        var job = Assert.Single(actual.Jobs);
        var expected = afterPublication ? operation switch { "X1" => "pending", "X2" => "waiting", _ => "validating" } : old.State;
        Assert.Equal(expected, job.State);
        Assert.Equal("incoming/fixture", job.Source);
        Assert.Equal(afterPublication && operation == "X1" ? "updated" : old.Message, job.Message);
        Assert.Equal(afterPublication && operation != "X3" ? null : "processing/snapshot", job.Snapshot);
        if (afterPublication && operation == "X3")
        {
            var active = Assert.Single(actual.ActiveWork);
            Assert.Equal(job.Id, active.Job);
            Assert.NotEqual(Guid.Empty, active.Owner);
            Assert.NotEqual(Guid.Empty, active.ClaimId);
            Assert.Equal(job.ClaimId, active.ClaimId);
            Assert.Equal("processing/scratch", active.Scratch);
        }
        else
        {
            Assert.Empty(actual.ActiveWork);
            Assert.Null(job.ClaimId);
        }

        var events = result.GetProperty("events").Deserialize<StoredAuthorityEvent[]>(AuthorityDocuments.Json)!;
        Assert.Equal(afterPublication ? 2 : 1, events.Length);
        Assert.Equal(oldAudit, events[0]);
        if (afterPublication)
        {
            Assert.Equal((before.Generation + 1, 0, expected, old.Id),
                (events[1].Generation, events[1].Ordinal, events[1].Action, events[1].Job));
            Assert.NotEqual(events[0].TxId, events[1].TxId);
        }
    }

    [Fact]
    public async Task OnlyOneConcurrentClaimWins()
    {
        var store = Open();
        var another = Open();
        store.Save(Job());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> TryClaim(IntakeStore candidate)
        {
            await start.Task;
            try
            {
                using var jobLock = candidate.LockJob("fixture");
                using var claim = Claim(candidate, "waiting");
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException) { return false; }
        }
        var contenders = new[] { TryClaim(store), TryClaim(another) };
        start.SetResult();
        Assert.Single(await Task.WhenAll(contenders), won => won);
        Assert.Equal("validating", store.Get("fixture").State);
        Assert.Single(store.Authority.ReadAudit(store.Authority.ReadSnapshot()), entry => entry.Action == "validating");
    }

    [Fact]
    public void ReleaseRequiresTheOriginalOwner()
    {
        var store = Open();
        store.Save(Job());
        IntakeWorkClaim abandoned;
        using (store.LockJob("fixture")) abandoned = Claim(store, "waiting");
        using var owner = store.LockJob("fixture");
        using var replacement = Claim(store, "validating");
        var before = store.Authority.ReadSnapshot();
        var active = Assert.Single(IntakeJobs.Read(before).ActiveWork);
        abandoned.Dispose();
        var after = store.Authority.ReadSnapshot();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(active, Assert.Single(IntakeJobs.Read(after).ActiveWork));
        replacement.Dispose();
        Assert.Empty(IntakeJobs.Read(store.Authority.ReadSnapshot()).ActiveWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgressIsBoundToTheCurrentClaim(bool reclaim)
    {
        var store = Open();
        store.Save(Job());
        ThrottledPackageProgress oldProgress;
        using (store.LockJob("fixture"))
        using (var originalClaim = Claim(store, "waiting"))
        {
            oldProgress = store.Progress("fixture", originalClaim.ClaimId);
            oldProgress.Report(new("old", 1, 5, 9));
            Assert.Equal("old", store.Get("fixture").Phase);
        }
        var oldFile = Assert.Single(Directory.GetFiles(store.Authority.ProgressPath));
        var oldBytes = File.ReadAllBytes(oldFile);
        Assert.True(store.ChangeState("fixture", "validating", "failed"));
        store.Retry("fixture");
        var reset = store.Authority.ReadSnapshot().Generation;
        oldProgress.Report(new("stale", 3, 5, 7));
        Assert.Equal(reset, store.Authority.ReadSnapshot().Generation);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));
        Assert.Null(store.Get("fixture").Phase);
        if (!reclaim) return;

        using var owner = store.LockJob("fixture");
        using var claim = Claim(store, "waiting");
        var newGeneration = store.Authority.ReadSnapshot().Generation;
        var progress = store.Progress("fixture", claim.ClaimId);
        progress.Report(new("current", 2, 5, 8));
        oldProgress.Report(new("still-stale", 4, 5, 6));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));
        Assert.Equal("current", store.Get("fixture").Phase);
        Assert.Equal(newGeneration, store.Authority.ReadSnapshot().Generation);
        var newFile = Directory.GetFiles(store.Authority.ProgressPath).Single(path => path != oldFile);
        File.WriteAllText(newFile, "malformed");
        Assert.Equal("validating", store.Get("fixture").State);
        Assert.Null(store.Get("fixture").Phase);
    }

    [Fact]
    public void ProgressRecoversAfterTemporarySnapshotReadFailure()
    {
        var store = Open();
        store.Save(Job());
        using var owner = store.LockJob("fixture");
        using var claim = Claim(store, "waiting");
        var generation = store.Authority.ReadSnapshot().Generation;
        var readFailures = 0;
        AuthorityStore.AfterReadHead.Value = _ =>
        {
            readFailures++;
            throw new IOException("Temporary progress snapshot read failure.");
        };

        ThrottledPackageProgress progress;
        try
        {
            progress = store.Progress("fixture", claim.ClaimId);
            Assert.Equal(0, readFailures);
            progress.Report(new("snapshot-copy", 1, 5, 9));
        }
        finally { AuthorityStore.AfterReadHead.Value = null; }

        Assert.True(readFailures > 0);
        Assert.Empty(Directory.EnumerateFiles(store.Authority.ProgressPath));
        Assert.Null(store.Get("fixture").Phase);

        // A new phase reports immediately, regardless of the byte-update throttle.
        progress.Report(new("extract", 2, 5, 8));

        var current = store.Get("fixture");
        Assert.Equal("validating", current.State);
        Assert.Equal("extract", current.Phase);
        Assert.Equal(2, current.ProcessedBytes);
        Assert.Equal(Path.Combine(store.Authority.ProgressPath, $"fixture.{claim.ClaimId:N}.json"),
            Assert.Single(Directory.EnumerateFiles(store.Authority.ProgressPath)));
        Assert.Equal(generation, store.Authority.ReadSnapshot().Generation);
    }

    [Fact]
    public async Task CleanupUsesOneWriterAcquisitionAndKeepsTheLiveOwner()
    {
        var store = Open();
        store.Save(Job());
        var scratch = Path.Combine(Root, "processing", "scratch");
        var dead = Path.Combine(Root, "processing", "dead");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(dead);
        AddAbandonedWork(store, "stale", dead);
        using var owner = store.LockJob("fixture");
        using var claim = Claim(store, "waiting");
        var acquisitions = 0;
        AuthorityStore.BeforeAcquireWriter.Value = () => Interlocked.Increment(ref acquisitions);
        try
        {
            await Task.Run(() => StorageMaintenance.Cleanup(store, true)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, acquisitions);
        }
        finally { AuthorityStore.BeforeAcquireWriter.Value = null; }
        Assert.True(Directory.Exists(scratch));
        Assert.False(Directory.Exists(dead));
        Assert.Equal("fixture", Assert.Single(IntakeJobs.Read(store.Authority.ReadSnapshot()).ActiveWork).Job);
    }

    [Fact]
    public void PublicJobApiPreservesOrderingSourceAndStateAndReadsOneHead()
    {
        var store = Open();
        var first = Job();
        var second = first with { Id = "second", Source = Path.Combine(Root, "incoming", "second") };
        store.Save(first);
        store.Save(second);
        store.Save(first with { Source = second.Source, State = "failed", Message = "fixture rejection" });
        var reads = 0;
        var another = Open();
        AuthorityStore.AfterReadHead.Value = _ =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            another.Save(second with { State = "failed" });
        };
        List<IntakeJob> observed;
        try { observed = store.List(); }
        finally { AuthorityStore.AfterReadHead.Value = null; }
        Assert.Equal(new[] { first.Id, second.Id }, observed.Select(job => job.Id));
        Assert.Equal(new[] { "failed", "waiting" }, observed.Select(job => job.State));
        Assert.Equal(first.Source, observed[0].Source);
        Assert.Equal(observed[0], store.Find(first.Id));
        Assert.Equal("failed", store.Get(second.Id).State);
        Assert.Null(store.Find("missing"));
        Assert.Throws<InvalidOperationException>(() => store.Get("missing"));
        Assert.Equal(new[] { "waiting", "waiting", "failed", "failed" },
            store.Authority.ReadAudit(store.Authority.ReadSnapshot()).Select(entry => entry.Action));
    }

    [Fact]
    public void SavePreservesAndReplacesAllProgressFieldsWithoutAnActiveClaim()
    {
        var store = Open();
        var original = Job() with
        {
            Phase = "snapshot-copy", ProcessedBytes = 4, TotalBytes = 16,
            EstimatedAdditionalDiskBytes = 64, ProgressUpdatedAt = "2026-11-26T00:12:00.0000000+00:00"
        };
        store.Save(original);
        Assert.Equal(original, Open().Get(original.Id));
        Assert.Equal(original, Assert.Single(store.List()));
        var replacement = Job() with { TotalBytes = 1024 };
        store.Save(replacement);
        Assert.Equal(replacement, store.Get(original.Id));
        Assert.Equal(replacement, Assert.Single(Open().List()));
        store.Save(Job());
        Assert.Equal(Job(), store.Get(original.Id));
        Assert.Equal(Job(), Assert.Single(Open().List()));
        Assert.Empty(IntakeJobs.Read(store.Authority.ReadSnapshot()).ActiveWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyOrMixedRootWithoutValidHeadIsRejectedWithoutChanges(bool mixed)
    {
        var hidden = Directory.CreateDirectory(Path.Combine(Root, ".hidden-empty"));
        if (OperatingSystem.IsWindows()) hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(Root, "distribution.db"), "legacy marker");
        if (mixed)
        {
            var authority = Directory.CreateDirectory(Path.Combine(Root, "authority"));
            File.WriteAllText(Path.Combine(authority.FullName, "HEAD"), "invalid");
        }
        var before = Inventory();
        var error = Assert.Throws<AuthorityRootException>(() => Open());
        Assert.Equal(AuthorityRootKind.LegacyDatabase, error.Kind);
        Assert.Equal(before, Inventory());
    }

    [Fact]
    public void CurrentOpenRejectsMixedDatabaseFilesEvenWithAValidHead()
    {
        var store = Open();
        store.Save(Job());
        Assert.Equal(Job(), Open().Get("fixture"));
        Assert.Empty(Directory.EnumerateFiles(Root, "*.db*"));
        File.WriteAllText(Path.Combine(Root, "distribution.db"), "fixture");
        var withDatabase = Inventory();
        Assert.Equal(AuthorityRootKind.LegacyDatabase, Assert.Throws<AuthorityRootException>(() => Open()).Kind);
        Assert.Equal(withDatabase, Inventory());
        File.Delete(Path.Combine(Root, "distribution.db"));
        File.WriteAllText(Path.Combine(Root, "unexpected.db-wal"), "fixture");
        var before = Inventory();
        Assert.Equal(AuthorityRootKind.LegacyDatabase, Assert.Throws<AuthorityRootException>(() => Open()).Kind);
        Assert.Equal(before, Inventory());
    }

    internal static void AddAbandonedWork(IntakeStore store, string job, string scratch, string? retained = null)
    {
        store.Authority.Commit(snapshot =>
        {
            var jobs = IntakeJobs.Read(snapshot);
            jobs.ActiveWork.Add(new(job, Guid.NewGuid(), Guid.NewGuid(), IntakeJobs.Relative(store.Root, scratch),
                retained is null ? null : IntakeJobs.Relative(store.Root, retained), DateTimeOffset.UnixEpoch));
            return jobs.Plan();
        });
    }

    private string[] Inventory() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path =>
        {
            var attributes = File.GetAttributes(path);
            var digest = attributes.HasFlag(FileAttributes.Directory) ? "" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return $"{Path.GetRelativePath(Root, path)}|{attributes}|{digest}";
        }).ToArray();

    private IntakeStore Open() => new(new() { Root = Root });
    private IntakeJob Job(string state = "waiting") => new("fixture", Path.Combine(Root, "incoming", "fixture"),
        state, Path.Combine(Root, "processing", "snapshot"), null);
    private IntakeWorkClaim Claim(IntakeStore store, string expected) => new(store, store.Get("fixture"), "validating",
        Path.Combine(Root, "processing", "scratch"), null, expected);

    public void Dispose()
    {

        fixture.Dispose();
    }
}
