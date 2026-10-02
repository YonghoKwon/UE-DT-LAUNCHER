using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ReleasePromotionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "promotion-tests-"+Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    public ReleasePromotionTests() => store = new(new() { Root = root });
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private ReleaseSelection Selection(string version) => new("demo", "prod", "stable", "windows-x64", version);
    private void Publish(string version)
    {
        var m = new ReleaseSidecar { ProjectId = "demo", Version = version, Platform = "windows-x64", EntryPoint = "game.exe", PackageFile = "Windows.zip" };
        using var db = store.Open(); using var q = db.CreateCommand();
        q.CommandText = "INSERT INTO releases VALUES($id,$job,$dir,$meta)";
        q.Parameters.AddWithValue("$id", m.ReleaseId); q.Parameters.AddWithValue("$job", version); q.Parameters.AddWithValue("$dir", root);
        q.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(m, JsonFiles.Options)); q.ExecuteNonQuery();
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
        using var db = store.Open(); using var q = db.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM audit WHERE action='release-promoted'";
        Assert.Equal(0L, Convert.ToInt64(q.ExecuteScalar()));
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
    public void MigrationDryRunIsReadOnlyAndApplyPreservesOrderExactlyOnce()
    {
        Publish("2.0.0"); Publish("1.0.0");
        using (var db = store.Open()) { using var q = db.CreateCommand(); q.CommandText = "DROP TABLE promotions; DROP TABLE promotion_state; PRAGMA user_version=3"; q.ExecuteNonQuery(); }
        SqliteConnection.ClearAllPools(); var before = Snapshot(root);
        var preview = ReleasePromotions.PreviewMigration(store.Settings); Assert.True(preview.ApplyRequired); Assert.Equal(before, Snapshot(root));
        var upgraded = new IntakeStore(store.Settings); Assert.Single(Directory.GetFiles(root, "distribution.pre-v5-*.db"));
        var p = new ReleasePromotions(upgraded); Assert.False(p.Snapshot().Ready);
        Assert.Throws<InvalidOperationException>(() => p.Promote(Selection("1.0.0"), 0, "before migration"));
        p.Migrate(); var current = p.Inspect(Selection("1.0.0")); Assert.Equal(Selection("1.0.0").ReleaseId, current.RecommendedRelease);
        Assert.Equal(2, current.History.Count); Assert.All(current.History, e => Assert.Equal("legacy-baseline", e.Kind));
        p.Migrate(); Assert.Equal(2, p.Snapshot().Events.Count);
    }
    [Fact]
    public void MigrationApplyRejectsLiveServerLease()
    {
        using var server = new AuthenticationProcessLease(root);
        Assert.Throws<IOException>(() => new ReleasePromotions(store).Migrate());
    }
    private static string[] Snapshot(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
        .Select(p => Path.GetRelativePath(folder, p)+":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
}
