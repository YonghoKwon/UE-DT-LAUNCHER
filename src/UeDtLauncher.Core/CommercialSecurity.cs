using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed class LauncherSecurityConfig
{
    public string AuthenticationMode { get; set; } = "bearer";
    public bool RequireHttps { get; set; } = true;
    public string? CredentialName { get; set; }
    public string? CustomCaCertificatePath { get; set; }
    public List<string> AllowedDownloadHosts { get; set; } = new();
    public List<TrustedSigningKey> TrustedSigningKeys { get; set; } = new();
    public bool EnforceCatalogFreshness { get; set; } = true;
    public int MaxCatalogAgeHours { get; set; } = 168;
    public int MaxCatalogBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxManifestBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxSignatureBytes { get; set; } = 64 * 1024;
    public int MaxManifestFiles { get; set; } = 200_000;
    public long MaxSingleFileBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    public long MaxTotalDownloadBytes { get; set; } = 512L * 1024 * 1024 * 1024;
}

public sealed class TrustedSigningKey
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeyPath { get; set; } = string.Empty;
}

public sealed class DetachedSignatureEnvelope
{
    public int SchemaVersion { get; set; } = 2;
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "ECDSA-P256-SHA256";
    public string Signature { get; set; } = string.Empty;
    public string? PayloadSha256 { get; set; }
    public List<DetachedSignatureEntry> AcceptedSignatures { get; set; } = new();
}

