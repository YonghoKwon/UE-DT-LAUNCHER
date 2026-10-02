using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public sealed class AtomicReleasePublishOptions
{
    public string PackageDir { get; set; } = string.Empty;
    public string ServerRoot { get; set; } = string.Empty;
    public string BaseUrlRoot { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Environment { get; set; } = "prod";
    public string Channel { get; set; } = "stable";
    public string Platform { get; set; } = "windows-x64";
    public string EntryPoint { get; set; } = string.Empty;
    public string CatalogProfile { get; set; } = "general";
    public List<string> AllowedClientProfiles { get; set; } = new();
    public string? Notes { get; set; }
    public bool SetLatest { get; set; }
    public bool DryRun { get; set; }
    public bool ReplaceExisting { get; set; }
    public bool AllowUnsigned { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? SigningKeyId { get; set; }
    public int CatalogLifetimeHours { get; set; } = 168;
}

public sealed record AtomicReleasePublishReport(
    bool Succeeded,
    bool DryRun,
    string ReleaseDirectory,
    string ManifestPath,
    string CatalogPath,
    long CatalogSequence,
    int FileCount,
    long TotalBytes,
    bool Signed,
    string CompletedAtUtc);

public static class AtomicReleasePublisher
{
    public static async Task<AtomicReleasePublishReport> PublishAsync(
        AtomicReleasePublishOptions options,
        CancellationToken cancellationToken = default)
    {
        Validate(options);
        var packageRoot = Path.GetFullPath(options.PackageDir);
        var serverRoot = Path.GetFullPath(options.ServerRoot);
        Directory.CreateDirectory(serverRoot);
        await using var publishLock = new FileStream(
            Path.Combine(serverRoot, ".publish.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose);

        var releaseRelative = Path.Combine("projects", options.ProjectId, options.Environment, options.Channel, options.Version, options.Platform);
        var finalRelease = SafePath.ResolveInside(serverRoot, releaseRelative);
        var catalogPath = SafePath.ResolveInside(serverRoot, Path.Combine("catalogs", options.CatalogProfile, "catalog.json"));
        var catalogSignaturePath = catalogPath + ".sig";
        if (Directory.Exists(finalRelease) && !options.ReplaceExisting)
            throw new IOException($"Release already exists and is immutable: {finalRelease}. Use --replace explicitly.");

        var transactionRoot = SafePath.ResolveInside(serverRoot, Path.Combine(".publishing", Guid.NewGuid().ToString("N")));
        var stagedRelease = Path.Combine(transactionRoot, "release");
        var stagedFiles = Path.Combine(stagedRelease, "files");
        var stagedManifest = Path.Combine(stagedRelease, "manifest.json");
        var stagedManifestSignature = stagedManifest + ".sig";
        var stagedCatalog = Path.Combine(transactionRoot, "catalog.json");
        var stagedCatalogSignature = stagedCatalog + ".sig";
        string? previousRelease = null;
        var releaseActivated = false;
        try
        {
            CopyDirectory(packageRoot, stagedFiles);
            var baseUrl = options.BaseUrlRoot.TrimEnd('/');
            var urlRelative = string.Join('/', "projects", options.ProjectId, options.Environment, options.Channel, options.Version, options.Platform);
            await ManifestGenerator.GenerateAsync(
                stagedFiles,
                stagedManifest,
                $"{baseUrl}/{urlRelative}/files",
                options.EntryPoint,
                options.Version,
                options.Channel,
                options.Platform,
                options.ProjectId,
                cancellationToken);
            var manifest = await JsonFiles.ReadAsync<LauncherManifest>(stagedManifest, cancellationToken);
            await VerifyManifestFilesAsync(stagedFiles, manifest, cancellationToken);

            var signed = !string.IsNullOrWhiteSpace(options.PrivateKeyPath);
            if (signed)
            {
                await WriteSignatureAsync(stagedManifest, stagedManifestSignature, options, null, null, cancellationToken);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stagedCatalog)!);
            if (File.Exists(catalogPath)) File.Copy(catalogPath, stagedCatalog, overwrite: true);
            await CatalogUpdater.UpsertReleaseAsync(stagedCatalog, new CatalogReleaseUpdate
            {
                ProjectId = options.ProjectId,
                DisplayName = options.DisplayName,
                Version = options.Version,
                Environment = options.Environment,
                Channel = options.Channel,
                Platform = options.Platform,
                ManifestUrl = $"{baseUrl}/{urlRelative}/manifest.json",
                ManifestSignatureUrl = signed ? $"{baseUrl}/{urlRelative}/manifest.json.sig" : null,
                AllowedClientProfiles = options.AllowedClientProfiles.ToList(),
                Notes = options.Notes,
                SetLatest = options.SetLatest
            }, cancellationToken);
            var catalog = await JsonFiles.ReadAsync<DistributionCatalog>(stagedCatalog, cancellationToken);
            var previousCatalog = File.Exists(catalogPath) ? await File.ReadAllTextAsync(catalogPath, cancellationToken) : null;
            catalog.SchemaVersion = 2;
            catalog.Sequence = Math.Max(1, catalog.Sequence + 1);
            var now = DateTimeOffset.UtcNow;
            catalog.GeneratedAt = now.ToString("O");
            catalog.IssuedAtUtc = now.ToString("O");
            catalog.ExpiresAtUtc = now.AddHours(Math.Clamp(options.CatalogLifetimeHours, 1, 24 * 365)).ToString("O");
            await JsonFiles.WriteAsync(stagedCatalog, catalog, cancellationToken);
            if (signed)
            {
                await WriteSignatureAsync(
                    stagedCatalog,
                    stagedCatalogSignature,
                    options,
                    previousCatalog,
                    File.Exists(catalogSignaturePath) ? catalogSignaturePath : null,
                    cancellationToken);
            }

            var report = new AtomicReleasePublishReport(
                true,
                options.DryRun,
                finalRelease,
                Path.Combine(finalRelease, "manifest.json"),
                catalogPath,
                catalog.Sequence,
                manifest.Files.Count,
                manifest.Files.Sum(file => file.Size),
                signed,
                DateTimeOffset.UtcNow.ToString("O"));
            if (options.DryRun) return report;

            Directory.CreateDirectory(Path.GetDirectoryName(finalRelease)!);
            if (Directory.Exists(finalRelease))
            {
                previousRelease = Path.Combine(transactionRoot, "previous-release");
                Directory.Move(finalRelease, previousRelease);
            }
            Directory.Move(stagedRelease, finalRelease);
            releaseActivated = true;

            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            if (signed) File.Copy(stagedCatalogSignature, catalogSignaturePath, overwrite: true);
            File.Copy(stagedCatalog, catalogPath, overwrite: true);
            await JsonFiles.WriteAsync(Path.Combine(finalRelease, "publish-report.json"), report, cancellationToken);
            if (previousRelease is not null && Directory.Exists(previousRelease)) Directory.Delete(previousRelease, recursive: true);
            return report;
        }
        catch
        {
            if (releaseActivated && Directory.Exists(finalRelease)) Directory.Delete(finalRelease, recursive: true);
            if (previousRelease is not null && Directory.Exists(previousRelease)) Directory.Move(previousRelease, finalRelease);
            throw;
        }
        finally
        {
            if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, recursive: true);
        }
    }

    private static void Validate(AtomicReleasePublishOptions options)
    {
        if (!Directory.Exists(options.PackageDir)) throw new DirectoryNotFoundException($"Package directory was not found: {options.PackageDir}");
        if (string.IsNullOrWhiteSpace(options.ProjectId) || string.IsNullOrWhiteSpace(options.Version) || string.IsNullOrWhiteSpace(options.EntryPoint))
            throw new ArgumentException("projectId, version, and entryPoint are required.");
        KnownValues.ValidateReleaseTuple(options.Platform, options.Environment, options.Channel);
        if (options.CatalogProfile is not ("general" or "developer")) throw new ArgumentException("catalogProfile must be general or developer.");
        if (options.AllowedClientProfiles.Count == 0) options.AllowedClientProfiles.Add(options.CatalogProfile);
        if (options.Environment == "prod" && options.AllowUnsigned)
            throw new InvalidOperationException("Production releases cannot use --allow-unsigned.");
        if (!options.AllowUnsigned && (string.IsNullOrWhiteSpace(options.PrivateKeyPath) || string.IsNullOrWhiteSpace(options.SigningKeyId)))
            throw new InvalidOperationException("Signed publishing requires --private-key and --key-id. Developer releases may use --allow-unsigned.");
        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath) && !File.Exists(options.PrivateKeyPath))
            throw new FileNotFoundException("Publishing private key was not found.", options.PrivateKeyPath);
        _ = new Uri(options.BaseUrlRoot, UriKind.Absolute);
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var target = SafePath.ResolveInside(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, overwrite: false);
        }
    }

    private static async Task VerifyManifestFilesAsync(string filesRoot, LauncherManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files)
        {
            var path = SafePath.ResolveInside(filesRoot, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || !await Hashing.Sha256MatchesAsync(path, file.Sha256, cancellationToken))
                throw new IOException($"Staged release verification failed: {file.Path}");
        }
    }

    private static async Task WriteSignatureAsync(
        string payloadPath,
        string signaturePath,
        AtomicReleasePublishOptions options,
        string? previousPayload,
        string? previousSignaturePath,
        CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllTextAsync(payloadPath, cancellationToken);
        var privateKey = await File.ReadAllTextAsync(options.PrivateKeyPath!, cancellationToken);
        var envelope = new DetachedSignatureEnvelope
        {
            KeyId = options.SigningKeyId!,
            Signature = ManifestSignatureVerifier.Sign(payload, privateKey),
            PayloadSha256 = PayloadHash(payload)
        };
        if (previousPayload is not null && previousSignaturePath is not null && File.Exists(previousSignaturePath))
        {
            var previousDocument = (await File.ReadAllTextAsync(previousSignaturePath, cancellationToken)).Trim();
            var previousEnvelope = previousDocument.StartsWith('{')
                ? JsonSerializer.Deserialize<DetachedSignatureEnvelope>(previousDocument, JsonFiles.Options)
                : null;
            envelope.AcceptedSignatures.Add(new DetachedSignatureEntry
            {
                KeyId = previousEnvelope?.KeyId ?? options.SigningKeyId!,
                Signature = previousEnvelope?.Signature ?? previousDocument,
                PayloadSha256 = PayloadHash(previousPayload)
            });
            if (previousEnvelope is not null) envelope.AcceptedSignatures.AddRange(previousEnvelope.AcceptedSignatures);
        }
        await JsonFiles.WriteAsync(signaturePath, envelope, cancellationToken);
    }

    private static string PayloadHash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}
