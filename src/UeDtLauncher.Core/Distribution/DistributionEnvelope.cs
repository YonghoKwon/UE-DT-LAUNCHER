namespace UeDtLauncher;

public sealed record DistributionEnvelope(string Payload, string SignatureDocument);

public static class DistributionEnvelopeVerifier
{
    public static string Verify(DistributionEnvelope envelope, LauncherConfig config)
    {
        var bytes = Convert.FromBase64String(envelope.Payload);
        if (bytes.Length > config.Security.MaxCatalogBytes) throw new InvalidDataException("Catalog limit exceeded.");
        var signature = System.Text.Json.JsonSerializer.Deserialize<DetachedSignatureEnvelope>(envelope.SignatureDocument, JsonFiles.Options)
            ?? throw new InvalidDataException("Missing catalog signature.");
        var trusted = config.Security.TrustedSigningKeys.SingleOrDefault(k => k.KeyId == signature.KeyId)
            ?? throw new System.Security.Cryptography.CryptographicException("Untrusted catalog signing key.");
        using var key = System.Security.Cryptography.ECDsa.Create(); key.ImportFromPem(File.ReadAllText(trusted.PublicKeyPath));
        if (!key.VerifyData(bytes, Convert.FromBase64String(signature.Signature), System.Security.Cryptography.HashAlgorithmName.SHA256))
            throw new System.Security.Cryptography.CryptographicException("Invalid catalog signature.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}

public sealed record ReleaseSelection(string ProjectId, string Environment, string Channel, string Platform, string Version)
{
    public string ReleaseId => string.Join("/", ProjectId, Environment, Channel, Version, Platform);
    public void Validate()
    {
        ReleaseSidecar.Segment(ProjectId); ReleaseSidecar.Segment(Version);
        KnownValues.ValidateReleaseTuple(Platform, Environment, Channel);
    }
}
