using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using Xunit.Abstractions;

namespace UeDtLauncher.Tests;

public sealed class IntakePerformanceTests(ITestOutputHelper testOutput) : IDisposable
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
    public void LegacyDatabaseIsRejectedWithoutSchemaChange()
    {
        Directory.CreateDirectory(root);
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "distribution.db") + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE jobs(id TEXT PRIMARY KEY,source TEXT NOT NULL,state TEXT NOT NULL,snapshot TEXT,message TEXT); INSERT INTO jobs VALUES('old','source','waiting',NULL,NULL)";
            command.ExecuteNonQuery();
        }
        var databasePath = Path.Combine(root, "distribution.db");
        var bytes = File.ReadAllBytes(databasePath);
        Assert.Equal(AuthorityRootKind.LegacyDatabase,
            Assert.Throws<AuthorityRootException>(() => new IntakeStore(new() { Root = root })).Kind);
        Assert.Equal(databasePath, Assert.Single(Directory.EnumerateFileSystemEntries(root)));
        Assert.Equal(bytes, File.ReadAllBytes(databasePath));
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
    public async Task ConcurrentActivationFailsTheLosingJobUnderItsHeldWriter()
    {
        var store = new IntakeStore(new() { Root = root, SigningKeyPath = Path.Combine(root, "key.pem") });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var loser = await store.IngestAsync(await Package(store, "loser"));
        var winner = await store.IngestAsync(await Package(store, "winner"));
        var publisher = new ApprovedPublisher(store);
        using var reached = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var writerAcquisitions = 0;
        var approval = Task.Run(async () =>
        {
            try
            {
                return await publisher.ApproveAsync(loser.Id, observer: new InlineProgress(progress =>
                {
                    if (progress.Phase != "activate") return;
                    reached.Set();
                    Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
                    AuthorityStore.BeforeAcquireWriter.Value = () =>
                        Assert.InRange(Interlocked.Increment(ref writerAcquisitions), 1, 2);
                }));
            }
            finally { AuthorityStore.BeforeAcquireWriter.Value = null; }
        });
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal("publishing", store.Get(loser.Id).State);
            Assert.Equal(winner.Id, (await publisher.ApproveAsync(winner.Id)).JobId);
        }
        finally { resume.Set(); }
        var error = await Assert.ThrowsAsync<IOException>(async () =>
            await approval.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("another publication", error.Message);
        Assert.Equal(2, writerAcquisitions); // Activation and claim release. Conflict recording borrows the writer.
        Assert.Equal(winner.Id, Assert.Single(publisher.List()).JobId);
        Assert.Equal("failed", store.Get(loser.Id).State);
        Assert.Equal("published", store.Get(winner.Id).State);
        var snapshot = store.Authority.ReadSnapshot();
        Assert.Single(store.Authority.ReadAudit(snapshot), entry => entry.Job == loser.Id && entry.Action == "failed");
        Assert.Empty(IntakeJobs.Read(snapshot).ActiveWork);
    }

    [Fact]
    public async Task DeadWorkRecordIsReclaimedWithoutDeletingRetainedSnapshot()
    {
        var store = new IntakeStore(new() { Root = root });
        var job = await store.IngestAsync(await Package(store));
        var scratch = Path.Combine(root, "processing", "crashed-build"); Directory.CreateDirectory(scratch);
        IntakeAuthorityTests.AddAbandonedWork(store, job.Id, scratch, job.Snapshot);
        var reopened = new IntakeStore(store.Settings);
        StorageMaintenance.Cleanup(reopened, false); Assert.True(Directory.Exists(scratch));
        StorageMaintenance.Cleanup(reopened, true); Assert.False(Directory.Exists(scratch));
        Assert.True(Directory.Exists(job.Snapshot));
        Assert.Equal("pending", reopened.Get(job.Id).State);
        Assert.Empty(IntakeJobs.Read(reopened.Authority.ReadSnapshot()).ActiveWork);
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
        using var child = AuthorityStoreFixture.Start(root, "intake-claim-barrier", upload);
        var started = Stopwatch.GetTimestamp();
        string scratch;
        try
        {
            var marker = await child.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var snapshot = store.Authority.ReadSnapshot();
            var jobs = IntakeJobs.Read(snapshot);
            var claimed = marker == "CLAIMED" && jobs.ActiveWork.Count > 0;
            testOutput.WriteLine($"Durable claim: elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}, " +
                $"generation={snapshot.Generation}, activeWork={jobs.ActiveWork.Count}, childExited={child.Process.HasExited}");
            Assert.True(claimed, "Child process must be killed after its durable work claim.");
            var work = Assert.Single(jobs.ActiveWork);
            scratch = IntakeJobs.Resolve(root, work.Scratch!);
            Assert.True(File.Exists(Path.Combine(scratch, "game.zip")));
            Assert.Equal("validating", Assert.Single(jobs.Jobs, job => job.Id == work.Job).State);
            child.Process.Kill(entireProcessTree: true);
        }
        finally
        {
            if (!child.Process.HasExited) child.Process.Kill(entireProcessTree: true);
            await child.Process.WaitForExitAsync();
            var stopped = await child.Finish();
            testOutput.WriteLine($"Owned intake child exit code={stopped.ExitCode}");
            if (stopped.Error.Length != 0) testOutput.WriteLine(stopped.Error);
        }
        var reopened = new IntakeStore(store.Settings);
        Assert.Empty(new ApprovedPublisher(reopened).List());
        StorageMaintenance.Cleanup(reopened, true);
        Assert.False(Directory.Exists(scratch));
        var recovered = await reopened.IngestAsync(upload);
        Assert.Equal("pending", recovered.State);
        Assert.Empty(new ApprovedPublisher(reopened).List());
    }

    [Fact]
    public async Task ActivationBeforeHeadCommitIsAdoptedOnlyByTheSameJob()
    {
        var store = new IntakeStore(new() { Root = root, SigningKeyPath = Path.Combine(root, "key.pem") });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var job = await store.IngestAsync(await Package(store));
        CatalogAuthority.PublicationBoundary.Value = FileAuthorityFixtures.FailBeforeHead;
        try { await Assert.ThrowsAsync<IOException>(() => new ApprovedPublisher(store).ApproveAsync(job.Id)); }
        finally { CatalogAuthority.PublicationBoundary.Value = null; }
        Assert.Empty(new ApprovedPublisher(store).List());
        var reopened = new IntakeStore(store.Settings);
        var recovered = await new ApprovedPublisher(reopened).ApproveAsync(job.Id);
        var captured = await SidecarPackageValidator.ReadAsync(Path.Combine(job.Snapshot!, "release.json"));
        Assert.Equal(captured.ReleaseId, recovered.ReleaseId);
        Assert.Equal("published", reopened.Get(job.Id).State);
        Assert.Single(new ApprovedPublisher(reopened).List());
    }

    [Fact]
    public async Task ApprovalRetryCompletesFileAuthorityAfterReleaseActivation()
    {
        var store = new IntakeStore(new() { Root = root, SigningKeyPath = Path.Combine(root, "key.pem") });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var job = await store.IngestAsync(await Package(store));
        var publisher = new ApprovedPublisher(store);
        var armed = false;
        var observer = new InlineProgress(progress =>
        {
            if (progress.Phase != "activate") return;
            armed = true;
            AuthorityDisk.Boundary.Value = boundary =>
            {
                if (!armed || boundary != AuthorityBoundary.BeforeHeadRename) return;
                armed = false;
                throw new IOException("owned X4 boundary");
            };
        });
        var error = await Assert.ThrowsAsync<IOException>(() => publisher.ApproveAsync(job.Id, observer: observer));
        Assert.Equal("owned X4 boundary", error.Message);
        Assert.Equal("publishing", store.Get(job.Id).State);
        Assert.Empty(publisher.List());
        var metadata = await SidecarPackageValidator.ReadAsync(Path.Combine(job.Snapshot!, "release.json"));
        Assert.True(File.Exists(Path.Combine(root, "releases", metadata.ReleaseId, "publication.json")));
        Assert.DoesNotContain(store.Authority.ReadAudit(store.Authority.ReadSnapshot()), entry => entry.Action == "published");

        var reopened = new IntakeStore(store.Settings);
        Assert.Equal(metadata.ReleaseId, (await new ApprovedPublisher(reopened).ApproveAsync(job.Id)).ReleaseId);
        Assert.Equal("published", reopened.Get(job.Id).State);
        Assert.Single(reopened.Authority.ReadAudit(reopened.Authority.ReadSnapshot()), entry => entry.Action == "published");
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
