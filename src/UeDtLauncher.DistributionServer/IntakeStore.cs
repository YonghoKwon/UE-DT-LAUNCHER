using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Channels;

namespace UeDtLauncher.Distribution;

public sealed record IntakeJob(string Id, string Source, string State, string? Snapshot, string? Message,
    string? Phase = null, long ProcessedBytes = 0, long TotalBytes = 0,
    long EstimatedAdditionalDiskBytes = 0, string? ProgressUpdatedAt = null);

public sealed class DistributionSettings
{
    public string Root { get; set; } = "/srv/ue-dt-distribution";
    public string PublicUrl { get; set; } = "https://updates.example.com";
    public string SigningKeyPath { get; set; } = "/etc/ue-dt-distribution/signing.pem";
    public string SigningKeyId { get; set; } = "release-1";
    public string ListenUrl { get; set; } = "http://127.0.0.1:18500";
    public string PolicyPath { get; set; } = "/etc/ue-dt-distribution/access-policy.json";
    public ZipIntakeLimits Limits { get; set; } = new();
    public int IntakeWorkers { get; set; } = 1;
    public string AuthenticationMode { get; set; } = "bearer";
    public int? MaxApiRequestsPerSecond { get; set; }
    public int? MaxConcurrentDownloads { get; set; }
}

