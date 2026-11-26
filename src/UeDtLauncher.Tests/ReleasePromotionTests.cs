using System.Security.Cryptography;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ReleasePromotionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "promotion-tests-"+Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    public ReleasePromotionTests() => store = new(new() { Root = root });
    public void Dispose() { Directory.Delete(root, true); }
    private ReleaseSelection Selection(string version) => new("demo", "prod", "stable", "windows-x64", version);
    private void Publish(string version)
    {
        var m = new ReleaseSidecar { ProjectId = "demo", Version = version, Platform = "windows-x64", EntryPoint = "game.exe", PackageFile = "Windows.zip" };
        FileAuthorityFixtures.SeedRelease(store, m, version);
    }
    [Fact]
    public void ApprovalDoesNotPromoteAndManualRevisionIsDurable()
    {
        Publish("1.0.0"); var p = new ReleasePromotions(store);
        Assert.Null(p.Inspect(Selection("1.0.0")).RecommendedRelease);
        var first = p.Promote(Selection("1.0.0"), 0, "validated");
        Publish("2.0.0"); var after = p.Inspect(Selection("1.0.0"));
        Assert.Equal(first.Revision, after.Revision); Assert.Equal(first.RecommendedRelease, after.RecommendedRelease); Assert.Single(after.History);
    }
    [Fact]
    public void HigherOrOlderApprovalDoesNotChangeRecommendation()
    {
        Publish("2.0.0"); var p = new ReleasePromotions(store); var promoted = p.Promote(Selection("2.0.0"), 0, "validated");
        Publish("9.0.0"); Publish("1.0.0"); var current = new ReleasePromotions(new IntakeStore(store.Settings)).Inspect(Selection("1.0.0"));
        Assert.Equal(promoted.Revision, current.Revision); Assert.Equal(Selection("2.0.0").ReleaseId, current.RecommendedRelease);
    }
    [Fact]
    public void SameTargetNoOpAndStaleRevisionFails()
    {
        Publish("1.0.0"); Publish("2.0.0"); var p = new ReleasePromotions(store); var a = p.Promote(Selection("1.0.0"), 0, "validated");
        var noOp = p.Promote(Selection("1.0.0"), a.Revision, "repeat"); Assert.Equal(a.Revision, noOp.Revision); Assert.Single(noOp.History);
        Assert.Throws<InvalidOperationException>(() => p.Promote(Selection("2.0.0"), 0, "stale"));
        var b = p.Promote(Selection("2.0.0"), a.Revision, "new validation"); Assert.Equal(a.RecommendedRelease, b.History.Last().PreviousRelease);
        Assert.Equal("manual", b.History.Last().Kind); Assert.NotEmpty(b.History.Last().Actor);
    }
    [Fact]
    public void UnpublishedAndDifferentTrackAreRejectedWithoutWrites()
    {
        Publish("1.0.0"); var p = new ReleasePromotions(store);
        Assert.Throws<InvalidOperationException>(() => p.Promote(Selection("missing"), 0, "test"));
        Assert.Throws<InvalidOperationException>(() => p.Promote(Selection("1.0.0") with { Environment = "dev" }, 0, "test"));
        Assert.Empty(p.Snapshot().Events);
    }
    [Fact]
    public void CommitFailureRollsBackHistoryAndAudit()
    {
        Publish("1.0.0"); var p = new ReleasePromotions(store);
        ReleasePromotions.BeforeCommit.Value = () => throw new IOException("test-only commit failure");
        try { Assert.Throws<IOException>(() => p.Promote(Selection("1.0.0"), 0, "test")); }
        finally { ReleasePromotions.BeforeCommit.Value = null; }
        Assert.Empty(p.Snapshot().Events);
        Assert.Empty(FileAuthorityFixtures.Audit(store, "release-promoted"));
    }
    [Fact]
    public async Task ConcurrentExpectedRevisionHasOnlyOneWinner()
    {
        Publish("1.0.0"); Publish("2.0.0"); using var start = new ManualResetEventSlim();
        var tasks = new[] { "1.0.0", "2.0.0" }.Select(v => Task.Run(() =>
        { start.Wait(); try { new ReleasePromotions(store).Promote(Selection(v), 0, "race"); return true; } catch (InvalidOperationException) { return false; } })).ToArray();
        start.Set(); Assert.Single(await Task.WhenAll(tasks), v => v); Assert.Single(new ReleasePromotions(store).Snapshot().Events);
    }
    [Fact]
    public void MigrationRejectsLegacyWithoutWriting()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        File.WriteAllBytes(Path.Combine(legacy, "distribution.db"), "SQLite format 3\0"u8.ToArray());
        var settings = new DistributionSettings { Root = legacy };
        var before = Snapshot(legacy);
        Assert.Equal(AuthorityRootKind.LegacyDatabase,
            Assert.Throws<AuthorityRootException>(() => ReleasePromotions.PreviewMigration(settings)).Kind);
        Assert.Equal(AuthorityRootKind.LegacyDatabase,
            Assert.Throws<AuthorityRootException>(() => new IntakeStore(settings)).Kind);
        Assert.Equal(before, Snapshot(legacy));
    }
    [Fact]
    public void MigrationOnFileRootIsReadOnlyEvenWhenLive()
    {
        var fresh = Path.Combine(root, "file-root");
        AuthorityStore.Open(fresh);
        using (new AuthenticationProcessLease(fresh)) { }
        var before = Snapshot(fresh);
        PromotionMigration result;
        using (new AuthenticationProcessLease(fresh))
            result = ReleasePromotions.PreviewMigration(new() { Root = fresh });
        Assert.True(result.Ready);
        Assert.False(result.ApplyRequired);
        Assert.Empty(result.BaselineReleases);
        Assert.Equal(before, Snapshot(fresh));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MigrationRejectsMixedOrDamagedRootWithoutWriting(bool mixed)
    {
        var path = Path.Combine(root, "file-root");
        var authority = AuthorityStore.Open(path);
        File.WriteAllText(mixed ? Path.Combine(path, "distribution.db") : authority.DataPath("HEAD"), "invalid");
        var before = Snapshot(path);
        Assert.Equal(mixed ? AuthorityRootKind.LegacyDatabase : AuthorityRootKind.Damaged,
            Assert.Throws<AuthorityRootException>(() => ReleasePromotions.PreviewMigration(new() { Root = path })).Kind);
        Assert.Equal(before, Snapshot(path));
    }

    private static string[] Snapshot(string folder) => Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
        .Select(p => Path.GetRelativePath(folder, p) + ":" +
            (Directory.Exists(p) ? "directory" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))).ToArray();
}
