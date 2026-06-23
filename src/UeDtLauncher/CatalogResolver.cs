using System.Text.Json;

namespace UeDtLauncher;

public static class CatalogResolver
{
    public static async Task ResolveAsync(LauncherConfig config, HttpClient httpClient, Action<string, string, double?>? log = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.CatalogUrl))
        {
            return;
        }

        ValidateClientSelection(config);

        log?.Invoke("Catalog", "Downloading release catalog...", 2);
        using var response = await httpClient.GetAsync(config.CatalogUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var catalogJson = await response.Content.ReadAsStringAsync(cancellationToken);

        var signatureVerified = await VerifyCatalogIfConfiguredAsync(catalogJson, config, httpClient, cancellationToken);
        if (!signatureVerified)
        {
            if (config.RequireSignedManifests)
            {
                throw new InvalidOperationException("requireSignedManifests is enabled, but catalogSignatureUrl or catalogPublicKeyPath is not configured.");
            }

            log?.Invoke("Security", "WARNING: catalog signature verification skipped (no signature URL or public key configured).", null);
        }

        var catalog = JsonSerializer.Deserialize<DistributionCatalog>(catalogJson, JsonFiles.Options)
            ?? throw new InvalidOperationException("Release catalog JSON was empty or invalid.");

        var release = SelectRelease(catalog, config);
        config.ManifestUrl = release.ManifestUrl;
        config.ManifestSignatureUrl = release.ManifestSignatureUrl;
        config.Channel = release.Channel;
        config.Environment = release.Environment;
        config.TargetPlatform = release.Platform;

        log?.Invoke("Catalog", $"Selected {config.ProjectId} {release.Version} / {release.Environment} / {release.Platform} / {release.Channel}", 4);
    }

    private static async Task<bool> VerifyCatalogIfConfiguredAsync(string catalogJson, LauncherConfig config, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.CatalogSignatureUrl) || string.IsNullOrWhiteSpace(config.CatalogPublicKeyPath))
        {
            return false;
        }

        if (!File.Exists(config.CatalogPublicKeyPath))
        {
            throw new FileNotFoundException("Catalog public key file was not found.", config.CatalogPublicKeyPath);
        }

        var signatureBase64 = await httpClient.GetStringAsync(config.CatalogSignatureUrl, cancellationToken);
        ManifestSignatureVerifier.Verify(catalogJson, signatureBase64.Trim(), await File.ReadAllTextAsync(config.CatalogPublicKeyPath, cancellationToken));
        return true;
    }

    internal static DistributionRelease SelectRelease(DistributionCatalog catalog, LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ProjectId))
        {
            throw new InvalidOperationException("projectId is required when catalogUrl is configured.");
        }

        var project = catalog.Projects.FirstOrDefault(project => string.Equals(project.ProjectId, config.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            throw new InvalidOperationException($"Project was not found in catalog: {config.ProjectId}");
        }

        var candidates = project.Releases
            .Where(release => string.Equals(release.Platform, config.TargetPlatform, StringComparison.OrdinalIgnoreCase))
            .Where(release => string.Equals(release.Environment, config.Environment, StringComparison.OrdinalIgnoreCase))
            .Where(release => string.Equals(release.Channel, config.Channel, StringComparison.OrdinalIgnoreCase))
            .Where(release => release.AllowedClientProfiles.Any(profile => string.Equals(profile, config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (string.Equals(config.VersionPolicy, "exact", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.RequestedVersion))
            {
                throw new InvalidOperationException("requestedVersion is required when versionPolicy is exact.");
            }

            candidates = candidates
                .Where(release => string.Equals(release.Version, config.RequestedVersion, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            var latest = candidates.FirstOrDefault(release => release.IsLatest);
            if (latest is not null)
            {
                return latest;
            }

            candidates = candidates
                .OrderByDescending(release => VersionKey.Parse(release.Version))
                .ToList();
        }

        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No allowed release matched this client configuration.");
    }

    internal static void ValidateClientSelection(LauncherConfig config)
    {
        if (string.Equals(config.ClientProfile, "general", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(config.TargetPlatform, "windows-x64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("General users are allowed to use only windows-x64 releases.");
            }

            if (!string.Equals(config.Environment, "prod", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("General users are allowed to use only prod releases.");
            }

            if (!string.Equals(config.Channel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("General users are allowed to use only stable releases.");
            }

            if (!string.Equals(config.VersionPolicy, "latest", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("General users are allowed to use only latest release policy.");
            }
        }
    }

    private readonly record struct VersionKey(int Major, int Minor, int Patch, string Raw) : IComparable<VersionKey>
    {
        public static VersionKey Parse(string raw)
        {
            var cleaned = raw.Trim().TrimStart('v', 'V');
            var main = cleaned.Split('-', '+')[0];
            var parts = main.Split('.');
            return new VersionKey(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2), raw);
        }

        public int CompareTo(VersionKey other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;
            var patch = Patch.CompareTo(other.Patch);
            if (patch != 0) return patch;
            return string.Compare(Raw, other.Raw, StringComparison.OrdinalIgnoreCase);
        }

        private static int ParsePart(string[] parts, int index)
        {
            if (index >= parts.Length) return 0;
            return int.TryParse(parts[index], out var value) ? value : 0;
        }
    }
}
