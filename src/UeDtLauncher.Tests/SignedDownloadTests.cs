using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class SignedDownloadTests
{
    [Fact]
    public async Task ManifestDownload_RequiredWithoutSignatureConfig_IsRejected()
    {
        var config = new LauncherConfig
        {
            ManifestUrl = "https://updates.example.com/manifest.json",
            RequireSignedManifests = true
        };
        using var http = HttpReturning(ValidManifestJson());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManifestDownloader.DownloadAsync(config, http));
    }

    [Fact]
    public async Task CatalogDownload_RequiredWithoutSignatureConfig_IsRejected()
    {
        var config = new LauncherConfig
        {
            CatalogUrl = "https://updates.example.com/catalog.json",
            RequireSignedManifests = true
        };
        using var http = HttpReturning(JsonSerializer.Serialize(new DistributionCatalog(), JsonFiles.Options));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CatalogResolver.DownloadCatalogAsync(config, http));
    }

    [Fact]
    public async Task CatalogDownload_WithValidSignature_IsAccepted()
    {
        using var temp = new TempDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPath = Path.Combine(temp.Path, "public.pem");
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        var json = JsonSerializer.Serialize(new DistributionCatalog(), JsonFiles.Options);
        var signature = Convert.ToBase64String(
            key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256));
        var config = new LauncherConfig
        {
            CatalogUrl = "https://updates.example.com/catalog.json",
            CatalogSignatureUrl = "https://updates.example.com/catalog.json.sig",
            CatalogPublicKeyPath = publicKeyPath,
            RequireSignedManifests = true
        };
        using var http = HttpReturning(json, signature);

        var catalog = await CatalogResolver.DownloadCatalogAsync(config, http);

        Assert.NotNull(catalog);
    }

    private static HttpClient HttpReturning(string json, string? signature = null) =>
        new(new StubHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? signature ?? string.Empty
                : json;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8)
            };
        }));

    private static string ValidManifestJson() =>
        JsonSerializer.Serialize(
            new LauncherManifest
            {
                EntryPoint = "app.exe",
                Files =
                {
                    new ManifestFile
                    {
                        Path = "app.exe",
                        Sha256 = new string('a', 64),
                        Size = 1
                    }
                }
            },
            JsonFiles.Options);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "uedt-signed-download-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
