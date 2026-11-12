using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> GenerateSidecarAsync(string[] args)
    {
        var zip = Required(args, "--zip");
        var metadata = new ReleaseSidecar
        {
            ProjectId = Required(args, "--project-id"),
            DisplayName = Get(args, "--display-name") ?? Required(args, "--project-id"),
            Version = Required(args, "--version"),
            Environment = Get(args, "--environment") ?? "prod",
            Channel = Get(args, "--channel") ?? "stable",
            Platform = Required(args, "--platform"),
            PayloadRoot = Get(args, "--payload-root") ?? ".",
            EntryPoint = Required(args, "--entry-point"),
            ExecutablePaths = (Get(args, "--executable-paths") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Notes = Get(args, "--notes"),
            HeroPath = Get(args, "--hero-path"),
            ThumbnailPath = Get(args, "--thumbnail-path")
        };
        var output = Get(args, "--output") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip))!, "release.json");
        await SidecarPackageValidator.GenerateAsync(zip, output, metadata);
        Console.WriteLine("Created external release metadata: " + output);
        return 0;
    }

    private static async Task<int> PublishReleaseAsync(string[] args)
    {
        var options = new AtomicReleasePublishOptions
        {
            PackageDir = Required(args, "--package-dir"),
            ServerRoot = Required(args, "--server-root"),
            BaseUrlRoot = Required(args, "--base-url-root"),
            ProjectId = Required(args, "--project-id"),
            DisplayName = Get(args, "--display-name") ?? Required(args, "--project-id"),
            Version = Required(args, "--version"),
            Environment = Get(args, "--environment") ?? "prod",
            Channel = Get(args, "--channel") ?? "stable",
            Platform = Required(args, "--platform"),
            EntryPoint = Required(args, "--entry-point"),
            CatalogProfile = Get(args, "--catalog-profile") ?? "general",
            AllowedClientProfiles = (Get(args, "--allowed-profiles") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            Notes = Get(args, "--notes"),
            SetLatest = Has(args, "--set-latest"),
            DryRun = Has(args, "--dry-run"),
            ReplaceExisting = Has(args, "--replace"),
            AllowUnsigned = Has(args, "--allow-unsigned"),
            PrivateKeyPath = Get(args, "--private-key"),
            SigningKeyId = Get(args, "--key-id")
        };
        var report = await AtomicReleasePublisher.PublishAsync(options);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonFiles.Options));
        return 0;
    }

    private static async Task<int> GenerateSigningKeyAsync(string[] args)
    {
        var privateKeyPath = Required(args, "--private-key");
        var publicKeyPath = Required(args, "--public-key");
        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
            throw new IOException("Signing key output already exists; refusing to overwrite it.");
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicKeyPath))!);
        await File.WriteAllTextAsync(privateKeyPath, key.ExportECPrivateKeyPem());
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Signing key pair generated. Keep private key offline: {privateKeyPath}");
        Console.WriteLine($"Public key: {publicKeyPath}");
        return 0;
    }

    private static async Task<int> GenerateManifestAsync(string[] args)
    {
        var packageDir = Required(args, "--package-dir");
        var output = Get(args, "--output") ?? Path.Combine(packageDir, "manifest.json");
        var baseUrl = Required(args, "--base-url");
        var entryPoint = Required(args, "--entry-point");
        var version = Get(args, "--version") ?? "1.0.0";
        var channel = Get(args, "--channel") ?? "stable";
        var platform = Get(args, "--platform") ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");
        var appId = Get(args, "--app-id");

        KnownValues.ValidatePlatform(platform);
        KnownValues.ValidateChannel(channel);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("WARNING: --base-url should be an absolute http(s) URL the client can reach. Manifest file URLs are built from it.");
        }

        await ManifestGenerator.GenerateAsync(packageDir, output, baseUrl, entryPoint, version, channel, platform, appId);
        return 0;
    }

    private static async Task<int> UpdateCatalogAsync(string[] args)
    {
        var catalogPath = Required(args, "--catalog");
        var projectId = Required(args, "--project-id");
        var version = Required(args, "--version");
        var environment = Get(args, "--environment") ?? "prod";
        var channel = Get(args, "--channel") ?? "stable";
        var platform = Get(args, "--platform") ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");

        KnownValues.ValidateReleaseTuple(platform, environment, channel);

        if (Has(args, "--remove"))
        {
            await CatalogUpdater.RemoveReleaseAsync(catalogPath, projectId, version, environment, channel, platform, Has(args, "--remove-project-if-empty"));
            Console.WriteLine($"Release removed (if present): {projectId} {version} {environment}/{channel}/{platform}");
            Console.WriteLine($"Catalog updated: {catalogPath}");
            return 0;
        }

        var profiles = (Get(args, "--allowed-profiles") ?? "general")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        await CatalogUpdater.UpsertReleaseAsync(catalogPath, new CatalogReleaseUpdate
        {
            ProjectId = projectId,
            DisplayName = Get(args, "--display-name") ?? projectId,
            Version = version,
            Environment = environment,
            Channel = channel,
            Platform = platform,
            ManifestUrl = Required(args, "--manifest-url"),
            ManifestSignatureUrl = Get(args, "--manifest-signature-url"),
            AllowedClientProfiles = profiles,
            Notes = Get(args, "--notes"),
            SetLatest = Has(args, "--set-latest")
        });

        Console.WriteLine($"Release upserted: {projectId} {version} {environment}/{channel}/{platform} (profiles: {string.Join(",", profiles)})");
        Console.WriteLine($"Catalog updated: {catalogPath}");
        return 0;
    }

    private static async Task<int> ListReleasesAsync(string[] args)
    {
        var catalogPath = Required(args, "--catalog");
        var projectFilter = Get(args, "--project");
        var catalog = await JsonFiles.ReadAsync<DistributionCatalog>(catalogPath);

        var projects = catalog.Projects
            .Where(p => string.IsNullOrWhiteSpace(projectFilter) || string.Equals(p.ProjectId, projectFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projects.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(projectFilter) ? "Catalog has no projects." : $"Project not found: {projectFilter}");
            return 0;
        }

        foreach (var project in projects)
        {
            Console.WriteLine($"{project.ProjectId}  ({project.DisplayName})  — {project.Releases.Count} release(s)");
            foreach (var r in project.Releases.OrderBy(r => r.Environment).ThenBy(r => r.Channel).ThenBy(r => r.Platform).ThenBy(r => r.Version))
            {
                var latest = r.IsLatest ? " [latest]" : string.Empty;
                Console.WriteLine($"  {r.Version,-14} {r.Environment,-5}/{r.Channel,-7}/{r.Platform,-12} profiles=[{string.Join(",", r.AllowedClientProfiles)}]{latest}");
            }
        }

        return 0;
    }

    private static async Task<int> GenerateNginxAclAsync(string[] args)
    {
        var allowlistPath = Required(args, "--allowlist");
        var output = Get(args, "--output");
        var allowlist = await JsonFiles.ReadAsync<ProjectIpAllowlist>(allowlistPath);
        var config = NginxAclGenerator.Generate(allowlist);

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(config);
        }
        else
        {
            await File.WriteAllTextAsync(output, config);
            Console.WriteLine($"Nginx per-project IP ACL written: {output}");
            Console.WriteLine("Include it ABOVE the generic /projects/ location blocks, then: sudo nginx -t && sudo systemctl reload nginx");
        }

        return 0;
    }

    private static async Task<int> SignManifestAsync(string[] args)
    {
        var manifestPath = Required(args, "--manifest");
        var privateKeyPath = Required(args, "--private-key");
        var keyId = Get(args, "--key-id");
        var output = Get(args, "--output") ?? manifestPath + ".sig";
        var payload = await File.ReadAllTextAsync(manifestPath);
        var privateKey = await File.ReadAllTextAsync(privateKeyPath);
        var signature = ManifestSignatureVerifier.Sign(payload, privateKey);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            await File.WriteAllTextAsync(output, signature);
        }
        else
        {
            await JsonFiles.WriteAsync(output, new DetachedSignatureEnvelope { KeyId = keyId, Signature = signature });
        }
        Console.WriteLine($"Manifest signature written: {output}");
        return 0;
    }

}
