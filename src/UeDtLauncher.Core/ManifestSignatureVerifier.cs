using System.Security.Cryptography;
using System.Text;

namespace UeDtLauncher;

public static class ManifestSignatureVerifier
{
    /// <summary>Returns true when the signature was actually verified, false when verification was skipped.</summary>
    public static async Task<bool> VerifyIfConfiguredAsync(string manifestJson, LauncherConfig config, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        return await DetachedSignatureVerifier.VerifyIfConfiguredAsync(
            manifestJson,
            config.ManifestSignatureUrl,
            config.ManifestPublicKeyPath,
            config,
            httpClient,
            cancellationToken);
    }

    public static void Verify(string payload, string signatureBase64, string publicKeyPem)
    {
        var signature = Convert.FromBase64String(signatureBase64);
        var data = Encoding.UTF8.GetBytes(payload);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);

        if (!ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("Manifest signature verification failed.");
        }
    }

    public static string Sign(string payload, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }
}
