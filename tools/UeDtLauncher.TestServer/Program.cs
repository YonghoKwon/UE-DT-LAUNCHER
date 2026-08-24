using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using UeDtLauncher;

var root = Path.GetFullPath(GetArgument(args, "--root")
                            ?? Path.Combine(Path.GetTempPath(), "uedt-commercial-test-server"));
var port = int.TryParse(GetArgument(args, "--port"), out var parsedPort) ? parsedPort : 18443;
var token = Environment.GetEnvironmentVariable("UE_DT_TEST_SERVER_TOKEN") ?? "commercial-e2e-token";
Directory.CreateDirectory(root);

using var tlsKey = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=localhost", tlsKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
    true));
certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
    true));
var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
certificateRequest.CertificateExtensions.Add(san.Build());
using var generatedCertificate = certificateRequest.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-5),
    DateTimeOffset.UtcNow.AddDays(1));
const string pfxPassword = "uedt-e2e-only";
var certificateBytes = generatedCertificate.Export(X509ContentType.Pfx, pfxPassword);
using var certificate = new X509Certificate2(
    certificateBytes,
    pfxPassword,
    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
var pfxPath = Path.Combine(root, "test-server.pfx");
await File.WriteAllBytesAsync(pfxPath, certificateBytes);
var caPath = Path.Combine(root, "test-ca.cer");
await File.WriteAllBytesAsync(caPath, certificate.Export(X509ContentType.Cert));

using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var signingPublicKeyPath = Path.Combine(root, "release-public.pem");
await File.WriteAllTextAsync(signingPublicKeyPath, signingKey.ExportSubjectPublicKeyInfoPem());
var payload = Encoding.UTF8.GetBytes("UE-DT commercial HTTPS update payload\n");
var fileSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
var baseUrl = $"https://localhost:{port}";
var manifest = new LauncherManifest
{
    AppId = "commercial-e2e",
    Version = "1.0.0",
    Channel = "stable",
    Platform = "windows-x64",
    EntryPoint = "app.bin",
    BaseUrl = baseUrl + "/files",
    Files =
    {
        new ManifestFile { Path = "app.bin", Url = "app.bin", Size = payload.Length, Sha256 = fileSha }
    }
};
var manifestJson = JsonSerializer.Serialize(manifest, JsonFiles.Options);
var manifestSignature = SignatureEnvelope(signingKey, manifestJson);
var now = DateTimeOffset.UtcNow;
var catalog = new DistributionCatalog
{
    SchemaVersion = 2,
    Sequence = 1,
    GeneratedAt = now.ToString("O"),
    IssuedAtUtc = now.ToString("O"),
    ExpiresAtUtc = now.AddHours(2).ToString("O"),
    MinimumLauncherVersion = "1.0.0",
    Projects =
    {
        new DistributionProject
        {
            ProjectId = "commercial-e2e",
            DisplayName = "Commercial E2E",
            Releases =
            {
                new DistributionRelease
                {
                    Version = "1.0.0",
                    Environment = "prod",
                    Channel = "stable",
                    Platform = "windows-x64",
                    ManifestUrl = baseUrl + "/manifest.json",
                    ManifestSignatureUrl = baseUrl + "/manifest.json.sig",
                    AllowedClientProfiles = new List<string> { "general" },
                    IsLatest = true
                }
            }
        }
    }
};
var catalogJson = JsonSerializer.Serialize(catalog, JsonFiles.Options);
var catalogSignature = SignatureEnvelope(signingKey, catalogJson);
var configPath = Path.Combine(root, "launcher.config.json");
await JsonFiles.WriteAsync(configPath, new LauncherConfig
{
    SchemaVersion = 2,
    CatalogUrl = baseUrl + "/catalog.json",
    CatalogSignatureUrl = baseUrl + "/catalog.json.sig",
    ProjectId = "commercial-e2e",
    ClientProfile = "general",
    Environment = "prod",
    Channel = "stable",
    VersionPolicy = "latest",
    TargetPlatform = "windows-x64",
    RequireSignedManifests = true,
    InstallDir = Path.Combine(root, "installed-app"),
    StateRootDir = Path.Combine(root, "state"),
    LogDir = Path.Combine(root, "logs"),
    LaunchAfterUpdate = false,
    Security = new LauncherSecurityConfig
    {
        CredentialName = "commercial-e2e",
        CustomCaCertificatePath = caPath,
        AllowedDownloadHosts = { "localhost" },
        TrustedSigningKeys = { new TrustedSigningKey { KeyId = "e2e-2026", PublicKeyPath = signingPublicKeyPath } }
    }
});

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port, listen =>
    listen.UseHttps(pfxPath, pfxPassword)));
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.Headers.Authorization.ToString().Equals("Bearer " + token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.MapGet("/catalog.json", () => Results.Text(catalogJson, "application/json"));
app.MapGet("/catalog.json.sig", () => Results.Text(catalogSignature, "application/json"));
app.MapGet("/manifest.json", () => Results.Text(manifestJson, "application/json"));
app.MapGet("/manifest.json.sig", () => Results.Text(manifestSignature, "application/json"));
app.MapGet("/files/app.bin", () => Results.Bytes(payload, "application/octet-stream"));
app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "ready",
    url = baseUrl,
    configPath,
    credentialName = "commercial-e2e"
})));
await app.RunAsync();

static string SignatureEnvelope(ECDsa key, string text) => JsonSerializer.Serialize(new DetachedSignatureEnvelope
{
    KeyId = "e2e-2026",
    Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256))
}, JsonFiles.Options);

static string? GetArgument(string[] values, string name)
{
    for (var index = 0; index + 1 < values.Length; index++)
        if (values[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return values[index + 1];
    return null;
}