public sealed class IntakeStore
{
    private readonly Func<string, long>? availableBytes;
    private string? scanCursor;
    private const string JobColumns = "id,source,state,snapshot,message,phase,processed_bytes,total_bytes,estimated_disk_bytes,progress_at";
    public DistributionSettings Settings { get; }
    internal ReaderWriterLockSlim DatabaseGate { get; }
    internal IDisposable DatabaseRead() => DistributionDatabaseCoordinator.Read(DatabaseGate);
    internal IDisposable DatabaseWrite() => DistributionDatabaseCoordinator.Write(DatabaseGate);
    public string Root => Settings.Root;
    public IntakeStore(DistributionSettings settings, Func<string, long>? availableBytes = null)
    {
        this.availableBytes = availableBytes;
        if (settings.IntakeWorkers is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(settings.IntakeWorkers), "IntakeWorkers must be 1 or 2.");
        Settings = settings;
        settings.Root = Path.GetFullPath(settings.Root);
        DatabaseGate = DistributionDatabaseCoordinator.ForRoot(settings.Root);
        foreach (var directory in new[] { "incoming", "processing", "releases", "archive", ".job-locks" })
            Directory.CreateDirectory(Path.Combine(Root, directory));
        using var gate = Lock();
        var existed = File.Exists(Path.Combine(Root, "distribution.db"));
        using var database = DatabaseWrite();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 5) throw new InvalidDataException("Distribution database schema is newer than this server.");
        if (version == 5) return;
        using var offline = new AuthenticationProcessLease(Root);
        // SQLite's backup API captures a consistent image, including committed WAL pages, before any schema change.
        if (existed)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Root, "distribution.pre-v5-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".db"), Pooling = false }.ToString());
            backup.Open(); db.BackupDatabase(backup);
        }
        using var transaction = db.BeginTransaction(); command.Transaction = transaction;
        if (version < 1)
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,source TEXT NOT NULL,state TEXT NOT NULL,snapshot TEXT,message TEXT);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,at TEXT NOT NULL,action TEXT NOT NULL,job TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tokens(hash TEXT PRIMARY KEY,client TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS releases(id TEXT PRIMARY KEY,job TEXT NOT NULL,directory TEXT NOT NULL,metadata TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sequence(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER);
            INSERT OR IGNORE INTO sequence VALUES(1,0);
            ALTER TABLE jobs ADD COLUMN phase TEXT;
            ALTER TABLE jobs ADD COLUMN processed_bytes INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN total_bytes INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN estimated_disk_bytes INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE jobs ADD COLUMN progress_at TEXT;
            """;
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS active_work(job TEXT PRIMARY KEY,owner TEXT NOT NULL,scratch TEXT,snapshot TEXT,started_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS releases_job_idx ON releases(job);
            CREATE TABLE IF NOT EXISTS device_keys(key_id TEXT PRIMARY KEY,client TEXT NOT NULL,public_pem TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS promotion_state(id INTEGER PRIMARY KEY CHECK(id=1),ready INTEGER NOT NULL);
            INSERT OR IGNORE INTO promotion_state SELECT 1,CASE WHEN COUNT(*)=0 THEN 1 ELSE 0 END FROM releases;
            CREATE TABLE IF NOT EXISTS promotions(id INTEGER PRIMARY KEY AUTOINCREMENT,track TEXT NOT NULL,release_id TEXT NOT NULL,previous_release TEXT,actor TEXT NOT NULL,at TEXT NOT NULL,reason TEXT NOT NULL,kind TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS promotions_track_idx ON promotions(track,id);
            """;
        command.ExecuteNonQuery();
        void AddColumn(string table, string name, string declaration)
        {
            command.CommandText = "PRAGMA table_info(" + table + ")";
            bool exists;
            using (var columns = command.ExecuteReader())
            { exists = false; while (columns.Read()) if (columns.GetString(1) == name) exists = true; }
            if (exists) return;
            command.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + name + " " + declaration; command.ExecuteNonQuery();
        }
        AddColumn("tokens", "management_id", "TEXT"); AddColumn("tokens", "expires_at", "TEXT"); AddColumn("tokens", "expired", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("device_keys", "expires_at", "TEXT"); AddColumn("device_keys", "expired", "INTEGER NOT NULL DEFAULT 0");
        command.CommandText = """
            UPDATE tokens SET management_id='legacy-' || substr(hash,1,24) WHERE management_id IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS tokens_management_id_idx ON tokens(management_id);
            PRAGMA user_version=5;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public SqliteConnection Open()
    {
        using var measurement = DistributionPerformance.MeasureDatabase("open");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "distribution.db") }.ToString());
        try { db.Open(); }
        catch (SqliteException ex) { db.Dispose(); DistributionPerformance.RecordDatabaseBusy(ex.SqliteErrorCode); throw; }
        return db;
    }
    public List<IntakeJob> List()
    {
        using var measurement = DistributionPerformance.MeasureDatabase("jobs-list");
        using var database = DatabaseRead();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT " + JobColumns + " FROM jobs ORDER BY rowid";
        using var reader = command.ExecuteReader(); var list = new List<IntakeJob>();
        while (reader.Read()) list.Add(ReadJob(reader));
        return list;
    }
    public IntakeJob? Find(string id)
    {
        using var measurement = DistributionPerformance.MeasureDatabase("job-find");
        using var database = DatabaseRead();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT " + JobColumns + " FROM jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadJob(reader) : null;
    }
    public IntakeJob Get(string id) => Find(id) ?? throw new InvalidOperationException("Job not found.");
    private static IntakeJob ReadJob(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));
    public void Save(IntakeJob job)
    {
        using var measurement = DistributionPerformance.MeasureDatabase("job-save");
        using var database = DatabaseWrite();
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs(id,source,state,snapshot,message,phase,processed_bytes,total_bytes,estimated_disk_bytes,progress_at)
            VALUES($id,$source,$state,$snapshot,$message,$phase,$processed,$total,$disk,$progressAt)
            ON CONFLICT(id) DO UPDATE SET state=$state,snapshot=$snapshot,message=$message,
                phase=$phase,processed_bytes=$processed,total_bytes=$total,estimated_disk_bytes=$disk,progress_at=$progressAt;
            INSERT INTO audit(at,action,job) VALUES($at,$state,$id);
            """;
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$source", job.Source);
        command.Parameters.AddWithValue("$state", job.State); command.Parameters.AddWithValue("$snapshot", (object?)job.Snapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        AddProgressParameters(command, job);
        command.ExecuteNonQuery(); transaction.Commit();
    }
    private static void AddProgressParameters(SqliteCommand command, IntakeJob job)
    {
        command.Parameters.AddWithValue("$phase", (object?)job.Phase ?? DBNull.Value);
        command.Parameters.AddWithValue("$processed", job.ProcessedBytes);
        command.Parameters.AddWithValue("$total", job.TotalBytes);
        command.Parameters.AddWithValue("$disk", job.EstimatedAdditionalDiskBytes);
        command.Parameters.AddWithValue("$progressAt", (object?)job.ProgressUpdatedAt ?? DBNull.Value);
    }
    internal ThrottledPackageProgress Progress(string id, IProgress<PackageWorkProgress>? observer = null) => new(value =>
    {
        using (var database = DatabaseWrite())
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "UPDATE jobs SET phase=$phase,processed_bytes=$processed,total_bytes=$total,estimated_disk_bytes=$disk,progress_at=$progressAt WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            AddProgressParameters(command, new(id, "", "", null, null, value.Phase, value.ProcessedBytes, value.TotalBytes,
                value.EstimatedAdditionalDiskBytes, DateTimeOffset.UtcNow.ToString("O")));
            command.ExecuteNonQuery();
        }
        observer?.Report(value); // Observers may perform I/O; never call them while holding the SQL gate.
    });
    internal Func<string, long>? AvailableBytes => availableBytes;
    public IDisposable Lock()
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(Path.Combine(Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30)) { Thread.Sleep(20); }
        }
    }
    public IDisposable LockJob(string id)
    {
        ReleaseSidecar.Segment(id);
        // Keep the inode/path stable: deleting lock files can split ownership on Unix.
        return new FileStream(Path.Combine(Root, ".job-locks", id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal bool ChangeState(string id, string expected, string next, bool reset = false, string? message = null)
    {
        using var database = DatabaseWrite();
        using var db = Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE jobs SET state=$next,message=$message" + (reset ? ",snapshot=NULL,phase=NULL,processed_bytes=0,total_bytes=0,estimated_disk_bytes=0,progress_at=NULL" : "") + " WHERE id=$id AND state=$expected";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$expected", expected); command.Parameters.AddWithValue("$next", next);
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) return false;
        command.Parameters.Clear(); command.CommandText = "INSERT INTO audit(at,action,job) VALUES($at,$action,$id)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$action", next); command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    }
    public void Audit(string action, string subject)
    {
        using var database = DatabaseWrite();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO audit(at,action,job) VALUES($at,$action,$subject)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$subject", subject);
        command.ExecuteNonQuery();
    }

    public async Task<IntakeJob> IngestAsync(string directory, CancellationToken token = default,
        IProgress<PackageWorkProgress>? observer = null)
    {
        var source = Path.GetFullPath(directory);
        var incoming = Path.Combine(Root, "incoming");
        if (!string.Equals(Path.GetDirectoryName(source), incoming, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Upload must be an immediate child of incoming.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
        using var jobLock = LockJob(id);
        var previous = Find(id);
        if (previous is { State: "pending" or "published" or "rejected" or "failed" or "publishing" }) return previous;
        var job = new IntakeJob(id, source, "waiting", null, null);
        using (var gate = Lock()) Save(job);
        IntakeWorkClaim? work = null;
        try
        {
            RejectLink(source);
            if (Directory.EnumerateFiles(source, "*.uploading").Any()) return job;
            var metadataPath = Path.Combine(source, "release.json");
            if (!File.Exists(metadataPath)) return job;
            RejectLink(metadataPath);
            var metadataBytes = await SidecarPackageValidator.ReadDocumentAsync(metadataPath, token);
            var metadata = SidecarPackageValidator.Parse(metadataBytes);
            var zip = Path.Combine(source, metadata.PackageFile);
            if (!File.Exists(zip)) return job;
            RejectLink(zip);
            var snapshot = Path.Combine(Root, "processing", id + "-" + Guid.NewGuid().ToString("N"));
            work = new IntakeWorkClaim(this, job, "validating", snapshot, snapshot, "waiting", "validating");
            job = Get(id);
            var progress = Progress(id, observer);
            var estimate = IntakeDiskSpace.Estimate(metadata.PackageSize);
            progress.Report(new("snapshot-copy", 0, metadata.PackageSize, estimate));
            IntakeDiskSpace.Require(Root, estimate, availableBytes);
            Directory.CreateDirectory(snapshot);
            // Copies are private and immutable to upload users. Approval uses only this snapshot.
            await File.WriteAllBytesAsync(Path.Combine(snapshot, "release.json"), metadataBytes, token);
            var capturedZip = Path.Combine(snapshot, metadata.PackageFile);
            await using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(capturedZip, FileMode.CreateNew, FileAccess.Write))
            {
                if (input.Length != metadata.PackageSize || input.Length > Settings.Limits.MaxZipBytes)
                    throw new InvalidDataException("ZIP size mismatch or limit exceeded.");
                var buffer = new byte[128 * 1024]; long copied = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += read;
                    if (copied > metadata.PackageSize) throw new InvalidDataException("ZIP changed while capturing upload.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress.Report(new("snapshot-copy", copied, metadata.PackageSize, IntakeDiskSpace.Estimate(metadata.PackageSize - copied)));
                }
                if (copied != metadata.PackageSize) throw new InvalidDataException("ZIP truncated while capturing upload.");
            }
            await SidecarPackageValidator.ValidateAndExtractAsync(capturedZip, metadata, Path.Combine(snapshot, "payload"), Settings.Limits, token, progress, availableBytes);
            progress.Report(new("awaiting-approval", metadata.PackageSize, metadata.PackageSize, 0));
            job = Get(id) with { State = "pending", Snapshot = snapshot };
        }
        catch (OperationCanceledException) { job = Find(id) ?? job; throw; }
        catch (Exception ex) { job = (Find(id) ?? job) with { State = "failed", Message = ex.Message }; }
        finally
        {
            // Make a successful snapshot durable before removing its active-work protection.
            try { using var gate = Lock(); Save(job); }
            finally { work?.Dispose(); }
        }
        return job;
    }

    public void Reject(string id)
    {
        using var jobLock = LockJob(id); using var gate = Lock();
        if (!ChangeState(id, "pending", "rejected")) throw new InvalidOperationException("Only pending jobs can be rejected.");
    }
    public void Retry(string id)
    {
        using var jobLock = LockJob(id); using var gate = Lock();
        if (!ChangeState(id, "failed", "waiting", reset: true)) throw new InvalidOperationException("Only failed jobs can be retried.");
    }
    public async Task ScanAsync(CancellationToken token)
    {
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(Settings.IntakeWorkers * 2)
        { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var workers = Enumerable.Range(0, Settings.IntakeWorkers).Select(_ => ConsumeAsync()).ToArray();
        try
        {
            var finished = List().Where(j => j.State is "pending" or "published" or "rejected" or "failed" or "publishing")
                .Select(j => j.Source).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var previousCursor = scanCursor;
            var afterCursor = previousCursor is null;
            var full = false;
            foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "incoming")))
            {
                token.ThrowIfCancellationRequested();
                if (!afterCursor) { if (directory == previousCursor) afterCursor = true; continue; }
                if (!Enqueue(directory)) { full = true; break; }
            }
            // Rotate admission so persistent partial uploads at the front cannot starve ready uploads.
            if (!full && previousCursor is not null)
                foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "incoming")))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Enqueue(directory)) break;
                    if (directory == previousCursor) break;
                }
            bool Enqueue(string directory)
            {
                if (finished.Contains(directory)) return true;
                if (!queue.Writer.TryWrite(directory)) return false;
                scanCursor = directory; return true;
                // Full queues defer to the next scan; never spawn per-upload tasks.
            }
        }
        finally { queue.Writer.TryComplete(); await Task.WhenAll(workers); }

        async Task ConsumeAsync()
        {
            await foreach (var directory in queue.Reader.ReadAllAsync(token))
                try { await IngestAsync(directory, token); }
                catch (IOException) { /* Job claimed by another process; retry on the next scan. */ }
        }
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Upload links are not accepted.");
    }
}
