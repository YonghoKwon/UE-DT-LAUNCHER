using System.Text.Json;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using Xunit.Abstractions;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class AuthorityRootAndCacheTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void G8_NewOrEmptyRootInitializesGenerationZero(bool missing)
    {
        using var fixture = new AuthorityStoreFixture();
        var root = missing ? Path.Combine(fixture.Root, "new", "storage") : fixture.Root;
        var store = AuthorityStore.Open(root);
        var snapshot = store.ReadSnapshot();
        Assert.Equal(0, snapshot.Generation);
        Assert.Null(snapshot.Head.Previous);
        Assert.True(snapshot.Get(AuthorityArea.Catalog).GetProperty("promotionReady").GetBoolean());
        Assert.Equal(0, snapshot.Get(AuthorityArea.Sequence).GetProperty("highWater").GetInt64());
        Assert.Equal(5, snapshot.Head.Files.Count);
        Assert.Empty(store.ReadAudit(snapshot));
        Assert.Empty(ReadProjection(store));
        Assert.Null(store.OpenMaintenanceError);
    }

    [Theory]
    [InlineData("database", "LegacyDatabase")]
    [InlineData("wal", "LegacyDatabase")]
    [InlineData("pre-v6", "LegacyDatabase")]
    [InlineData("mixed", "ExistingDistribution")]
    [InlineData("head-sha", "Damaged")]
    [InlineData("area-sha", "Damaged")]
    [InlineData("missing-reference", "Damaged")]
    [InlineData("historical-event-sha", "Damaged")]
    [InlineData("dt-parent", "UnrecognizedLayout")]
    [InlineData("dt-static", "UnrecognizedLayout")]
    [InlineData("standalone-lock", "UnrecognizedLayout")]
    public async Task G8_RejectedRootExitsNonzeroBeforeAnyChange(string scenario, string expectedKind)
    {
        using var fixture = new AuthorityStoreFixture();
        var root = fixture.Root;
        var head = Path.Combine(root, "authority", "HEAD");
        var audit = Path.Combine(root, "authority", "audit.jsonl");
        var maintenance = Path.Combine(root, ".maintenance.lock");
        var writer = Path.Combine(root, ".writer.lock");
        var initialized = scenario is "head-sha" or "area-sha" or "missing-reference" or "historical-event-sha";
        switch (scenario)
        {
            case "database":
                File.WriteAllText(Path.Combine(root, "distribution.db"), "old data");
                break;
            case "wal":
                File.WriteAllText(Path.Combine(root, "copy.db-wal"), "uncheckpointed");
                break;
            case "pre-v6":
                File.WriteAllText(Path.Combine(root, "distribution.pre-v6-20261126.db"), "old backup");
                break;
            case "mixed":
                Directory.CreateDirectory(Path.Combine(root, "releases", "empty"));
                Directory.CreateDirectory(Path.Combine(root, "jobs", "empty"));
                break;
            case "head-sha":
            case "area-sha":
            case "missing-reference":
            case "historical-event-sha":
                var store = fixture.Open();
                if (scenario == "historical-event-sha")
                {
                    Committed(store.Commit(_ => CommitPlan.Audit(Event("historical"))));
                    Committed(store.Commit(_ => CommitPlan.Audit(Event("latest"))));
                    var oldEvent = store.DataPath("events/000001.json");
                    File.WriteAllText(oldEvent, File.ReadAllText(oldEvent).Replace("historical", "modified", StringComparison.Ordinal));
                }
                File.Delete(maintenance);
                File.Delete(writer);
                File.Delete(audit);
                var catalog = store.DataPath("catalog/000000.json");
                if (scenario == "head-sha")
                {
                    var original = AuthorityDocuments.Decode<AuthorityHead>(File.ReadAllBytes(head));
                    File.WriteAllBytes(head, AuthorityDocuments.Encode(original with { HeadSha256 = new string('0', 64) }));
                }
                else if (scenario == "area-sha")
                    File.AppendAllText(catalog, " ");
                else if (scenario == "missing-reference") File.Delete(catalog);
                break;
            case "dt-parent":
                foreach (var directory in new[] { "incoming", "keys", "publish/linux-x64", "tools", "ue-dt-updates/catalogs/general" })
                    Directory.CreateDirectory(Path.Combine(root, directory));
                File.WriteAllText(Path.Combine(root, "publish", "linux-x64", "UeDtLauncher"), "fixture");
                break;
            case "dt-static":
                Directory.CreateDirectory(Path.Combine(root, "catalogs", "general"));
                Directory.CreateDirectory(Path.Combine(root, "projects", "m7at10-dt", "prod", "stable", "0.0.1", "linux-x64"));
                File.WriteAllText(Path.Combine(root, "catalogs", "general", "catalog.json"), "{}");
                break;
            case "standalone-lock":
                File.WriteAllText(writer, "unknown lock");
                break;
        }
        if (scenario is "dt-parent" or "dt-static")
        {
            var hiddenFile = Path.Combine(root, ".fixture-hidden");
            var hiddenDirectory = Directory.CreateDirectory(Path.Combine(root, ".empty-hidden"));
            File.WriteAllText(hiddenFile, "existing hidden fixture data");
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(hiddenFile, FileAttributes.Hidden);
                hiddenDirectory.Attributes |= FileAttributes.Hidden;
            }
        }
        var before = Inventory(root);
        using var child = Start(root, "probe");
        var rejected = await child.Finish();
        Assert.Equal(3, rejected.ExitCode);
        Assert.StartsWith(expectedKind + ":", rejected.Error);
        Assert.Equal(before, Inventory(root));
        Assert.Equal(initialized, File.Exists(head));
        Assert.False(File.Exists(audit));
        Assert.False(File.Exists(maintenance));
        Assert.Equal(scenario == "standalone-lock", File.Exists(writer));
        output.WriteLine($"{scenario}: exit 3 ({expectedKind}), {before.Length} unchanged inventory entries including file hashes and directory attributes.");
    }

    [Fact]
    public void G8_LayoutCanBeSuppliedWithoutChangingLogicalAreaNames()
    {
        using var fixture = new AuthorityStoreFixture();
        var layout = new AuthorityLayout(AuthorityDirectory: "state", CatalogDirectory: "index", JobsDirectory: "work",
            AuthDirectory: "credentials", SequenceDirectory: "counter", EventsDirectory: "history",
            ReleaseMetadataDirectory: "release-info", ProgressDirectory: "status",
            ExistingDistributionMarkers: ["old/published"]);
        var old = Directory.CreateDirectory(Path.Combine(fixture.Root, "old", "published"));
        var rejected = Assert.Throws<AuthorityRootException>(() => AuthorityStore.Open(fixture.Root, layout));
        Assert.Equal(AuthorityRootKind.ExistingDistribution, rejected.Kind);
        Assert.True(old.Exists);
        var store = AuthorityStore.Open(Path.Combine(fixture.Root, "new-store"), layout);
        var committed = Committed(store.Commit(snapshot => SetCounters(snapshot, 42, Event("configured"))));
        Assert.EndsWith("state", store.DirectoryPath);
        Assert.Equal(Path.Combine(store.DirectoryPath, "release-info"), store.ReleaseMetadataPath);
        Assert.Equal(Path.Combine(store.DirectoryPath, "status"), store.ProgressPath);
        Assert.True(Directory.Exists(store.ReleaseMetadataPath));
        Assert.True(Directory.Exists(store.ProgressPath));
        Assert.Equal("index/000001.json", store.ReadSnapshot().Head.Files["catalog"]);
        Assert.Equal("history/000001.json", store.ReadSnapshot().Head.Files["events"]);
        Assert.Equal(committed.Generation, AuthorityStore.Open(Path.Combine(fixture.Root, "new-store"), layout).ReadSnapshot().Generation);
        Assert.Equal("configured", Assert.Single(ReadProjection(store)).Job);
    }

    [Fact]
    public void G9_SamePathAndDigestReuseParsingButChangesAndMissingFilesAreObserved()
    {
        using var fixture = new AuthorityStoreFixture();
        var store = fixture.Open();
        var original = store.ReadSnapshot();
        var oldCatalog = original.Get(AuthorityArea.Catalog);
        Assert.Equal(oldCatalog, store.ReadSnapshot().Get(AuthorityArea.Catalog));

        var relative = original.Head.Files["catalog"];
        var updated = AuthorityDocuments.Encode(Set(oldCatalog, "counter", 42));
        var newHash = AuthorityDocuments.Hash(updated);
        File.WriteAllBytes(store.DataPath(relative), updated);
        Assert.Throws<AuthorityStoreException>(() => store.ReadSnapshot());
        var hashes = new Dictionary<string, string>(original.Head.Sha256) { ["catalog"] = newHash };
        var head = AuthorityDocuments.Seal(original.Head with { Sha256 = hashes });
        File.WriteAllBytes(store.DataPath("HEAD"), AuthorityDocuments.Encode(head));

        var changed = store.ReadSnapshot().Get(AuthorityArea.Catalog);
        Assert.NotEqual(oldCatalog, changed);
        Assert.Equal(42, changed.GetProperty("counter").GetInt32());
        Assert.Equal(changed, store.ReadSnapshot().Get(AuthorityArea.Catalog));
        File.Delete(store.DataPath(relative));
        Assert.Throws<AuthorityStoreException>(() => store.ReadSnapshot());
    }

    [Fact]
    public void G9_CacheEvictsByEntryCountAndSerializedByteBudget()
    {
        var cache = new AuthorityAreaCache();
        var tiny = AuthorityDocuments.Encode(AuthorityDocuments.Empty(AuthorityArea.Sequence));
        var digest = AuthorityDocuments.Hash(tiny);
        var first = cache.Parse("old", digest, tiny);
        for (var i = 0; i < AuthorityAreaCache.EntryLimit; i++)
            cache.Parse($"entry-{i}", digest, tiny);
        Assert.NotEqual(first, cache.Parse("old", digest, tiny));

        var large = AuthorityDocuments.Encode(new { schemaVersion = 1, data = new string('a', AuthorityAreaCache.ByteLimit / 2) });
        var largeDigest = AuthorityDocuments.Hash(large);
        var oldLarge = cache.Parse("large-a", largeDigest, large);
        cache.Parse("large-b", largeDigest, large);
        Assert.NotEqual(oldLarge, cache.Parse("large-a", largeDigest, large));
    }

    [Fact]
    public void HeldWriterSupportsRepeatedCommitsAndRejectsForeignOrDisposedLease()
    {
        using var fixture = new AuthorityStoreFixture();
        using var other = new AuthorityStoreFixture();
        var store = fixture.Open();
        var otherStore = other.Open();
        using var writer = store.Lock(exclusiveMaintenance: true);
        Assert.Throws<InvalidOperationException>(() => otherStore.CommitUnderHeldLock(writer, _ => CommitPlan.Audit(Event("wrong-root"))));
        var noOp = store.CommitUnderHeldLock(writer, _ => CommitPlan.Audit());
        Assert.False(noOp.Changed);
        Assert.Equal(0, noOp.Generation);
        for (var generation = 1; generation <= 2; generation++)
            Assert.Equal(generation, Committed(store.CommitUnderHeldLock(writer, _ => CommitPlan.Audit(Event("held")))).Generation);
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.CommitUnderHeldLock(writer, _ => CommitPlan.Audit(Event("disposed"))));
        Assert.Equal(2, store.ReadSnapshot().Generation);
    }

    private static string[] Inventory(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path =>
        {
            var attributes = File.GetAttributes(path);
            var hash = (attributes & FileAttributes.Directory) != 0 ? "-" : AuthorityDocuments.Hash(File.ReadAllBytes(path));
            return $"{Path.GetRelativePath(root, path)} {attributes} {hash}";
        }).Order(StringComparer.Ordinal).ToArray();
}
