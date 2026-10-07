using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record PublishedRelease(string ReleaseId, string JobId, string Directory, ReleaseSidecar Metadata);

public sealed class ApprovedPublisher(IntakeStore store)
{
    public List<PublishedRelease> List()
    {
        using var measurement = DistributionPerformance.MeasureDatabase("releases-list");
        using var database = store.DatabaseRead();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,job,directory,metadata FROM releases ORDER BY rowid";
        using var reader = command.ExecuteReader(); var items = new List<PublishedRelease>();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            JsonSerializer.Deserialize<ReleaseSidecar>(reader.GetString(3), JsonFiles.Options)!));
        return items;
    }
    public PublishedRelease? Find(string releaseId) => FindOne("id", releaseId);
    public PublishedRelease? FindByJob(string jobId) => FindOne("job", jobId);
    private PublishedRelease? FindOne(string column, string value)
    {
        using var measurement = DistributionPerformance.MeasureDatabase("release-find");
        using var database = store.DatabaseRead();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,job,directory,metadata FROM releases WHERE " + column + "=$value";
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            JsonSerializer.Deserialize<ReleaseSidecar>(reader.GetString(3), JsonFiles.Options)!) : null;
    }

    public async Task<PublishedRelease> ApproveAsync(string id, CancellationToken token = default,
        IProgress<PackageWorkProgress>? observer = null)
    {
        using var operation = DistributionMaintenanceLease.Acquire(store.Root, false);
        using var jobLock = store.LockJob(id);
        var job = store.Get(id);
        if (job.State == "published") return FindByJob(id) ?? throw new InvalidDataException("Published release record missing.");
        if (job.State is not ("pending" or "publishing")) throw new InvalidOperationException("Job is not awaiting approval.");
        var snapshot = job.Snapshot ?? throw new InvalidDataException("Missing private snapshot.");
        var metadata = await SidecarPackageValidator.ReadAsync(Path.Combine(snapshot, "release.json"), token);
        var existing = Find(metadata.ReleaseId);
        if (existing is not null)
        {
            if (existing.JobId != id) throw PublicationConflict(id);
            using var gate = store.Lock();
            if (!store.ChangeState(id, job.State, "published")) throw new InvalidOperationException("Job state changed.");
            return existing;
        }
        // A publishing journal is committed before touching release directories. Repeating approval recovers it.
        var build = Path.Combine(store.Root, "processing", "publish-" + id + "-" + Guid.NewGuid().ToString("N"));
        using var work = new IntakeWorkClaim(store, job, "publishing", build, snapshot, "pending", "publishing");
        var progress = store.Progress(id, observer);
        Directory.CreateDirectory(build);
        var payload = await SidecarPackageValidator.ValidateAndExtractAsync(Path.Combine(snapshot, metadata.PackageFile), metadata,
            Path.Combine(build, "unpacked"), store.Settings.Limits, token, progress, store.AvailableBytes);
        var staged = Path.Combine(build, "release"); Directory.CreateDirectory(staged);
        Directory.Move(payload, Path.Combine(staged, "files"));
        var final = SafePath.ResolveInside(Path.Combine(store.Root, "releases"), metadata.ReleaseId);
        var manifestPath = Path.Combine(staged, "manifest.json");
        progress.Report(new("manifest", 0, 0, 0));
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
        // Validate interrupted activation outside the short global writer lock. Releases are immutable.
        var recovered = false;
        if (Directory.Exists(final))
        {
            // Crash after directory activation but before DB commit: only adopt this exact job's complete release.
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(final, "publication.json"), token));
            if (receipt.RootElement.GetProperty("jobId").GetString() != id) throw PublicationConflict(id);
            if (!string.Equals(receipt.RootElement.GetProperty("packageSha256").GetString(), metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interrupted publication package receipt differs; operator inspection required.");
            var previousDocument = await File.ReadAllTextAsync(Path.Combine(final, "manifest.json"), token);
            var previous = JsonSerializer.Deserialize<LauncherManifest>(previousDocument, JsonFiles.Options)
                ?? throw new InvalidDataException("Interrupted publication manifest missing.");
            var previousSignature = await JsonFiles.ReadAsync<DetachedSignatureEnvelope>(Path.Combine(final, "manifest.json.sig"), token);
            if (previousSignature.KeyId != store.Settings.SigningKeyId)
                throw new InvalidDataException("Interrupted publication signing key changed; operator inspection required.");
            ManifestSignatureVerifier.Verify(previousDocument, previousSignature.Signature, await File.ReadAllTextAsync(store.Settings.SigningKeyPath, token));
            if (previous.AppId != metadata.ProjectId || previous.Version != metadata.Version || previous.Channel != metadata.Channel ||
                previous.Platform != metadata.Platform || previous.EntryPoint != manifest.EntryPoint || previous.Files.Count != manifest.Files.Count)
                throw new InvalidDataException("Interrupted publication does not match the approved package.");
            var expectedFiles = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            foreach (var file in previous.Files)
                if (!expectedFiles.Remove(file.Path, out var expected) || expected.Size != file.Size || expected.Executable != file.Executable ||
                    !string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !await Hashing.Sha256MatchesAsync(SafePath.ResolveInside(Path.Combine(final, "files"), file.Path), file.Sha256, token))
                    throw new InvalidDataException("Interrupted publication requires operator inspection.");
            recovered = true;
        }
        progress.Report(new("activate", metadata.PackageSize, metadata.PackageSize, 0));
        token.ThrowIfCancellationRequested();
        using (var gate = store.Lock())
        {
            // Two jobs can build the same ReleaseId in parallel, but only one may activate it.
            existing = Find(metadata.ReleaseId);
            if (existing is not null)
            {
                if (existing.JobId != id) throw PublicationConflict(id);
                if (!store.ChangeState(id, "publishing", "published")) throw new InvalidOperationException("Job state changed.");
                return existing;
            }
            if (store.Get(id).State != "publishing") throw new InvalidOperationException("Job state changed before activation.");
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (Directory.Exists(final))
            {
                if (!recovered) throw new IOException("A release directory appeared during publication; retry approval to verify its receipt.");
            }
            else Directory.Move(staged, final);

            using var database = store.DatabaseWrite();
            using var db = store.Open(); using var transaction = db.BeginTransaction(); using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO releases(id,job,directory,metadata) VALUES($id,$job,$dir,$metadata);
                UPDATE jobs SET state='published',phase='published',processed_bytes=$size,total_bytes=$size,estimated_disk_bytes=0,progress_at=$at WHERE id=$job AND state='publishing';
                INSERT INTO audit(at,action,job) VALUES($at,'published',$job);
                """;
            command.Parameters.AddWithValue("$id", metadata.ReleaseId); command.Parameters.AddWithValue("$job", id);
            command.Parameters.AddWithValue("$dir", final); command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, JsonFiles.Options));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$size", metadata.PackageSize);
            command.ExecuteNonQuery(); transaction.Commit();
        }
        progress.Report(new("published", metadata.PackageSize, metadata.PackageSize, 0));
        return new(metadata.ReleaseId, id, final, metadata);
    }

    private IOException PublicationConflict(string id)
    {
        const string message = "Release version already belongs to another publication; use a new version.";
        // A known losing publisher is not a recoverable activation journal. Keep retry available.
        store.ChangeState(id, "publishing", "failed", message: message);
        return new IOException(message);
    }

    public string Sign(string payload)
    {
        using var measurement=DistributionPerformance.MeasurePhase("signing");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(store.Settings.SigningKeyPath));
        return JsonSerializer.Serialize(new DetachedSignatureEnvelope
        {
            KeyId = store.Settings.SigningKeyId,
            Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256))
        }, JsonFiles.Options);
    }
}
