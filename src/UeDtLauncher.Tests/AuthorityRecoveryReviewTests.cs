using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using Xunit.Abstractions;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class AuthorityRecoveryReviewTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPreservesFormerHeadReferencesUntilDirectorySyncSucceeds(bool killWriter)
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        var formerHeadReference = store.DataPath("catalog/000000.json");
        for (var next = 1; next <= AuthorityStore.PriorGenerationsToKeep; next++)
            Committed(store.Commit(_ => CommitPlan.Audit(Event($"g{next}"))));
        Assert.Equal("catalog/000000.json", store.ReadSnapshot().Head.Files["catalog"]);
        Assert.True(File.Exists(formerHeadReference));

        if (killWriter)
        {
            using var child = Start(fixture.Root, "crash", nameof(AuthorityBoundary.HeadRename), "mixed");
            await ForceStop(child);
        }
        else
        {
            AuthorityDisk.Boundary.Value = point =>
            {
                if (point == AuthorityBoundary.HeadRename) throw new IOException("HEAD directory sync interrupted.");
            };
            try
            {
                Assert.Equal(9, Assert.Throws<AuthorityCommitDurabilityException>(() =>
                    store.Commit(snapshot => SetCounters(snapshot, 9, Event("g9")))).PublishedGeneration);
            }
            finally { AuthorityDisk.Boundary.Value = null; }
        }

        var orphan = store.DataPath("events/000010.json");
        var temporary = AuthorityDisk.Temporary(store.DataPath("HEAD"));
        var releaseMetadata = Path.Combine(store.ReleaseMetadataPath, "unpublished.json");
        File.WriteAllText(orphan, "{}");
        File.WriteAllText(temporary, "{}");
        File.WriteAllText(releaseMetadata, "{}");
        var before = Inventory(fixture.Root);
        var syncError = new IOException("Injected pre-cleanup directory sync failure.");
        AuthorityDisk.Boundary.Value = point =>
        {
            if (point == AuthorityBoundary.BeforeCleanupDirectoryFlush) throw syncError;
        };
        try
        {
            var reopened = fixture.Open();
            Assert.Same(syncError, reopened.OpenMaintenanceError);
            Assert.Equal(9, reopened.ReadSnapshot().Generation);
            Assert.Equal(8, ReadProjection(reopened).Length);
            using (var writer = reopened.Lock()) Assert.Same(syncError, reopened.Recover(writer));
            Assert.Same(syncError, Assert.Throws<IOException>(() => reopened.Commit(
                _ => throw new InvalidOperationException("The plan cannot run after failed pre-cleanup sync."))));
            Assert.Equal(before, Inventory(fixture.Root));
        }
        finally { AuthorityDisk.Boundary.Value = null; }

        var recovered = fixture.Open();
        Assert.Null(recovered.OpenMaintenanceError);
        Assert.Equal(9, recovered.ReadSnapshot().Generation);
        Assert.Equal(9, ReadProjection(recovered).Length);
        Assert.Equal(recovered.ReadAudit(recovered.ReadSnapshot()), ReadProjection(recovered));
        Assert.All(new[] { formerHeadReference, orphan, temporary, releaseMetadata }, path => Assert.False(File.Exists(path)));
        output.WriteLine($"Interrupted publication by {(killWriter ? "process stop" : "sync failure")}; reopen, explicit recovery and commit preserved the whole tree until sync succeeded.");
    }

    [Theory]
    [InlineData("InitializationPrepared", false)]
    [InlineData("InitializationPrepared", true)]
    [InlineData("InitializationPublished", false)]
    [InlineData("InitializationPublished", true)]
    public async Task InterruptedInitializationCanReopenEmptyOrPublishedRoot(string boundary, bool precreate)
    {
        using var fixture = new AuthorityStoreFixture();
        var root = Path.Combine(fixture.Root, "store");
        if (precreate) Directory.CreateDirectory(root);
        using (var child = Start(root, "crash-init", boundary)) await ForceStop(child);
        var published = boundary == nameof(AuthorityBoundary.InitializationPublished);
        Assert.Equal(published ? ["authority"] : Array.Empty<string>(),
            Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(root, ".maintenance.lock")));
        Assert.False(File.Exists(Path.Combine(root, ".writer.lock")));

        using var restarted = Start(root, "reopen");
        var outcome = await restarted.Finish();
        Assert.True(outcome.ExitCode == 0, outcome.Error);
        var store = AuthorityStore.Open(root);
        Assert.Null(store.OpenMaintenanceError);
        Assert.Equal(0, store.ReadSnapshot().Generation);
        Assert.True(store.ReadSnapshot().Get(AuthorityArea.Catalog).GetProperty("promotionReady").GetBoolean());
        Assert.Empty(ReadProjection(store));
        output.WriteLine($"{boundary}, existing empty directory {precreate}: restart initialized or reopened generation zero.");
    }

    [Fact]
    public async Task ConcurrentInitializersPublishOnlyOneGenerationZero()
    {
        using var fixture = new AuthorityStoreFixture();
        var root = Path.Combine(fixture.Root, "store");
        using var first = Start(root, "reopen");
        using var second = Start(root, "reopen");
        foreach (var result in await Task.WhenAll(first.Finish(), second.Finish()))
            Assert.True(result.ExitCode == 0, result.Error);
        var store = AuthorityStore.Open(root);
        Assert.Equal(0, store.ReadSnapshot().Generation);
        Assert.Single(Directory.EnumerateFiles(store.DataPath("events")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".authority-init.tmp-*"));
    }

    [Fact]
    public void StagedRootCanBeInspectedReadOnlyUsingItsExistingExclusiveLease()
    {
        using var fixture = new AuthorityStoreFixture();
        var original = fixture.Open();
        Committed(original.Commit(_ => CommitPlan.Audit(Event("protected"))));
        var fence = Path.Combine(fixture.Root, "restore-staged.json");
        File.WriteAllText(fence, "{}");
        File.Delete(Path.Combine(fixture.Root, ".writer.lock"));
        File.Delete(original.DataPath("audit.jsonl"));
        File.WriteAllText(original.DataPath("events/000002.json"), "{}");

        using var lease = DistributionMaintenanceLease.Acquire(fixture.Root, exclusive: true, create: false);
        var before = Inventory(fixture.Root);
        var inspection = AuthorityStore.OpenReadOnly(fixture.Root, lease);
        var snapshot = inspection.ReadSnapshot();
        Assert.Equal(1, snapshot.Generation);
        Assert.True(inspection.HasAudit(snapshot, "audit", "protected"));
        Assert.Throws<InvalidOperationException>(() => inspection.Commit(_ => CommitPlan.Audit(Event("forbidden"))));
        Assert.Equal(before, Inventory(fixture.Root));

        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(inspection.ReadSnapshot);
        Assert.Throws<ObjectDisposedException>(() => inspection.ReadAudit(snapshot).ToArray());
        Assert.Throws<InvalidDataException>(fixture.Open);
        Assert.Equal(before, Inventory(fixture.Root));
    }

    [Fact]
    public void InspectionRejectsSharedForeignAndUninitializedLeasesWithoutWriting()
    {
        using var fixture = new AuthorityStoreFixture();
        using var other = new AuthorityStoreFixture();
        fixture.Open();
        using (var shared = DistributionMaintenanceLease.Acquire(fixture.Root, exclusive: false, create: false))
            Assert.Throws<ArgumentException>(() => AuthorityStore.OpenReadOnly(fixture.Root, shared));
        using var otherLease = DistributionMaintenanceLease.Acquire(other.Root, exclusive: true, create: false);
        var before = Inventory(other.Root);
        Assert.Throws<ArgumentException>(() => AuthorityStore.OpenReadOnly(fixture.Root, otherLease));
        Assert.Throws<AuthorityRootException>(() => AuthorityStore.OpenReadOnly(other.Root, otherLease));
        Assert.Equal(before, Inventory(other.Root));
    }

    private static async Task ForceStop(Child child)
    {
        Assert.Equal("BOUNDARY", await child.ReadLine());
        child.Process.Kill();
        Assert.NotEqual(0, (await child.Finish()).ExitCode);
    }

    private static string[] Inventory(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path) == ".maintenance.lock"
                ? $"{Path.GetRelativePath(root, path)} {File.GetAttributes(path)}"
                : $"{Path.GetRelativePath(root, path)} {AuthorityDocuments.Hash(File.ReadAllBytes(path))}")
            .Order(StringComparer.Ordinal).ToArray();
}
