using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
}

public sealed class IntakeStore
{
    private readonly Func<string, long>? availableBytes;
    private const string JobColumns = "id,source,state,snapshot,message,phase,processed_bytes,total_bytes,estimated_disk_bytes,progress_at";
    public DistributionSettings Settings { get; }
    public string Root => Settings.Root;
    public IntakeStore(DistributionSettings settings, Func<string, long>? availableBytes = null)
    {
        this.availableBytes = availableBytes;
        Settings = settings;
        settings.Root = Path.GetFullPath(settings.Root);
        foreach (var directory in new[] { "incoming", "processing", "releases", "archive" })
            Directory.CreateDirectory(Path.Combine(Root, directory));
        using var gate = Lock();
        var existed = File.Exists(Path.Combine(Root, "distribution.db"));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) throw new InvalidDataException("Distribution database schema is newer than this server.");
        if (version == 1) return;
        // SQLite's backup API captures a consistent image, including committed WAL pages, before any schema change.
        if (existed)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Root, "distribution.pre-v1-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".db"), Pooling = false }.ToString());
            backup.Open(); db.BackupDatabase(backup);
        }
        using var transaction = db.BeginTransaction(); command.Transaction = transaction;
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
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "distribution.db") }.ToString());
        db.Open();
        return db;
    }
    public List<IntakeJob> List()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT " + JobColumns + " FROM jobs ORDER BY rowid";
        using var reader = command.ExecuteReader(); var list = new List<IntakeJob>();
        while (reader.Read()) list.Add(ReadJob(reader));
        return list;
    }
    public IntakeJob? Find(string id)
    {
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
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE jobs SET phase=$phase,processed_bytes=$processed,total_bytes=$total,estimated_disk_bytes=$disk,progress_at=$progressAt WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        AddProgressParameters(command, new(id, "", "", null, null, value.Phase, value.ProcessedBytes, value.TotalBytes,
            value.EstimatedAdditionalDiskBytes, DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery(); observer?.Report(value);
    });
    internal Func<string, long>? AvailableBytes => availableBytes;
    public IDisposable Lock() => new FileStream(Path.Combine(Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void Audit(string action, string subject)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO audit(at,action,job) VALUES($at,$action,$subject)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$subject", subject);
        command.ExecuteNonQuery();
    }

    public async Task<IntakeJob> IngestAsync(string directory, CancellationToken token = default,
        IProgress<PackageWorkProgress>? observer = null)
    {
        using var gate = Lock();
        var source = Path.GetFullPath(directory);
        var incoming = Path.Combine(Root, "incoming");
        if (!string.Equals(Path.GetDirectoryName(source), incoming, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Upload must be an immediate child of incoming.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
        var previous = Find(id);
        if (previous is { State: "pending" or "published" or "rejected" or "failed" or "publishing" }) return previous;
        var job = new IntakeJob(id, source, "waiting", null, null);
        try
        {
            RejectLink(source);
            if (Directory.EnumerateFiles(source, "*.uploading").Any()) { Save(job); return job; }
            var metadataPath = Path.Combine(source, "release.json");
            if (!File.Exists(metadataPath)) { Save(job); return job; }
            RejectLink(metadataPath);
            var metadataBytes = await SidecarPackageValidator.ReadDocumentAsync(metadataPath, token);
            var metadata = SidecarPackageValidator.Parse(metadataBytes);
            var zip = Path.Combine(source, metadata.PackageFile);
            if (!File.Exists(zip)) { Save(job); return job; }
            RejectLink(zip);
            job = job with { State = "validating" }; Save(job);
            var progress = Progress(id, observer);
            var estimate = IntakeDiskSpace.Estimate(metadata.PackageSize);
            progress.Report(new("snapshot-copy", 0, metadata.PackageSize, estimate));
            IntakeDiskSpace.Require(Root, estimate, availableBytes);
            var snapshot = Path.Combine(Root, "processing", id + "-" + Guid.NewGuid().ToString("N"));
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { job = (Find(id) ?? job) with { State = "failed", Message = ex.Message }; }
        Save(job); return job;
    }

    public void Reject(string id) { using var gate = Lock(); var job = Get(id); if (job.State != "pending") throw new InvalidOperationException("Only pending jobs can be rejected."); Save(job with { State = "rejected" }); }
    public void Retry(string id) { using var gate = Lock(); var job = Get(id); if (job.State != "failed") throw new InvalidOperationException("Only failed jobs can be retried."); Save(job with { State = "waiting", Message = null, Snapshot = null }); }
    public async Task ScanAsync(CancellationToken token)
    {
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, "incoming")))
        {
            token.ThrowIfCancellationRequested();
            try { await IngestAsync(directory, token); }
            catch (IOException) { /* Another writer holds the lock; retry on the next scan. */ }
        }
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Upload links are not accepted.");
    }
}
