namespace UeDtLauncher;

public sealed record LaunchPolicyDecision(bool Allowed, string Code);

public static class LaunchPolicy
{
    public static async Task<LaunchPolicyDecision> VerifyOnlineAsync(LauncherConfig config, CancellationToken token = default)
    {
        using var http = SecureHttpClientFactory.Create(config);
        if (!string.IsNullOrWhiteSpace(config.DistributionServerUrl) || !string.IsNullOrWhiteSpace(config.CatalogUrl))
        {
            var expected = config.SelectedRelease;
            var catalog = await CatalogResolver.DownloadCatalogAsync(config, http, cancellationToken: token);
            if (!config.CatalogAuthenticated) throw new InvalidDataException("Online launch requires authenticated metadata.");
            if (expected is not null)
            {
                var pinned = new LauncherConfig { ProjectId = expected.ProjectId, Environment = expected.Environment, Channel = expected.Channel,
                    TargetPlatform = expected.Platform, RequestedVersion = expected.Version, VersionPolicy = "exact", ClientProfile = config.ClientProfile };
                var selected = CatalogResolver.SelectRelease(catalog, pinned);
                if (selected.Version != expected.Version) throw new InvalidDataException("Online release identity changed.");
            }
            else CatalogResolver.SelectRelease(catalog, config);
        }
        else
        {
            var document = await ManifestDownloader.DownloadAsync(config, http, cancellationToken: token);
            if (!document.SignatureVerified || string.IsNullOrWhiteSpace(config.Security.CredentialName))
                throw new InvalidDataException("New launch requires online authentication and signed metadata; update legacy configuration.");
        }
        return new(true, "online-authorized");
    }
}
