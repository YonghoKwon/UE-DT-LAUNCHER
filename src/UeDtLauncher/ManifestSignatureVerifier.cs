using System.Security.Cryptography;
using System.Text;

namespace UeDtLauncher;

public static class ManifestSignatureVerifier
{
    public static async Task VerifyIfConfiguredAsync(string manifestJson, LauncherConfig config, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ManifestSignatureUrl) || string.IsNullOrWhiteSpace(config.ManifestPublicKeyPath))
        {
            return;
        }

        if (!File.Exists(config.ManifestPublicKeyPath))
        {
            throw new FileNotFoundException("Manifest public key file was not found.", config.ManifestPublicKeyPath);
        }

        var signatureBase64 = await httpClient.GetStringAsync(config.ManifestSignatureUrl, cancellationToken);
        Verify(manifestJson, signatureBase64.Trim(), await File.ReadAllTextAsync(config.ManifestPublicKeyPath, cancellationToken));
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