public sealed class DetachedSignatureEntry
{
    public string KeyId { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "ECDSA-P256-SHA256";
    public string Signature { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
}

public sealed class CatalogTrustState
{
    public Dictionary<string, long> HighestSequenceByCatalog { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class LauncherConfigValidator
{
    public static void Validate(LauncherConfig config)
    {
        RuntimeDataPolicy.ValidateConfiguration(config);
        if (config.SchemaVersion is < 1 or > 3)
            throw new InvalidOperationException($"Unsupported launcher config schemaVersion: {config.SchemaVersion}.");
        if (config.DeploymentMode is not ("portable" or "managed-agent"))
            throw new InvalidOperationException("deploymentMode must be portable or managed-agent.");
        if (config.Performance is null || config.Performance.DownloadConcurrency is < 1 or > 8)
            throw new InvalidOperationException("performance.downloadConcurrency must be between 1 and 8.");
        if (config.Performance.HashConcurrency is < 1 or > 4)
            throw new InvalidOperationException("performance.hashConcurrency must be between 1 and 4.");
        if (config.Performance.ResumeCacheBytes is < 0 or > 549755813888L)
            throw new InvalidOperationException("performance.resumeCacheBytes must be null or between 0 and 512 GiB.");
        if (config.Security.AuthenticationMode is not ("bearer" or "request-signature-v1"))
            throw new InvalidOperationException("Unsupported authentication mode.");
        if (config.Security.AuthenticationMode == "request-signature-v1")
        {
            if (config.SchemaVersion != 3 || !config.RequireSignedManifests || !config.Security.EnforceCatalogFreshness ||
                string.IsNullOrWhiteSpace(config.Security.CredentialName) || config.Security.TrustedSigningKeys.Count == 0 ||
                !Uri.TryCreate(config.DistributionServerUrl, UriKind.Absolute, out var origin) ||
                origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new InvalidOperationException("Request signatures require schema 3, a distribution origin, device credential and trusted signed metadata with freshness checks.");
            if (config.Security.TrustedSigningKeys.Any(key => string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyPath)))
                throw new InvalidOperationException("Each trusted publication key requires an ID and public-key path.");
        }
        if (config.SchemaVersion < 2) return;

        var production = string.Equals(config.Environment, "prod", StringComparison.OrdinalIgnoreCase);
        if (production && !config.RequireSignedManifests)
            throw new InvalidOperationException("schemaVersion 2 production config requires requireSignedManifests=true.");
        if (production && string.IsNullOrWhiteSpace(config.Security.CredentialName))
            throw new InvalidOperationException("schemaVersion 2 production config requires security.credentialName.");
        if (config.Security.MaxCatalogBytes is < 1024 or > 64 * 1024 * 1024)
            throw new InvalidOperationException("security.maxCatalogBytes is outside the supported range.");
        if (config.Security.MaxManifestBytes is < 1024 or > 128 * 1024 * 1024)
            throw new InvalidOperationException("security.maxManifestBytes is outside the supported range.");
        if (config.Security.MaxManifestFiles is < 1 or > 1_000_000)
            throw new InvalidOperationException("security.maxManifestFiles is outside the supported range.");

        foreach (var url in RequiredMetadataUrls(config))
        {
            ValidateUrl(config, new Uri(url, UriKind.Absolute), "metadata");
        }

        var duplicateKey = config.Security.TrustedSigningKeys
            .Where(key => !string.IsNullOrWhiteSpace(key.KeyId))
            .GroupBy(key => key.KeyId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null) throw new InvalidOperationException($"Duplicate signing keyId: {duplicateKey.Key}.");
    }

    public static void ValidateUrl(LauncherConfig config, Uri uri, string purpose)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException($"Credentials in {purpose} URLs are not allowed: {uri.GetLeftPart(UriPartial.Path)}");
        if (config.SchemaVersion >= 2 && config.Security.RequireHttps && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"schemaVersion 2 requires HTTPS for {purpose}: {uri.GetLeftPart(UriPartial.Path)}");
        if (config.SchemaVersion >= 2 && config.Security.AllowedDownloadHosts.Count > 0 &&
            !config.Security.AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Host is not allowed for {purpose}: {uri.Host}");
        }
    }

    private static IEnumerable<string> RequiredMetadataUrls(LauncherConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CatalogUrl)) yield return config.CatalogUrl;
        else if (!string.IsNullOrWhiteSpace(config.ManifestUrl)) yield return config.ManifestUrl;
        if (!string.IsNullOrWhiteSpace(config.CatalogSignatureUrl)) yield return config.CatalogSignatureUrl;
        if (string.IsNullOrWhiteSpace(config.CatalogUrl) && !string.IsNullOrWhiteSpace(config.ManifestSignatureUrl))
            yield return config.ManifestSignatureUrl;
    }
}

public static partial class CredentialStore
{
    public static void Save(string name, string token, ManagedLauncherPathLayout? layout = null)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Credential token must not be empty.", nameof(token));
        var path = CredentialPath(name, layout);
        var plain = Encoding.UTF8.GetBytes(token);
        try { ProtectedCredentialFile.Write(path, plain, DeviceCredentials.IsManaged(layout), replace: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string? Read(string? name, ManagedLauncherPathLayout? layout = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        ValidateName(name);
        var path = CredentialPath(name, layout);
        if (!File.Exists(path)) return null;
        var plain = ProtectedCredentialFile.Read(path, DeviceCredentials.IsManaged(layout));
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static bool Exists(string name, ManagedLauncherPathLayout? layout = null) =>
        File.Exists(CredentialPath(ValidateName(name), layout));

    public static void Delete(string name, ManagedLauncherPathLayout? layout = null)
    {
        var path = CredentialPath(ValidateName(name), layout);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string CredentialPath(string name, ManagedLauncherPathLayout? layout) =>
        SafePath.ResolveInside((layout ?? ManagedLauncherPathLayout.Current()).CredentialRoot, name + ".cred");

    private static string ValidateName(string name)
    {
        if (!CredentialNamePattern().IsMatch(name))
            throw new ArgumentException("Credential name must use 1-64 letters, digits, '.', '_' or '-'.", nameof(name));
        return name;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] value) =>
        ProtectedData.Protect(value, optionalEntropy: null, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] value) =>
        ProtectedData.Unprotect(value, optionalEntropy: null, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void SetUnixCredentialPermissions(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialNamePattern();
}

public static class SecureHttpClientFactory
{
    public static HttpClient Create(LauncherConfig config)
    {
        LauncherConfigValidator.Validate(config);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(config.Security.CustomCaCertificatePath))
        {
            var customRoot = new X509Certificate2(config.Security.CustomCaCertificatePath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(customRoot);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate));
            };
        }
        var bearer = config.Security.AuthenticationMode == "bearer" ? CredentialStore.Read(config.Security.CredentialName) : null;
        if (config.Security.AuthenticationMode == "bearer" && !string.IsNullOrWhiteSpace(config.Security.CredentialName) && string.IsNullOrWhiteSpace(bearer))
        { handler.Dispose(); throw new InvalidOperationException("Configured Bearer credential is missing; no unauthenticated fallback is allowed."); }
        HttpMessageHandler authentication = config.Security.AuthenticationMode == "request-signature-v1"
            ? new DeviceSignatureHandler(config, handler)
            : new BearerCredentialHandler(config, bearer, handler);
        var client = new HttpClient(authentication) { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UE-DT-Launcher/1.0");
        return client;
    }

    public static async Task<string> GetBoundedStringAsync(
        HttpClient client,
        string url,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedStringAsync(response, maxBytes, cancellationToken);
    }

    public static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken = default)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"HTTP metadata exceeds the configured limit: {response.Content.Headers.ContentLength} / {maxBytes} bytes.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maxBytes) throw new InvalidDataException($"HTTP metadata exceeds the configured limit of {maxBytes} bytes.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }
}

public static class DetachedSignatureVerifier
{
    public static async Task<bool> VerifyIfConfiguredAsync(
        string payload,
        string? signatureUrl,
        string? legacyPublicKeyPath,
        LauncherConfig config,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signatureUrl)) return false;
        LauncherConfigValidator.ValidateUrl(config, new Uri(signatureUrl, UriKind.Absolute), "signature");
        var signatureDocument = (await SecureHttpClientFactory.GetBoundedStringAsync(
            httpClient,
            signatureUrl,
            config.Security.MaxSignatureBytes,
            cancellationToken)).Trim();

