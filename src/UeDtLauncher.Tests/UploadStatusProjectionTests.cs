using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadStatusProjectionTests
{
    [Theory]
    [InlineData("pending", true, false, "transferring", "전송 중")]
    [InlineData("waiting", false, true, "waiting", "접수 대기")]
    [InlineData("validating", false, true, "processing", "검사 중")]
    [InlineData("failed", false, true, "failed", "검사 실패")]
    [InlineData("rejected", false, true, "rejected", "거절")]
    [InlineData("pending", false, true, "pending", "승인 대기")]
    [InlineData("publishing", false, true, "publishing", "게시 처리 중")]
    [InlineData("published", false, true, "published", "게시됨")]
    [InlineData("published", false, true, "recommended", "현재 추천판")]
    [InlineData(null, false, true, "waiting", "접수 대기")]
    public void ClassifiesStatus(string? jobState, bool marker, bool pair, string expected, string label)
    {
        var release = jobState == "published" ? new UploadStatusRelease("demo", "1.0", "windows-x64", "prod", "stable") : null;
        var status = UploadStatusProjection.Observe("upload", jobState, marker, pair,
            release is null ? null : (release, expected == "recommended"), DateTimeOffset.UtcNow);
        Assert.NotNull(status);
        Assert.Equal(expected, status.State);
        Assert.Equal(label, status.Label);
        Assert.Equal(release, status.Release);
    }

    [Fact]
    public async Task SupersedingPromotionUpdatesBothUploadsInTheExactTrack()
    {
        using var temp = new UploadStatusTestDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(temp.Path, "signing.pem");
        await File.WriteAllTextAsync(keyPath, key.ExportECPrivateKeyPem());
        var store = new IntakeStore(new() { Root = temp.Path, SigningKeyPath = keyPath });
        try
        {
            var publisher = new ApprovedPublisher(store);
            var promotions = new ReleasePromotions(store);
            var projection = CheckedProjection(store);
            foreach (var version in new[] { "1.0", "2.0" })
            {
                var source = UploadFolder(temp.Path, version);
                var zip = Path.Combine(source, "payload.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                using (var stream = new StreamWriter(archive.CreateEntry("game.exe").Open()))
                    await stream.WriteAsync("fixture");
                await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(source, "release.json"),
                    new() { ProjectId = "demo", Version = version, EntryPoint = "game.exe" });
                var job = await store.IngestAsync(source);
                await publisher.ApproveAsync(job.Id);
            }
            var first = promotions.Promote(new("demo", "prod", "stable", "windows-x64", "1.0"), 0, "fixture");
            await projection.RefreshAsync(default);
            Assert.Equal("recommended", ReadStatus(temp.Path, "1.0").State);
            Assert.Equal("published", ReadStatus(temp.Path, "2.0").State);
            var secondPromotion = promotions.Promote(new("demo", "prod", "stable", "windows-x64", "2.0"), first.Revision, "fixture");
            await projection.RefreshAsync(default);
            Assert.Equal("published", ReadStatus(temp.Path, "1.0").State);
            var second = ReadStatus(temp.Path, "2.0");
            Assert.Equal("recommended", second.State);
            Assert.Equal("2.0", second.Release?.Version);
            promotions.Promote(new("demo", "prod", "stable", "windows-x64", "1.0"), secondPromotion.Revision, "fixture");
            await projection.RefreshAsync(default);
            Assert.Equal("recommended", ReadStatus(temp.Path, "1.0").State);
            Assert.Equal("published", ReadStatus(temp.Path, "2.0").State);
        }
        finally { ClearPool(temp.Path); }
    }

    [Fact]
    public async Task RetryProjectsWaitingWithAnIncompletePairWithoutRegisteringOtherIncompleteUploads()
    {
        using var temp = new UploadStatusTestDirectory();
        var store = new IntakeStore(new() { Root = temp.Path });
        try
        {
            var source = UploadFolder(temp.Path, "retry");
            await File.WriteAllTextAsync(Path.Combine(source, "release.json"), "fixture");
            var unregistered = UploadFolder(temp.Path, "unregistered");
            await File.WriteAllTextAsync(Path.Combine(unregistered, "release.json"), "fixture");
            store.Save(new("fixture-job", source, "failed", null, "fixture failure"));
            var projection = CheckedProjection(store);
            await projection.RefreshAsync(default);
            Assert.Equal("failed", ReadStatus(temp.Path, "retry").State);
            store.Retry("fixture-job");
            await projection.RefreshAsync(default);
            var status = ReadStatus(temp.Path, "retry");
            Assert.Equal("waiting", status.State);
            Assert.Null(status.Reason);
            Assert.False(File.Exists(StatusPath(temp.Path, "unregistered")));
        }
        finally { ClearPool(temp.Path); }
    }

    [Fact]
    public async Task SingleServerProjectsProcessingBeforeItsLongIntakeCompletes()
    {
        using var temp = new UploadStatusTestDirectory();
        using var releaseScan = new ManualResetEventSlim();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new IntakeStore(new()
        {
            Root = temp.Path,
            ListenUrl = "http://127.0.0.1:0",
            PublicUrl = "http://127.0.0.1:18500",
            PolicyPath = Path.Combine(temp.Path, "policy.json"),
            AuthenticationMode = "request-signature-v1"
        }, _ =>
        {
            validating.TrySetResult();
            releaseScan.Wait(stopping.Token);
            return long.MaxValue;
        });
        try
        {
            await File.WriteAllTextAsync(store.Settings.PolicyPath, """{"clients":[]}""");
            var source = UploadFolder(temp.Path, "slow-upload");
            var zip = Path.Combine(source, "payload.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var stream = new StreamWriter(archive.CreateEntry("game.exe").Open()))
                await stream.WriteAsync("fixture");
            await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(source, "release.json"),
                new() { ProjectId = "demo", Version = "1.0", EntryPoint = "game.exe" });

            await using var app = DistributionHttp.CreateApplication(store);
            await app.StartAsync(stopping.Token);
            var scanner = UploadStatusScanner.RunAsync(store, error => Assert.Fail(error.ToString()), stopping.Token);
            try
            {
                await validating.Task.WaitAsync(stopping.Token);
                await WaitForStateAsync(temp.Path, "slow-upload", "processing", stopping.Token);
                await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                using var response = await client.GetAsync("/api/v1/catalog", stopping.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("validating", Assert.Single(store.List()).State);
                Assert.Equal("processing", ReadStatus(temp.Path, "slow-upload").State);
                releaseScan.Set();
                await WaitForStateAsync(temp.Path, "slow-upload", "pending", stopping.Token);
            }
            finally
            {
                releaseScan.Set();
                await stopping.CancelAsync();
                await scanner;
                await app.StopAsync();
            }
        }
        finally { ClearPool(temp.Path); }
    }

    private static async Task WaitForStateAsync(string root, string uploadId, string state, CancellationToken token)
    {
        while (!File.Exists(StatusPath(root, uploadId)) || ReadStatus(root, uploadId).State != state)
            await Task.Delay(50, token);
    }

    [Fact]
    public async Task WritesExactlyOneHundredAtomicUpdatesDuringOneThousandReads()
    {
        using var temp = new UploadStatusTestDirectory();
        var store = new IntakeStore(new() { Root = temp.Path });
        try
        {
            var source = UploadFolder(temp.Path, "upload");
            var marker = Path.Combine(source, "payload.zip.uploading");
            await File.WriteAllTextAsync(marker, "");
            store.Save(new("job", source, "pending", null, null));
            var projection = CheckedProjection(store);
            await projection.RefreshAsync(default);
            using var barrier = new Barrier(2);
            var reader = Task.Run(() =>
            {
                try
                {
                    var reads = 0;
                    for (var update = 0; update < 100; update++)
                    {
                        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                        for (var sample = 0; sample < 10; sample++)
                        {
                            Assert.Contains(ReadStatus(temp.Path, "upload").State, new[] { "transferring", "pending" });
                            reads++;
                            Thread.Yield();
                        }
                    }
                    return reads;
                }
                finally { barrier.RemoveParticipant(); }
            });
            var writer = Task.Run(async () =>
            {
                try
                {
                    var writes = 0;
                    for (var update = 0; update < 100; update++)
                    {
                        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                        if (update % 2 == 0) File.Delete(marker);
                        else await File.WriteAllTextAsync(marker, "");
                        await projection.RefreshAsync(default);
                        Assert.Equal(update % 2 == 0 ? "pending" : "transferring", ReadStatus(temp.Path, "upload").State);
                        writes++;
                    }
                    return writes;
                }
                finally { barrier.RemoveParticipant(); }
            });
            Assert.Equal(new[] { 1000, 100 }, await Task.WhenAll(reader, writer));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(temp.Path, "upload-status"), "*.tmp"));
        }
        finally { ClearPool(temp.Path); }
    }

    [Fact]
    public async Task ThreeRefreshesPreserveAllOtherBytesAndRetainRemovedUploads()
    {
        using var temp = new UploadStatusTestDirectory();
        var store = new IntakeStore(new() { Root = temp.Path });
        try
        {
            var source = UploadFolder(temp.Path, "unregistered");
            await File.WriteAllTextAsync(Path.Combine(source, "payload.zip"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(source, "release.json"), "fixture");
            ClearPool(temp.Path);
            var before = Inventory(temp.Path);
            var projection = CheckedProjection(store);
            await projection.RefreshAsync(default);
            var statusFile = StatusPath(temp.Path, "unregistered");
            var first = await File.ReadAllBytesAsync(statusFile);
            var modified = File.GetLastWriteTimeUtc(statusFile);
            await projection.RefreshAsync(default);
            await projection.RefreshAsync(default);
            ClearPool(temp.Path);
            Assert.Equal(before, Inventory(temp.Path));
            Assert.Equal(first, await File.ReadAllBytesAsync(statusFile));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(statusFile));
            Assert.Equal("waiting", ReadStatus(temp.Path, "unregistered").State);
            Directory.Delete(source, true);
            await projection.RefreshAsync(default);
            Assert.Equal(first, await File.ReadAllBytesAsync(statusFile));
        }
        finally { ClearPool(temp.Path); }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("rejected")]
    public async Task JobDetailsNeverLeaveTheServer(string state)
    {
        using var temp = new UploadStatusTestDirectory();
        var store = new IntakeStore(new() { Root = temp.Path });
        try
        {
            const string jobId = "97b44994a2af7900520bc2ef";
            const string secret = "synthetic-key-403b81a8d";
            var source = UploadFolder(temp.Path, "upload");
            store.Save(new(jobId, source, state, temp.Path, $"Authorization {secret} at {temp.Path}"));
            await CheckedProjection(store).RefreshAsync(default);
            var bytes = await File.ReadAllBytesAsync(StatusPath(temp.Path, "upload"));
            var status = UploadStatus.Parse(bytes, "upload", out _);
            Assert.NotNull(status.Reason);
            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exit = await Program.UploadAsync(["status", "--upload-id", "upload", "--server", "u@host",
                "--remote-status", "/status", "--wait", "--format", "json"], output, errors,
                new UploadStatusCommandTests.FakeRunner(new UploadProcessOutput(0, bytes)));
            Assert.Equal(3, exit);
            var exposed = Encoding.UTF8.GetString(bytes) + output + errors;
            foreach (var prohibited in new[] { jobId, secret, "Authorization", temp.Path, JsonEncodedText.Encode(temp.Path).ToString() })
                Assert.DoesNotContain(prohibited, exposed, StringComparison.OrdinalIgnoreCase);
        }
        finally { ClearPool(temp.Path); }
    }

    private static UploadStatusProjection CheckedProjection(IntakeStore store) =>
        new(store, error => Assert.Fail($"Projection failed to refresh: {error}"));

    private static void ClearPool(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "distribution.db") }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private static string UploadFolder(string root, string uploadId) =>
        Directory.CreateDirectory(Path.Combine(root, "incoming", uploadId)).FullName;

    private static string StatusPath(string root, string uploadId) => Path.Combine(root, "upload-status", uploadId + ".json");

    private static UploadStatus ReadStatus(string root, string uploadId)
    {
        using var stream = new FileStream(StatusPath(root, uploadId), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return UploadStatus.Parse(data.ToArray(), uploadId, out _);
    }

    private static string[] Inventory(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path)))
        .Where(item => !item.Relative.Split(Path.DirectorySeparatorChar)[0].Equals("upload-status", StringComparison.Ordinal))
        .Select(item => File.Exists(item.Path)
            ? item.Relative + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Path)))
            : item.Relative + "/")
        .Order(StringComparer.Ordinal).ToArray();
}

internal sealed class UploadStatusTestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("upload-status-tests-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
