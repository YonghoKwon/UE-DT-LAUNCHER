using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public async Task CleanupSkipsLiveScratchWhileAnotherJobCanIngest()
    {
        var store = new IntakeStore(new() { Root = root });
        var upload = await Package(store);
        using var reached = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var ingest = Task.Run(() => store.IngestAsync(upload, observer: new InlineProgress(value =>
        {
            if (value.Phase == "extract") { reached.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(15))); }
        })));
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));
            var scratch = Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "processing")));
            var reopened = new IntakeStore(store.Settings);
            StorageMaintenance.Cleanup(reopened, true);
            Assert.True(Directory.Exists(scratch));
            var second = await reopened.IngestAsync(await Package(store, "second"));
            Assert.Equal("pending", second.State);
        }
        finally { resume.Set(); }
        Assert.Equal("pending", (await ingest).State);
    }

    [Fact]
    public async Task CrossProcessDuplicateClaimIsExcludedByOperatingSystemLock()
    {
        var store = new IntakeStore(new() { Root = root });
        var upload = await Package(store);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(upload)))).ToLowerInvariant()[..24];
        var config = Path.Combine(root, "server.json"); await JsonFiles.WriteAsync(config, store.Settings);
        using (store.LockJob(id))
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var argument in new[] { typeof(IntakeStore).Assembly.Location, "ingest", upload, "--config", config }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var error = child.StandardError.ReadToEndAsync(); var output = child.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(true); }
            Assert.NotEqual(0, child.ExitCode);
            Assert.Contains("IOException", await error); await output;
            Assert.Empty(store.List());
        }
        Assert.Equal("pending", (await new IntakeStore(store.Settings).IngestAsync(upload)).State);
    }

    [Fact]
    public async Task ConcurrentApprovalAndRejectionCannotOverwriteOneReleaseId()
    {
        var store = new IntakeStore(new() { Root = root, SigningKeyPath = Path.Combine(root, "key.pem") });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var first = await store.IngestAsync(await Package(store));
        var second = await store.IngestAsync(await Package(store, "second"));
        using var reached = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var publisher = new ApprovedPublisher(store);
        var approval = Task.Run(() => publisher.ApproveAsync(first.Id, observer: new InlineProgress(value =>
        {
            if (value.Phase == "hash") { reached.Set(); Assert.True(resume.Wait(TimeSpan.FromSeconds(15))); }
        })));
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));
            Assert.Throws<IOException>(() => store.Reject(first.Id));
            await Assert.ThrowsAsync<IOException>(() => new ApprovedPublisher(new IntakeStore(store.Settings)).ApproveAsync(first.Id));
            var winning = await publisher.ApproveAsync(second.Id);
            Assert.Equal(second.Id, winning.JobId);
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<IOException>(() => approval);
        Assert.Equal(second.Id, Assert.Single(publisher.List()).JobId);
        Assert.Equal("failed", store.Get(first.Id).State);
        Assert.Contains("another publication", store.Get(first.Id).Message);
        Assert.Equal("published", store.Get(second.Id).State);
        StorageMaintenance.Cleanup(store, true);
        Assert.True(Directory.Exists(first.Snapshot));
        store.Retry(first.Id);
        Assert.Equal("waiting", store.Get(first.Id).State);
    }

    [Fact]
    public async Task DeadWorkRecordIsReclaimedWithoutDeletingRetainedSnapshot()
    {
        var store = new IntakeStore(new() { Root = root });
        var job = await store.IngestAsync(await Package(store));
        var scratch = Path.Combine(root, "processing", "crashed-build"); Directory.CreateDirectory(scratch);
        using (var db = store.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "INSERT INTO active_work VALUES($id,'dead-process',$scratch,$snapshot,'2000-01-01')";
            command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$scratch", scratch); command.Parameters.AddWithValue("$snapshot", job.Snapshot);
            command.ExecuteNonQuery();
        }
        var reopened = new IntakeStore(store.Settings);
        StorageMaintenance.Cleanup(reopened, false); Assert.True(Directory.Exists(scratch));
        StorageMaintenance.Cleanup(reopened, true); Assert.False(Directory.Exists(scratch));
        Assert.True(Directory.Exists(job.Snapshot));
        Assert.Equal("pending", reopened.Get(job.Id).State);
        using var check = reopened.Open(); using var query = check.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM active_work";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public async Task KilledIntakeProcessRecoversClaimAndNeverPublishesAutomatically()
    {
        var store = new IntakeStore(new() { Root = root });
        var upload = Path.Combine(root, "incoming", "crash"); Directory.CreateDirectory(upload);
        var zip = Path.Combine(upload, "game.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var file = archive.CreateEntry("game.exe").Open())
        {
            var buffer = new byte[64 * 1024]; RandomNumberGenerator.Fill(buffer);
            for (var i = 0; i < 2048; i++) file.Write(buffer); // 128 MiB expanded, bounded memory.
        }
        await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(upload, "release.json"),
            new() { ProjectId = "crash", Version = "1.0.0", EntryPoint = "game.exe" });
        var config = Path.Combine(root, "server.json"); await JsonFiles.WriteAsync(config, store.Settings);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(IntakeStore).Assembly.Location, "ingest", upload, "--config", config }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var error = child.StandardError.ReadToEndAsync(); var output = child.StandardOutput.ReadToEndAsync();
        var claimed = false;
        try
        {
            var started = Stopwatch.GetTimestamp();
            while (!child.HasExited && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                using var db = store.Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM active_work";
                if (Convert.ToInt64(query.ExecuteScalar()) > 0) { claimed = true; child.Kill(true); break; }
                await Task.Delay(5);
            }
            Assert.True(claimed, "Child process must be killed after its durable work claim.");
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); await error; await output; }
        var reopened = new IntakeStore(store.Settings);
        Assert.Empty(new ApprovedPublisher(reopened).List());
        StorageMaintenance.Cleanup(reopened, true);
        var recovered = await reopened.IngestAsync(upload);
        Assert.Equal("pending", recovered.State);
        Assert.Empty(new ApprovedPublisher(reopened).List());
    }

    [Fact]
    public async Task ActivationBeforeDatabaseCommitIsAdoptedOnlyByTheSameJob()
    {
        var store = new IntakeStore(new() { Root = root, SigningKeyPath = Path.Combine(root, "key.pem") });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var job = await store.IngestAsync(await Package(store));
        var first = await new ApprovedPublisher(store).ApproveAsync(job.Id);
        using (var db = store.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "DELETE FROM releases; UPDATE jobs SET state='publishing' WHERE id=$id";
            command.Parameters.AddWithValue("$id", job.Id); command.ExecuteNonQuery();
        }
        var reopened = new IntakeStore(store.Settings);
        var recovered = await new ApprovedPublisher(reopened).ApproveAsync(job.Id);
        Assert.Equal(first.ReleaseId, recovered.ReleaseId);
        Assert.Equal("published", reopened.Get(job.Id).State);
        Assert.Single(new ApprovedPublisher(reopened).List());
    }

    [Fact]
    public async Task BoundedScanDefersOverflowWithoutApproving()
    {
        var store = new IntakeStore(new() { Root = root, IntakeWorkers = 2 });
        for (var i = 0; i < 12; i++) await Package(store, "upload" + i);
        await store.ScanAsync(default);
        Assert.InRange(store.List().Count, 1, 6);
        for (var i = 0; i < 12 && store.List().Count < 12; i++) await store.ScanAsync(default);
        Assert.Equal(12, store.List().Count);
        Assert.All(store.List(), job => Assert.Equal("pending", job.State));
        Assert.Empty(new ApprovedPublisher(store).List());
        Assert.Throws<ArgumentOutOfRangeException>(() => new IntakeStore(new() { Root = root, IntakeWorkers = 3 }));
    }

    [Fact]
    public async Task PartialUploadsDoNotStarveLaterReadyUpload()
    {
        var store = new IntakeStore(new() { Root = root });
        for (var i = 0; i < 16; i++) Directory.CreateDirectory(Path.Combine(root, "incoming", "partial" + i));
        var ready = await Package(store, "ready");
        for (var i = 0; i < 20 && !store.List().Any(j => j.Source == ready && j.State == "pending"); i++)
            await store.ScanAsync(default);
        Assert.Contains(store.List(), job => job.Source == ready && job.State == "pending");
    }

    private async Task<string> Package(IntakeStore store, string name = "upload")
    {
        var upload = Path.Combine(store.Root, "incoming", name); Directory.CreateDirectory(upload);
        var zip = Path.Combine(upload, "game.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("game.exe").Open())) writer.Write(new string('x', 4096));
        await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(upload, "release.json"),
            new() { ProjectId = "demo", Version = "1.0.0", EntryPoint = "game.exe" });
        return upload;
    }

    private sealed class InlineProgress(Action<PackageWorkProgress> callback) : IProgress<PackageWorkProgress>
    {
        public void Report(PackageWorkProgress value) => callback(value);
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
