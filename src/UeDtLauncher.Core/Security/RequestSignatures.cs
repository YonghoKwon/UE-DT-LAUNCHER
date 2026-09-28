using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed record RequestSignatureContext(string KeyId, string Nonce, string Method, string TargetUri);
public sealed record CatalogRequestBinding(string KeyId, string Nonce, string Method, string TargetUri, string PayloadSha256, string SignatureDocument);
public sealed record AuthChallenge(string Challenge, int ExpiresInSeconds = 60);

/// <summary>Deliberately restricted RFC 9421 GET/HEAD profile, not a general Structured Fields parser.</summary>
public static class RequestSignatures
{
    public const string ChallengeHeader = "X-UE-DT-Challenge";
    public static readonly HttpRequestOptionsKey<RequestSignatureContext> ContextKey = new("UE-DT.request-proof");
    private const string Algorithm = "ecdsa-p256-sha256";
    private const string Tag = "ue-dt-request-v1";
    private static readonly Regex InputPattern = new("\\Asig1=\\(\"@method\" \"@target-uri\" \"x-ue-dt-challenge\"(?<range> \"range\")?\\);keyid=\"(?<key>[A-Za-z0-9][A-Za-z0-9._-]{0,63})\";nonce=\"(?<nonce>[A-Za-z0-9_-]{43})\";alg=\"ecdsa-p256-sha256\";tag=\"ue-dt-request-v1\"\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);
    public static string Target(Uri uri) => uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    public static string NewNonce() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    public static string Input(RequestSignatureContext context, bool range) =>
        $"sig1=(\"@method\" \"@target-uri\" \"x-ue-dt-challenge\"{(range ? " \"range\"" : "")});keyid=\"{context.KeyId}\";nonce=\"{context.Nonce}\";alg=\"{Algorithm}\";tag=\"{Tag}\"";

    public static byte[] SignatureBase(RequestSignatureContext context, string challenge, string? range)
    {
        if (context.Method is not ("GET" or "HEAD") || challenge.Length is < 1 or > 2048 || challenge.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new InvalidDataException("Unsupported signed request.");
        DeviceCredentials.ValidateIdentifier(context.KeyId);
        if (!Regex.IsMatch(context.Nonce, "\\A[A-Za-z0-9_-]{43}\\z") || FromBase64Url(context.Nonce).Length != 32)
            throw new InvalidDataException("Invalid request nonce.");
        if (range is not null && !Regex.IsMatch(range, "\\Abytes=([0-9]+-[0-9]*|-[0-9]+)\\z")) throw new InvalidDataException("Only one byte Range is supported.");
        if (!Uri.TryCreate(context.TargetUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || context.TargetUri.Any(char.IsControl))
            throw new InvalidDataException("Invalid signed target.");
        var input = Input(context, range is not null)[5..];
        return Encoding.UTF8.GetBytes($"\"@method\": {context.Method}\n\"@target-uri\": {context.TargetUri}\n\"x-ue-dt-challenge\": {challenge}\n" +
            (range is null ? "" : $"\"range\": {range}\n") + $"\"@signature-params\": {input}");
    }

    public static (string Input, string Signature) Sign(RequestSignatureContext context, string challenge, string? range, string privatePem)
    {
        using var key = ECDsa.Create(); key.ImportFromPem(privatePem); DeviceCredentials.ValidateCurve(key);
        var signature = key.SignData(SignatureBase(context, challenge, range), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (Input(context, range is not null), "sig1=:" + Convert.ToBase64String(signature) + ":");
    }

    public static RequestSignatureContext Parse(string input, string method, string target, bool hasRange)
    {
        if (input.Length > 1024) throw new InvalidDataException("Signature input too large.");
        var match = InputPattern.Match(input);
        if (!match.Success || match.Groups["range"].Success != hasRange) throw new InvalidDataException("Unsupported signature profile or unsigned Range.");
        return new(match.Groups["key"].Value, match.Groups["nonce"].Value, method, target);
    }

    public static bool Verify(RequestSignatureContext context, string challenge, string? range, string signatureHeader, string publicPem)
    {
        if (signatureHeader.Length != 95 || !signatureHeader.StartsWith("sig1=:", StringComparison.Ordinal) || !signatureHeader.EndsWith(':')) return false;
        var signature = Convert.FromBase64String(signatureHeader[6..^1]);
        if (signature.Length != 64) return false;
        using var key = ECDsa.Create(); key.ImportFromPem(publicPem); DeviceCredentials.ValidateCurve(key);
        return key.VerifyData(SignatureBase(context, challenge, range), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static byte[] BindingBytes(RequestSignatureContext request, string hash) => JsonSerializer.SerializeToUtf8Bytes(
        new[] { "ue-dt-catalog-request-binding-v1", request.KeyId, request.Nonce, request.Method, request.TargetUri, hash });

    public static void VerifyBinding(DistributionEnvelope envelope, LauncherConfig config, RequestSignatureContext expected)
    {
        var binding = envelope.RequestBinding ?? throw new CryptographicException("Catalog request binding is missing.");
        var hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(envelope.Payload))).ToLowerInvariant();
        if (binding.KeyId != expected.KeyId || binding.Nonce != expected.Nonce || binding.Method != expected.Method || binding.TargetUri != expected.TargetUri || binding.PayloadSha256 != hash)
            throw new CryptographicException("Catalog belongs to a different request.");
        var signature = JsonSerializer.Deserialize<DetachedSignatureEnvelope>(binding.SignatureDocument, JsonFiles.Options)
            ?? throw new CryptographicException("Missing binding signature.");
        if (signature.SchemaVersion != 2 || signature.Algorithm != "ECDSA-P256-SHA256") throw new CryptographicException("Unsupported binding signature.");
        var trusted = config.Security.TrustedSigningKeys.SingleOrDefault(k => k.KeyId == signature.KeyId)
            ?? throw new CryptographicException("Untrusted binding signing key.");
        using var verifier = ECDsa.Create(); verifier.ImportFromPem(File.ReadAllText(trusted.PublicKeyPath));
        if (!verifier.VerifyData(BindingBytes(expected, hash), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256))
            throw new CryptographicException("Catalog request binding signature is invalid.");
    }
}

internal sealed class DeviceSignatureHandler(LauncherConfig config, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly DevicePrivateKey credential = DeviceCredentials.Read(config.Security.CredentialName!);
    private readonly SemaphoreSlim challengeGate = new(1, 1);
    private string? cachedChallenge;
    private long acquired;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri ?? throw new InvalidDataException("Missing request target.");
        LauncherConfigValidator.ValidateUrl(config, uri, "signed request");
        if (RequestSignatures.Origin(uri) != RequestSignatures.Origin(new Uri(config.DistributionServerUrl!)) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Device authentication may only be sent to its configured distribution origin.");
        if (request.Method.Method is not ("GET" or "HEAD") || request.Content is not null || request.Headers.Any(h => h.Key.StartsWith("If-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unsupported signed request.");
        for (var attempt = 0; ; attempt++)
        {
            using var outgoing = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers)
                if (header.Key is not ("Signature" or "Signature-Input" or "Authorization") && !header.Key.Equals(RequestSignatures.ChallengeHeader, StringComparison.OrdinalIgnoreCase))
                    outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var challenge = await ChallengeAsync(token);
            var context = new RequestSignatureContext(credential.KeyId, RequestSignatures.NewNonce(), request.Method.Method, RequestSignatures.Target(uri));
            var signed = RequestSignatures.Sign(context, challenge, outgoing.Headers.Range?.ToString(), credential.PrivateKeyPem);
            outgoing.Headers.TryAddWithoutValidation(RequestSignatures.ChallengeHeader, challenge);
            outgoing.Headers.TryAddWithoutValidation("Signature-Input", signed.Input);
            outgoing.Headers.TryAddWithoutValidation("Signature", signed.Signature);
            outgoing.Options.Set(RequestSignatures.ContextKey, context);
            var response = await base.SendAsync(outgoing, token);
            if (attempt == 0 && response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                response.Headers.TryGetValues("X-UE-DT-Auth-Error", out var values) && values.SingleOrDefault() == "stale-challenge")
            {
                response.Dispose();
                await challengeGate.WaitAsync(token);
                try { if (cachedChallenge == challenge) cachedChallenge = null; }
                finally { challengeGate.Release(); }
                continue;
            }
            return response;
        }
    }

    private async Task<string> ChallengeAsync(CancellationToken token)
    {
        await challengeGate.WaitAsync(token);
        try
        {
            if (cachedChallenge is not null && System.Diagnostics.Stopwatch.GetElapsedTime(acquired) < TimeSpan.FromSeconds(45)) return cachedChallenge;
            using var request = new HttpRequestMessage(HttpMethod.Get, config.DistributionServerUrl!.TrimEnd('/') + "/api/v1/auth/challenge?keyId=" + Uri.EscapeDataString(credential.KeyId));
            using var response = await base.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = await SecureHttpClientFactory.ReadBoundedStringAsync(response, 4096, token);
            var value = JsonSerializer.Deserialize<AuthChallenge>(json, JsonFiles.Options) ?? throw new InvalidDataException("Invalid challenge response.");
            if (value.Challenge.Length is < 1 or > 2048 || value.ExpiresInSeconds != 60) throw new InvalidDataException("Unsupported authentication challenge.");
            cachedChallenge = value.Challenge; acquired = System.Diagnostics.Stopwatch.GetTimestamp(); return cachedChallenge;
        }
        finally { challengeGate.Release(); }
    }
}

internal sealed class BearerCredentialHandler(LauncherConfig config, string? credential, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        LauncherConfigValidator.ValidateUrl(config, request.RequestUri!, "request");
        if (!string.IsNullOrEmpty(credential))
        {
            if (request.RequestUri!.Scheme != "https") throw new InvalidOperationException("Bearer over HTTP is disabled. Migrate to schema 3 request-signature-v1 or use HTTPS.");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        }
        return base.SendAsync(request, token);
    }
}
