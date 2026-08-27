using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class CommercialSecurityTests
{
    [Fact]
    public void SchemaV2Production_RequiresHttpsSignedMetadataAndCredentialReference()
    {
        var config = ProductionConfig();
        config.RequireSignedManifests = false;
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));

        config.RequireSignedManifests = true;
        config.Security.CredentialName = null;
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));

        config.Security.CredentialName = "prod-token";
        config.CatalogUrl = "http://updates.example.com/catalog.json";
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));

        config.CatalogUrl = "https://user:password@updates.example.com/catalog.json";
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));

        config.CatalogUrl = "https://updates.example.com/catalog.json";
        LauncherConfigValidator.Validate(config);
    }

    [Fact]
    public void CredentialStore_RoundTripsWithoutUsingTheConfigFile()
    {
        using var temp = new TempDirectory();
        var layout = Layout(temp.Path);
        const string token = "test-token-that-must-not-be-logged";

        CredentialStore.Save("prod-token", token, layout);

        Assert.True(CredentialStore.Exists("prod-token", layout));
        Assert.Equal(token, CredentialStore.Read("prod-token", layout));
        var storedText = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(layout.CredentialRoot, "prod-token.cred")));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain(token, storedText, StringComparison.Ordinal);
        CredentialStore.Delete("prod-token", layout);
        Assert.False(CredentialStore.Exists("prod-token", layout));
    }

    [Fact]
    public async Task DetachedSignatureV2_SelectsTrustedKeyByKeyId()
    {
        using var temp = new TempDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(temp.Path, "release-key.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        const string payload = "{\"schemaVersion\":2}";
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
        var envelope = JsonSerializer.Serialize(new DetachedSignatureEnvelope
        {
            KeyId = "release-2026",
            Signature = signature
        }, JsonFiles.Options);
        var config = ProductionConfig();
        config.Security.TrustedSigningKeys.Add(new TrustedSigningKey { KeyId = "release-2026", PublicKeyPath = publicKeyPath });
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8)
        }));

        Assert.True(await DetachedSignatureVerifier.VerifyIfConfiguredAsync(
            payload,
            "https://updates.example.com/catalog.sig",
            null,
            config,
            http));
    }

    [Fact]
    public async Task CatalogTrust_RejectsReplayAndExpiredMetadata()
    {
        using var temp = new TempDirectory();
        var config = ProductionConfig();
        config.InstallStatePath = Path.Combine(temp.Path, "state", "install-state.json");
        var current = Catalog(sequence: 10, expires: DateTimeOffset.UtcNow.AddHours(1));
        await CatalogTrustManager.ValidateAndRecordAsync(config, current);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CatalogTrustManager.ValidateAndRecordAsync(config, Catalog(9, DateTimeOffset.UtcNow.AddHours(1))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CatalogTrustManager.ValidateAndRecordAsync(config, Catalog(11, DateTimeOffset.UtcNow.AddMinutes(-1))));
    }

    [Fact]
    public void ManifestV2_MustMatchSelectedCatalogReleaseAndSizeLimits()
    {
        var config = ProductionConfig();
        config.ProjectId = "project-a";
        config.TargetPlatform = "windows-x64";
        config.Channel = "stable";
        config.ResolvedReleaseVersion = "2.0.0";
        var manifest = ValidManifest();

        LauncherEngine.ValidateManifest(manifest, config);
        manifest.Version = "1.0.0";
        Assert.Throws<InvalidDataException>(() => LauncherEngine.ValidateManifest(manifest, config));
        manifest.Version = "2.0.0";
        manifest.Files[0].Size = config.Security.MaxSingleFileBytes + 1;
        Assert.Throws<InvalidDataException>(() => LauncherEngine.ValidateManifest(manifest, config));
    }

    [Fact]
    public async Task BoundedMetadataRead_RejectsBodiesOverTheLimit()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2048), Encoding.UTF8)
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SecureHttpClientFactory.GetBoundedStringAsync(http, "https://updates.example.com/catalog.json", 1024));
    }

    private static LauncherConfig ProductionConfig() => new()
    {
        SchemaVersion = 2,
        Environment = "prod",
        RequireSignedManifests = true,
        CatalogUrl = "https://updates.example.com/catalog.json",
        CatalogSignatureUrl = "https://updates.example.com/catalog.sig",
        Security = new LauncherSecurityConfig
        {
            CredentialName = "prod-token",
            AllowedDownloadHosts = { "updates.example.com" }
        }
    };

    private static DistributionCatalog Catalog(long sequence, DateTimeOffset expires) => new()
    {
        SchemaVersion = 2,
        Sequence = sequence,
        IssuedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        ExpiresAtUtc = expires.ToString("O")
    };

    private static LauncherManifest ValidManifest() => new()
    {
        AppId = "project-a",
        Version = "2.0.0",
        Channel = "stable",
        Platform = "windows-x64",
        EntryPoint = "app.exe",
        Files =
        {
            new ManifestFile { Path = "app.exe", Size = 1, Sha256 = new string('a', 64) }
        }
    };

    private static ManagedLauncherPathLayout Layout(string root) => new(
        root,
        Path.Combine(root, "config"),
        Path.Combine(root, "state"),
        Path.Combine(root, "apps"),
        Path.Combine(root, "logs"),
        Path.Combine(root, "credentials"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-commercial-security-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