        if (!signatureDocument.StartsWith('{'))
        {
            if (string.IsNullOrWhiteSpace(legacyPublicKeyPath)) return false;
            ManifestSignatureVerifier.Verify(payload, signatureDocument, await File.ReadAllTextAsync(legacyPublicKeyPath, cancellationToken));
            return true;
        }

        var envelope = JsonSerializer.Deserialize<DetachedSignatureEnvelope>(signatureDocument, JsonFiles.Options)
                       ?? throw new InvalidDataException("Detached signature envelope was invalid.");
        if (envelope.SchemaVersion != 2) throw new CryptographicException("Detached signature schema is not supported.");
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var candidates = new List<DetachedSignatureEntry>
        {
            new()
            {
                KeyId = envelope.KeyId,
                Algorithm = envelope.Algorithm,
                Signature = envelope.Signature,
                PayloadSha256 = envelope.PayloadSha256 ?? payloadHash
            }
        };
        candidates.AddRange(envelope.AcceptedSignatures);
        var candidate = candidates.FirstOrDefault(value => value.PayloadSha256.Equals(payloadHash, StringComparison.OrdinalIgnoreCase))
                        ?? throw new CryptographicException("Detached signature set does not contain this payload hash.");
        if (!candidate.Algorithm.Equals("ECDSA-P256-SHA256", StringComparison.Ordinal))
            throw new CryptographicException("Detached signature algorithm is not supported.");
        var trusted = config.Security.TrustedSigningKeys.SingleOrDefault(key => key.KeyId.Equals(candidate.KeyId, StringComparison.Ordinal))
                      ?? throw new CryptographicException($"Detached signature keyId is not trusted: {candidate.KeyId}.");
        ManifestSignatureVerifier.Verify(payload, candidate.Signature, await File.ReadAllTextAsync(trusted.PublicKeyPath, cancellationToken));
        return true;
    }
}

public static class CatalogTrustManager
{
    public static async Task ValidateAndRecordAsync(
        LauncherConfig config,
        DistributionCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        if (config.SchemaVersion < 2 || !config.Security.EnforceCatalogFreshness) return;
        if (catalog.SchemaVersion != 2) throw new InvalidDataException("schemaVersion 2 client requires catalog schemaVersion 2.");
        if (catalog.Sequence <= 0) throw new InvalidDataException("Catalog sequence must be positive.");
        if (!DateTimeOffset.TryParse(catalog.IssuedAtUtc ?? catalog.GeneratedAt, out var issuedAt))
            throw new InvalidDataException("Catalog issuedAtUtc is invalid.");
        if (!DateTimeOffset.TryParse(catalog.ExpiresAtUtc, out var expiresAt))
            throw new InvalidDataException("Catalog expiresAtUtc is required and must be valid.");
        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.AddMinutes(5)) throw new InvalidDataException("Catalog issue time is in the future.");
        if (issuedAt < now.AddHours(-Math.Clamp(config.Security.MaxCatalogAgeHours, 1, 24 * 365)))
            throw new InvalidDataException("Catalog is older than the configured maximum age.");
        if (expiresAt <= now) throw new InvalidDataException("Catalog has expired.");
        if (!string.IsNullOrWhiteSpace(catalog.MinimumLauncherVersion) &&
            CompareVersions(CurrentLauncherVersion(), catalog.MinimumLauncherVersion) < 0)
        {
            throw new InvalidOperationException($"Launcher {catalog.MinimumLauncherVersion} or newer is required.");
        }

        var path = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))
            ?? throw new InvalidOperationException("Could not resolve catalog trust state directory."),
            "catalog-trust.json");
        var state = File.Exists(path)
            ? await JsonFiles.ReadAsync<CatalogTrustState>(path, cancellationToken)
            : new CatalogTrustState();
        var catalogKey = new Uri(config.CatalogUrl!, UriKind.Absolute).GetLeftPart(UriPartial.Path);
        if (state.HighestSequenceByCatalog.TryGetValue(catalogKey, out var highest) && catalog.Sequence < highest)
            throw new InvalidDataException($"Catalog replay detected: sequence {catalog.Sequence} is older than accepted sequence {highest}.");
        if (catalog.Sequence > highest)
        {
            state.HighestSequenceByCatalog[catalogKey] = catalog.Sequence;
            await JsonFiles.WriteAsync(path, state, cancellationToken);
        }
    }

    internal static int CompareVersions(string left, string right)
    {
        static int[] Parts(string value) => value.Trim().TrimStart('v', 'V').Split('-', '+')[0]
            .Split('.').Take(4).Select(part => int.TryParse(part, out var parsed) ? parsed : 0)
            .Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        var leftParts = Parts(left);
        var rightParts = Parts(right);
        for (var index = 0; index < 4; index++)
        {
            var comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string CurrentLauncherVersion() =>
        typeof(CatalogTrustManager).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
