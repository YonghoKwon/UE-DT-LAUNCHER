using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class PreparedUpdateTests
{
    [Fact]
    public async Task Prepare_DownloadsToStaging_WithoutChangingLiveInstall()
    {
        using var temp = new TempDirectory();
        var original = Encoding.UTF8.GetBytes("old");
        var updated = Encoding.UTF8.GetBytes("new-version");
        var installFile = Path.Combine(temp.InstallDir, "app.bin");
        Directory.CreateDirectory(temp.InstallDir);
        await File.WriteAllBytesAsync(installFile, original);
        await JsonFiles.WriteAsync(temp.Config.InstalledManifestPath, Manifest("0.9.0", original));

        using var http = CreateHttpClient(Manifest("1.0.0", updated), updated);
        using var engine = new LauncherEngine(temp.Config, null, null, echoToConsole: false, httpClient: http);
        using var prepared = await engine.PrepareAsync();

        Assert.True(prepared.HasLiveChanges);
        Assert.Equal(original, await File.ReadAllBytesAsync(installFile));
        Assert.True(File.Exists(Path.Combine(temp.Config.StagingDir, "app.bin")));
        Assert.False(File.Exists(temp.Config.InstallStatePath));
    }

    [Fact]
    public async Task Commit_AppliesPreviouslyPreparedFilesAndState()
    {
        using var temp = new TempDirectory();
        var original = Encoding.UTF8.GetBytes("old");
        var updated = Encoding.UTF8.GetBytes("new-version");
        var installFile = Path.Combine(temp.InstallDir, "app.bin");
        Directory.CreateDirectory(temp.InstallDir);
        await File.WriteAllBytesAsync(installFile, original);
        await JsonFiles.WriteAsync(temp.Config.InstalledManifestPath, Manifest("0.9.0", original));

        using var http = CreateHttpClient(Manifest("1.0.0", updated), updated);
        using var engine = new LauncherEngine(temp.Config, null, null, echoToConsole: false, httpClient: http);
        using var prepared = await engine.PrepareAsync();
        var backupRoot = await engine.CommitPreparedAsync(prepared);

        Assert.NotNull(backupRoot);
        Assert.Equal(updated, await File.ReadAllBytesAsync(installFile));
        var state = await JsonFiles.ReadAsync<InstallState>(temp.Config.InstallStatePath);
        Assert.Equal("1.0.0", state.Version);
    }

    private static HttpClient CreateHttpClient(LauncherManifest manifest, byte[] payload)
    {
        var manifestJson = JsonSerializer.Serialize(manifest, JsonFiles.Options);
        return new HttpClient(new StubHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? new StringContent(manifestJson, Encoding.UTF8, "application/json")
                : new ByteArrayContent(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
    }

    private static LauncherManifest Manifest(string version, byte[] payload) => new()
    {
        AppId = "test-app",
        Version = version,
        Platform = "test-platform",
        EntryPoint = "app.bin",
        BaseUrl = "https://updates.example.com/files/",
        Files =
        {
            new ManifestFile
            {
                Path = "app.bin",
                Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                Size = payload.Length,
                Url = "app.bin"
            }
        }
    };

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
                "uedt-prepared-update-tests",
                Guid.NewGuid().ToString("N"));
            InstallDir = System.IO.Path.Combine(Path, "app");
            var stateDir = System.IO.Path.Combine(Path, "state");
            Config = new LauncherConfig
            {
                ManifestUrl = "https://updates.example.com/manifest.json",
                ProjectId = "test-project",
                TargetPlatform = "test-platform",
                InstallDir = InstallDir,
                StagingDir = System.IO.Path.Combine(stateDir, "staging"),
                BackupDir = System.IO.Path.Combine(stateDir, "backups"),
                InstalledManifestPath = System.IO.Path.Combine(stateDir, "installed-manifest.json"),
                InstallStatePath = System.IO.Path.Combine(stateDir, "install-state.json"),
                AppPidPath = System.IO.Path.Combine(stateDir, "app.pid"),
                LaunchAfterUpdate = false,
                MaxRetryCount = 1
            };
        }

        public string Path { get; }
        public string InstallDir { get; }
        public LauncherConfig Config { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
