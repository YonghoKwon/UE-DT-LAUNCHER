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
        var catalog = await DownloadCatalogAsync(config, httpClient, log, cancellationToken);
        var release = SelectRelease(catalog, config);
        config.ManifestUrl = release.ManifestUrl;
        config.ManifestSignatureUrl = release.ManifestSignatureUrl;
        config.Channel = release.Channel;
        config.Environment = release.Environment;
        config.TargetPlatform = release.Platform;
        config.ResolvedReleaseVersion = release.Version;

        log?.Invoke("Catalog", $"Selected {config.ProjectId} {release.Version} / {release.Environment} / {release.Platform} / {release.Channel}", 4);
    }

    internal static async Task<DistributionCatalog> DownloadCatalogAsync(
        LauncherConfig config,
        HttpClient httpClient,
        Action<string, string, double?>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.CatalogUrl))
        {
            throw new InvalidOperationException("catalogUrl is required.");
        }

        LauncherConfigValidator.ValidateUrl(config, new Uri(config.CatalogUrl, UriKind.Absolute), "catalog");
        var catalogJson = await SecureHttpClientFactory.GetBoundedStringAsync(
            httpClient,
            config.CatalogUrl,
            config.Security.MaxCatalogBytes,
            cancellationToken);

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
        await CatalogTrustManager.ValidateAndRecordAsync(config, catalog, cancellationToken);
        return catalog;
    }

    private static async Task<bool> VerifyCatalogIfConfiguredAsync(string catalogJson, LauncherConfig config, HttpClient httpClient, CancellationToken cancellationToken)
    {
        return await DetachedSignatureVerifier.VerifyIfConfiguredAsync(
            catalogJson,
            config.CatalogSignatureUrl,
            config.CatalogPublicKeyPath,
            config,
            httpClient,
            cancellationToken);
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

        var matched = project.Releases
            .Where(release => string.Equals(release.Platform, config.TargetPlatform, StringComparison.OrdinalIgnoreCase))
            .Where(release => string.Equals(release.Environment, config.Environment, StringComparison.OrdinalIgnoreCase))
            .Where(release => string.Equals(release.Channel, config.Channel, StringComparison.OrdinalIgnoreCase))
            .Where(release => release.AllowedClientProfiles.Any(profile => string.Equals(profile, config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 0)
        {
            throw new InvalidOperationException(BuildNoMatchMessage(project, config));
        }

        if (string.Equals(config.VersionPolicy, "exact", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.RequestedVersion))
            {
                throw new InvalidOperationException("requestedVersion is required when versionPolicy is exact.");
            }

            var exact = matched.FirstOrDefault(release => string.Equals(release.Version, config.RequestedVersion, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var available = string.Join(", ", matched.Select(r => r.Version).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"No release with version '{config.RequestedVersion}' for {config.ProjectId} ({config.Environment}/{config.Channel}/{config.TargetPlatform}). Available versions: {available}.");
        }

        return matched.FirstOrDefault(release => release.IsLatest)
               ?? matched.OrderByDescending(release => VersionKey.Parse(release.Version)).First();
    }

    private static string BuildNoMatchMessage(DistributionProject project, LauncherConfig config)
    {
        var lines = new List<string>
        {
            $"No release in catalog matched this client.",
            $"Requested: project='{config.ProjectId}' platform='{config.TargetPlatform}' environment='{config.Environment}' channel='{config.Channel}' profile='{config.ClientProfile}'."
        };

        if (project.Releases.Count == 0)
        {
            lines.Add($"Project '{project.ProjectId}' has no releases in the catalog yet.");
            return string.Join(" ", lines);
        }

        lines.Add($"Available releases for '{project.ProjectId}' ({project.Releases.Count}):");
        foreach (var r in project.Releases)
        {
            lines.Add($"  - version={r.Version} platform={r.Platform} environment={r.Environment} channel={r.Channel} profiles=[{string.Join(",", r.AllowedClientProfiles)}] isLatest={(r.IsLatest ? "true" : "false")}");
        }

        // If some release differs from the request in exactly one dimension, point right at it —
        // this is the common "windows-64 vs windows-x64" typo case.
        foreach (var r in project.Releases)
        {
            var diffs = new List<string>();
            if (!string.Equals(r.Platform, config.TargetPlatform, StringComparison.OrdinalIgnoreCase)) diffs.Add($"platform (catalog '{r.Platform}' vs requested '{config.TargetPlatform}')");
            if (!string.Equals(r.Environment, config.Environment, StringComparison.OrdinalIgnoreCase)) diffs.Add($"environment (catalog '{r.Environment}' vs requested '{config.Environment}')");
            if (!string.Equals(r.Channel, config.Channel, StringComparison.OrdinalIgnoreCase)) diffs.Add($"channel (catalog '{r.Channel}' vs requested '{config.Channel}')");
            if (!r.AllowedClientProfiles.Any(p => string.Equals(p, config.ClientProfile, StringComparison.OrdinalIgnoreCase))) diffs.Add($"profile ('{config.ClientProfile}' not in [{string.Join(",", r.AllowedClientProfiles)}])");
            if (diffs.Count == 1)
            {
                lines.Add($"Hint: version {r.Version} matches except {diffs[0]}. These strings must match exactly.");
                break;
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static void ValidateClientSelection(LauncherConfig config)
    {
        if (string.Equals(config.ClientProfile, "general", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(config.TargetPlatform, "windows-x64", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(config.TargetPlatform, "linux-x64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("General users are allowed to use only windows-x64 or linux-x64 releases.");
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
