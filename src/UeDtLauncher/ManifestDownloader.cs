using System.Text.Json;

namespace UeDtLauncher;

internal sealed record ManifestDocument(LauncherManifest Manifest, string Json);

internal static class ManifestDownloader
{
    internal static async Task<ManifestDocument> DownloadAsync(
        LauncherConfig config,
        HttpClient httpClient,
        Action<string, string, double?>? log = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(config.ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var signatureVerified = await ManifestSignatureVerifier.VerifyIfConfiguredAsync(
            json,
            config,
            httpClient,
            cancellationToken);
        if (!signatureVerified)
        {
            if (config.RequireSignedManifests)
            {
                throw new InvalidOperationException(
                    "requireSignedManifests is enabled, but manifestSignatureUrl or manifestPublicKeyPath is not configured.");
            }

            log?.Invoke(
                "Security",
                "WARNING: manifest signature verification skipped (no signature URL or public key configured).",
                null);
        }

        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, JsonFiles.Options)
                       ?? throw new InvalidOperationException("Remote manifest JSON was empty or invalid.");
        LauncherEngine.ValidateManifest(manifest);
        return new ManifestDocument(manifest, json);
    }
}
