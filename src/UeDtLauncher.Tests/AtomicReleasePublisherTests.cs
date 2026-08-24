using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace UeDtLauncher.Tests;

public class AtomicReleasePublisherTests
{
    [Fact]
    public async Task SignedPublish_ActivatesReleaseThenAdvancesCatalog()
    {
        using var temp = new TempDirectory();
        var options = await OptionsAsync(temp, "1.0.0");

        var report = await AtomicReleasePublisher.PublishAsync(options);

        Assert.True(report.Succeeded);
        Assert.True(report.Signed);
        Assert.Equal(1, report.CatalogSequence);
        Assert.True(File.Exists(report.ManifestPath));
        Assert.True(File.Exists(report.ManifestPath + ".sig"));
        Assert.True(File.Exists(report.CatalogPath));
        Assert.True(File.Exists(report.CatalogPath + ".sig"));
        Assert.True(File.Exists(Path.Combine(report.ReleaseDirectory, "publish-report.json")));
        var catalog = await JsonFiles.ReadAsync<DistributionCatalog>(report.CatalogPath);
        Assert.Equal(2, catalog.SchemaVersion);
        Assert.Equal(1, catalog.Sequence);
        Assert.Equal("1.0.0", Assert.Single(Assert.Single(catalog.Projects).Releases).Version);
    }

    [Fact]
    public async Task ExistingRelease_IsRejectedUnlessReplaceIsExplicit()
    {
        using var temp = new TempDirectory();
        var options = await OptionsAsync(temp, "1.0.0");
        _ = await AtomicReleasePublisher.PublishAsync(options);

        await Assert.ThrowsAsync<IOException>(() => AtomicReleasePublisher.PublishAsync(options));
    }

    [Fact]
    public async Task DryRun_ValidatesWithoutChangingServerState()
    {
        using var temp = new TempDirectory();
        var options = await OptionsAsync(temp, "1.0.0");
        options.DryRun = true;

        var report = await AtomicReleasePublisher.PublishAsync(options);

        Assert.True(report.DryRun);
        Assert.False(Directory.Exists(report.ReleaseDirectory));
        Assert.False(File.Exists(report.CatalogPath));
    }

    [Fact]
    public async Task CatalogSignatureTransition_AcceptsPreviousAndCurrentCatalogBytes()
    {
        using var temp = new TempDirectory();
        var firstOptions = await OptionsAsync(temp, "1.0.0");
        var first = await AtomicReleasePublisher.PublishAsync(firstOptions);
        var previousCatalog = await File.ReadAllTextAsync(first.CatalogPath);

        var secondOptions = await OptionsAsync(temp, "1.1.0", reuseKeyPath: firstOptions.PrivateKeyPath);
        var second = await AtomicReleasePublisher.PublishAsync(secondOptions);
        var currentCatalog = await File.ReadAllTextAsync(second.CatalogPath);
        var signatureDocument = await File.ReadAllTextAsync(second.CatalogPath + ".sig");
        var publicKeyPath = Path.Combine(temp.Path, "public.pem");
        using (var signingKey = ECDsa.Create())
        {
            signingKey.ImportFromPem(await File.ReadAllTextAsync(firstOptions.PrivateKeyPath!));
            await File.WriteAllTextAsync(publicKeyPath, signingKey.ExportSubjectPublicKeyInfoPem());
        }
        var config = new LauncherConfig
        {
            Security = new LauncherSecurityConfig
            {
                AllowedDownloadHosts = { "updates.example.com" },
                TrustedSigningKeys = { new TrustedSigningKey { KeyId = "test-key", PublicKeyPath = publicKeyPath } }
            }
        };
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(signatureDocument, Encoding.UTF8)
        }));

        Assert.True(await DetachedSignatureVerifier.VerifyIfConfiguredAsync(previousCatalog, "https://updates.example.com/catalog.sig", null, config, http));
        Assert.True(await DetachedSignatureVerifier.VerifyIfConfiguredAsync(currentCatalog, "https://updates.example.com/catalog.sig", null, config, http));
    }

    private static async Task<AtomicReleasePublishOptions> OptionsAsync(TempDirectory temp, string version, string? reuseKeyPath = null)
    {
        var package = Path.Combine(temp.Path, "packages", version);
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "app.exe"), "payload-" + version);
        var privateKeyPath = reuseKeyPath ?? Path.Combine(temp.Path, "private.pem");
        if (!File.Exists(privateKeyPath))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(privateKeyPath, key.ExportECPrivateKeyPem());
        }
        return new AtomicReleasePublishOptions
        {
            PackageDir = package,
            ServerRoot = Path.Combine(temp.Path, "server"),
            BaseUrlRoot = "https://updates.example.com",
            ProjectId = "project-a",
            DisplayName = "Project A",
            Version = version,
            Environment = "prod",
            Channel = "stable",
            Platform = "windows-x64",
            EntryPoint = "app.exe",
            CatalogProfile = "general",
            AllowedClientProfiles = { "general" },
            SetLatest = true,
            PrivateKeyPath = privateKeyPath,
            SigningKeyId = "test-key"
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-atomic-publish-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
