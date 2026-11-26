using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

public sealed record PublishedRelease(string ReleaseId, string JobId, string Directory, ReleaseSidecar Metadata);

public sealed class ApprovedPublisher(IntakeStore store)
{
    public List<PublishedRelease> List()
    {
        if (CatalogAuthority.For(store) is { } authority) return authority.Snapshot().Releases.ToList();
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
        if (CatalogAuthority.For(store) is { } authority) return authority.Find(value, byJob: column == "job");
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
        var authority = CatalogAuthority.For(store);
        var job = store.Get(id);
        if (job.State == "published") return FindByJob(id) ?? throw new InvalidDataException("Published release record missing.");
        if (job.State is not ("pending" or "publishing")) throw new InvalidOperationException("Job is not awaiting approval.");
        var snapshot = job.Snapshot ?? throw new InvalidDataException("Missing private snapshot.");
        var metadataBytes = await SidecarPackageValidator.ReadDocumentAsync(Path.Combine(snapshot, "release.json"), token);
        var metadata = SidecarPackageValidator.Parse(metadataBytes);
        var existing = Find(metadata.ReleaseId);
        if (existing is not null)
        {
            if (existing.JobId != id) throw PublicationConflict(id);
            if (authority is not null)
                throw new InvalidDataException("Publication catalog and job state differ.");
            using var gate = store.Lock();
            if (!store.ChangeState(id, job.State, "published", writer: gate)) throw new InvalidOperationException("Job state changed.");
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
        var final = SafePath.ResolveInsideChecked(store.Root, "releases/" + metadata.ReleaseId);
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
            // Activation ahead of the authority commit is recoverable only for this exact job and payload.
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(SafePath.ResolveInsideChecked(final, "publication.json"), token));
            if (receipt.RootElement.GetProperty("jobId").GetString() != id) throw PublicationConflict(id);
            if (!string.Equals(receipt.RootElement.GetProperty("packageSha256").GetString(), metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interrupted publication package receipt differs; operator inspection required.");
            var previousDocument = await File.ReadAllTextAsync(SafePath.ResolveInsideChecked(final, "manifest.json"), token);
            var previous = JsonSerializer.Deserialize<LauncherManifest>(previousDocument, JsonFiles.Options)
                ?? throw new InvalidDataException("Interrupted publication manifest missing.");
            var previousSignature = await JsonFiles.ReadAsync<DetachedSignatureEnvelope>(SafePath.ResolveInsideChecked(final, "manifest.json.sig"), token);
            if (previousSignature.KeyId != store.Settings.SigningKeyId)
                throw new InvalidDataException("Interrupted publication signing key changed; operator inspection required.");
            ManifestSignatureVerifier.Verify(previousDocument, previousSignature.Signature, await File.ReadAllTextAsync(store.Settings.SigningKeyPath, token));
            if (previous.AppId != metadata.ProjectId || previous.Version != metadata.Version || previous.Channel != metadata.Channel ||
                previous.Platform != metadata.Platform || previous.EntryPoint != manifest.EntryPoint || previous.Files.Count != manifest.Files.Count)
                throw new InvalidDataException("Interrupted publication does not match the approved package.");
            var expectedFiles = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var expectedInventory = expectedFiles.Keys.Select(path => "files/" + path)
                .Concat(["manifest.json", "manifest.json.sig", "publication.json"])
                .ToHashSet(StringComparer.Ordinal);
            var actualInventory = DistributionBackup.Files(final)
                .Select(path => Path.GetRelativePath(final, path).Replace(Path.DirectorySeparatorChar, '/'));
            if (!expectedInventory.SetEquals(actualInventory))
                throw new InvalidDataException("Interrupted publication inventory differs; operator inspection required.");
            foreach (var file in previous.Files)
                if (!expectedFiles.Remove(file.Path, out var expected) || expected.Size != file.Size || expected.Executable != file.Executable ||
                    !string.Equals(expected.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    !await Hashing.Sha256MatchesAsync(SafePath.ResolveInsideChecked(final, "files/" + file.Path), file.Sha256, token))
                    throw new InvalidDataException("Interrupted publication requires operator inspection.");
            recovered = true;
        }
        progress.Report(new("activate", metadata.PackageSize, metadata.PackageSize, 0));
        token.ThrowIfCancellationRequested();
        if (authority is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                // Flush staged or receipt-verified files outside the global writer lease.
                var durableRoot = recovered ? final : staged;
                foreach (var file in manifest.Files.Select(file => "files/" + file.Path)
                             .Concat(["manifest.json", "manifest.json.sig", "publication.json"]))
                {
                    token.ThrowIfCancellationRequested();
                    using var handle = new FileStream(SafePath.ResolveInsideChecked(durableRoot, file), FileMode.Open,
                        FileAccess.Write, FileShare.Read);
                    handle.Flush(true);
                }
            }
            PublishedRelease published;
            try
            {
                published = authority.Publish(id, metadata, metadataBytes, work, Activate);
                work.MarkPublished();
            }
            catch (PublicationIdConflictException) { throw new IOException(PublicationConflictMessage); }
            observer?.Report(new("published", metadata.PackageSize, metadata.PackageSize, 0));
            return published;
        }

        using (var gate = store.Lock())
        {
            // Two jobs can build the same ReleaseId in parallel, but only one may activate it.
            existing = Find(metadata.ReleaseId);
            if (existing is not null)
            {
                if (existing.JobId != id) throw PublicationConflict(id, gate);
                if (!store.ChangeState(id, "publishing", "published", writer: gate)) throw new InvalidOperationException("Job state changed.");
                return existing;
            }
            Activate();

            using (var database = store.DatabaseWrite())
            using (var db = store.Open())
            using (var transaction = db.BeginTransaction())
            using (var command = db.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO releases(id,job,directory,metadata) VALUES($id,$job,$dir,$metadata)";
                command.Parameters.AddWithValue("$id", metadata.ReleaseId); command.Parameters.AddWithValue("$job", id);
                command.Parameters.AddWithValue("$dir", final); command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, JsonFiles.Options));
                command.ExecuteNonQuery(); transaction.Commit();
            }
            // Until PROD-01S5 integrates X4, a committed release can be reconciled by the existing approval retry.
            if (!store.ChangeState(id, "publishing", "published", writer: gate)) throw new InvalidOperationException("Job state changed.");
        }
        progress.Report(new("published", metadata.PackageSize, metadata.PackageSize, 0));
        return new(metadata.ReleaseId, id, final, metadata);

        void Activate()
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (Directory.Exists(final))
            {
                if (!recovered) throw new IOException("A release directory appeared during publication; retry approval to verify its receipt.");
            }
            else Directory.Move(staged, final);
        }
    }

    internal const string PublicationConflictMessage = "Release version already belongs to another publication; use a new version.";

    private IOException PublicationConflict(string id, IDisposable? writer = null)
    {
        // A known losing publisher is not a recoverable activation journal. Keep retry available.
        store.ChangeState(id, "publishing", "failed", message: PublicationConflictMessage, writer: writer);
        return new IOException(PublicationConflictMessage);
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
