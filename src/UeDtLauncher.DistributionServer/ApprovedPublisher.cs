using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record PublishedRelease(string ReleaseId, string JobId, string Directory, ReleaseSidecar Metadata);

public sealed class ApprovedPublisher(IntakeStore store)
{
    public List<PublishedRelease> List()
    {
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS releases(id TEXT PRIMARY KEY,job TEXT NOT NULL,directory TEXT NOT NULL,metadata TEXT NOT NULL)";
        command.ExecuteNonQuery(); command.CommandText = "SELECT id,job,directory,metadata FROM releases ORDER BY rowid";
        using var reader = command.ExecuteReader(); var items = new List<PublishedRelease>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            JsonSerializer.Deserialize<ReleaseSidecar>(reader.GetString(3), JsonFiles.Options)!));
        return items;
    }

    public async Task<PublishedRelease> ApproveAsync(string id, CancellationToken token = default)
    {
        using var gate = store.Lock();
        var job = store.Get(id);
        if (job.State == "published") return List().Single(r => r.JobId == id);
        if (job.State is not ("pending" or "publishing")) throw new InvalidOperationException("Job is not awaiting approval.");
        var snapshot = job.Snapshot ?? throw new InvalidDataException("Missing private snapshot.");
        var metadata = await SidecarPackageValidator.ReadAsync(Path.Combine(snapshot, "release.json"), token);
        var existing = List().SingleOrDefault(r => r.ReleaseId == metadata.ReleaseId);
        if (existing is not null)
        {
            if (existing.JobId != id) throw new IOException("Release version already published; use a new version.");
            store.Save(job with { State = "published" }); return existing;
        }
        // A publishing journal is committed before touching release directories. Repeating approval recovers it.
        store.Save(job with { State = "publishing", Message = null });
        var build = Path.Combine(store.Root, "processing", "publish-" + id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(build);
        var payload = await SidecarPackageValidator.ValidateAndExtractAsync(Path.Combine(snapshot, metadata.PackageFile), metadata,
            Path.Combine(build, "unpacked"), store.Settings.Limits, token);
        var staged = Path.Combine(build, "release"); Directory.CreateDirectory(staged);
        Directory.Move(payload, Path.Combine(staged, "files"));
        var final = SafePath.ResolveInside(Path.Combine(store.Root, "releases"), metadata.ReleaseId);
        var manifestPath = Path.Combine(staged, "manifest.json");
        await ManifestGenerator.GenerateAsync(packageDir: Path.Combine(staged, "files"), outputPath: manifestPath,
            baseUrl: store.Settings.PublicUrl.TrimEnd('/') + "/releases/" + metadata.ReleaseId + "/files",
            entryPoint: metadata.EntryPoint, version: metadata.Version, channel: metadata.Channel,
            platform: metadata.Platform, appId: metadata.ProjectId, cancellationToken: token);
        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(manifestPath, token);
        foreach (var file in manifest.Files)
            file.Executable = metadata.Platform == "linux-x64" && metadata.ExecutablePaths.Append(metadata.EntryPoint).Contains(file.Path, StringComparer.Ordinal);
        await JsonFiles.WriteAsync(manifestPath, manifest, token);
        await File.WriteAllTextAsync(manifestPath + ".sig", Sign(await File.ReadAllTextAsync(manifestPath, token)), token);
        await JsonFiles.WriteAsync(Path.Combine(staged, "publication.json"), new { JobId = id, metadata.PackageSha256 }, token);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (Directory.Exists(final))
        {
            // Crash after directory activation but before DB commit: only adopt this exact job's complete release.
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(final, "publication.json"), token));
            if (receipt.RootElement.GetProperty("jobId").GetString() != id)
                throw new IOException("Unregistered release directory belongs to a different publication.");
            var previous = await JsonFiles.ReadAsync<LauncherManifest>(Path.Combine(final, "manifest.json"), token);
            foreach (var file in previous.Files)
                if (!await Hashing.Sha256MatchesAsync(SafePath.ResolveInside(Path.Combine(final, "files"), file.Path), file.Sha256, token))
                    throw new InvalidDataException("Interrupted publication requires operator inspection.");
        }
        else Directory.Move(staged, final);

        using (var db = store.Open())
        using (var transaction = db.BeginTransaction())
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO releases VALUES($id,$job,$dir,$metadata); INSERT INTO audit(at,action,job) VALUES($at,'published',$job);";
            command.Parameters.AddWithValue("$id", metadata.ReleaseId); command.Parameters.AddWithValue("$job", id);
            command.Parameters.AddWithValue("$dir", final); command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, JsonFiles.Options));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery(); transaction.Commit();
        }
        store.Save(job with { State = "published" });
        return new(metadata.ReleaseId, id, final, metadata);
    }

    public string Sign(string payload)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(store.Settings.SigningKeyPath));
        return JsonSerializer.Serialize(new DetachedSignatureEnvelope
        {
            KeyId = store.Settings.SigningKeyId,
            Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256))
        }, JsonFiles.Options);
    }
}
