using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record IntakeJob(string Id, string Source, string State, string? Snapshot, string? Message);

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
    public DistributionSettings Settings { get; }
    public string Root => Settings.Root;
    public IntakeStore(DistributionSettings settings)
    {
        Settings = settings;
        settings.Root = Path.GetFullPath(settings.Root);
        foreach (var directory in new[] { "incoming", "processing", "releases", "archive" })
            Directory.CreateDirectory(Path.Combine(Root, directory));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,source TEXT NOT NULL,state TEXT NOT NULL,snapshot TEXT,message TEXT);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,at TEXT NOT NULL,action TEXT NOT NULL,job TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
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
        command.CommandText = "SELECT id,source,state,snapshot,message FROM jobs ORDER BY rowid";
        using var reader = command.ExecuteReader(); var list = new List<IntakeJob>();
        while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return list;
    }
    public IntakeJob Get(string id) => List().Single(j => j.Id == id);
    public void Save(IntakeJob job)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs VALUES($id,$source,$state,$snapshot,$message)
            ON CONFLICT(id) DO UPDATE SET state=$state,snapshot=$snapshot,message=$message;
            INSERT INTO audit(at,action,job) VALUES($at,$state,$id);
            """;
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$source", job.Source);
        command.Parameters.AddWithValue("$state", job.State); command.Parameters.AddWithValue("$snapshot", (object?)job.Snapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)job.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public IDisposable Lock() => new FileStream(Path.Combine(Root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public async Task<IntakeJob> IngestAsync(string directory, CancellationToken token = default)
    {
        using var gate = Lock();
        var source = Path.GetFullPath(directory);
        var incoming = Path.Combine(Root, "incoming");
        if (!string.Equals(Path.GetDirectoryName(source), incoming, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Upload must be an immediate child of incoming.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];
        var previous = List().SingleOrDefault(j => j.Id == id);
        if (previous is { State: "pending" or "published" or "rejected" or "failed" or "publishing" }) return previous;
        var job = new IntakeJob(id, source, "waiting", null, null);
        try
        {
            RejectLink(source);
            if (Directory.EnumerateFiles(source, "*.uploading").Any()) { Save(job); return job; }
            var metadataPath = Path.Combine(source, "release.json");
            if (!File.Exists(metadataPath)) { Save(job); return job; }
            RejectLink(metadataPath);
            var metadata = await SidecarPackageValidator.ReadAsync(metadataPath, token);
            var zip = Path.Combine(source, metadata.PackageFile);
            if (!File.Exists(zip)) { Save(job); return job; }
            RejectLink(zip);
            job = job with { State = "validating" }; Save(job);
            var snapshot = Path.Combine(Root, "processing", id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshot);
            // Copies are private and immutable to upload users. Approval uses only this snapshot.
            await JsonFiles.WriteAsync(Path.Combine(snapshot, "release.json"), metadata, token);
            var capturedZip = Path.Combine(snapshot, metadata.PackageFile);
            await using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(capturedZip, FileMode.CreateNew, FileAccess.Write))
            {
                if (input.Length != metadata.PackageSize || input.Length > Settings.Limits.MaxZipBytes)
                    throw new InvalidDataException("ZIP size mismatch or limit exceeded.");
                await input.CopyToAsync(output, token);
            }
            await SidecarPackageValidator.ValidateAndExtractAsync(capturedZip, metadata, Path.Combine(snapshot, "payload"), Settings.Limits, token);
            job = job with { State = "pending", Snapshot = snapshot };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { job = job with { State = "failed", Message = ex.Message }; }
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
