using System.IO.Compression;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class IntakePerformanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "intake-perf-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SnapshotSpaceFailurePrecedesCopyAndReportsEstimate()
    {
        var store = new IntakeStore(new() { Root = root }, _ => 0);
        var upload = await Package(store);
        var job = await store.IngestAsync(upload);
        Assert.Equal("failed", job.State);
        Assert.Equal("snapshot-copy", job.Phase);
        Assert.True(job.EstimatedAdditionalDiskBytes > IntakeDiskSpace.ReserveBytes);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "processing")));
        Assert.Contains("Insufficient disk space", job.Message);
    }

    [Fact]
    public async Task ExpandedSpaceIsRecheckedAndProgressSurvivesReopen()
    {
        var probes = 0;
        var store = new IntakeStore(new() { Root = root }, _ => ++probes == 1 ? long.MaxValue : 0);
        var job = await store.IngestAsync(await Package(store));
        Assert.Equal(2, probes);
        Assert.Equal("failed", job.State);
        Assert.Equal("extract", job.Phase);
        Assert.True(job.TotalBytes > 0);
        Assert.Equal(job, new IntakeStore(store.Settings).Get(job.Id));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "processing"), "payload", SearchOption.AllDirectories));
    }

    [Fact]
    public void ByteProgressIsThrottledButPhaseChangesAreImmediate()
    {
        var clock = new ManualClock(); var values = new List<PackageWorkProgress>();
        var progress = new ThrottledPackageProgress(values.Add, clock);
        progress.Report(new("copy", 0, 10, 10));
        for (var i = 1; i < 10; i++) progress.Report(new("copy", i, 10, 10 - i));
        Assert.Single(values);
        clock.Advance(); progress.Report(new("copy", 10, 10, 0));
        progress.Report(new("hash", 0, 10, 0));
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void LegacyDatabaseIsBackedUpBeforeSchemaChange()
    {
        Directory.CreateDirectory(root);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "distribution.db")))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE jobs(id TEXT PRIMARY KEY,source TEXT NOT NULL,state TEXT NOT NULL,snapshot TEXT,message TEXT); INSERT INTO jobs VALUES('old','source','waiting',NULL,NULL)";
            command.ExecuteNonQuery();
        }
        var store = new IntakeStore(new() { Root = root });
        Assert.Equal("waiting", store.Get("old").State);
        var backup = Assert.Single(Directory.GetFiles(root, "distribution.pre-v*.db"));
        using var original = new SqliteConnection("Data Source=" + backup); original.Open();
        using var query = original.CreateCommand(); query.CommandText = "PRAGMA user_version";
        Assert.Equal(0L, query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('jobs')";
        Assert.Equal(5L, query.ExecuteScalar());
    }

    private async Task<string> Package(IntakeStore store)
    {
        var upload = Path.Combine(store.Root, "incoming", "upload"); Directory.CreateDirectory(upload);
        var zip = Path.Combine(upload, "game.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("game.exe").Open())) writer.Write(new string('x', 4096));
        await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(upload, "release.json"),
            new() { ProjectId = "demo", Version = "1.0.0", EntryPoint = "game.exe" });
        return upload;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance() => timestamp += 1000;
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
