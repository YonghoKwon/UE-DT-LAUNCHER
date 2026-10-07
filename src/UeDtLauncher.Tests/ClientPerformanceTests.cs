using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class ClientPerformanceTests
{
    [Fact]
    public void OldConfigKeepsDefaults_AndNewConfigRoundTrips()
    {
        var legacy = JsonSerializer.Deserialize<LauncherConfig>("{}", JsonFiles.Options)!;
        Assert.Equal(2, legacy.Performance.DownloadConcurrency);
        Assert.Equal(2, legacy.Performance.HashConcurrency);
        Assert.True(legacy.Performance.ReusePreviousInstallations);
        legacy.Performance.DownloadConcurrency = 8;
        legacy.Performance.HashConcurrency = 4;
        var roundTrip = JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(legacy, JsonFiles.Options), JsonFiles.Options)!;
        Assert.Equal(8, roundTrip.Performance.DownloadConcurrency);
        Assert.Equal(4, roundTrip.Performance.HashConcurrency);
        // Existing IPC record shape and old readers tolerate the optional config section.
        Assert.Equal(new LauncherProgress("Download", "x"),
            JsonSerializer.Deserialize<LauncherProgress>("{\"stage\":\"Download\",\"message\":\"x\"}", JsonFiles.Options));
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(9, 2)] [InlineData(2, 0)] [InlineData(2, 5)]
    public void InvalidConcurrencyIsRejectedForLegacyConfigs(int download, int hash)
    {
        var config = new LauncherConfig { Performance = new() { DownloadConcurrency = download, HashConcurrency = hash } };
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));
    }

    [Fact]
    public void AggregateProgress_IsGloballyThrottledMonotonicAndRetrySafe()
    {
        long clock = 0;
        var reports = new List<LauncherProgress>();
        var files = Enumerable.Range(0, 2).Select(i => new ManifestFile { Path = $"{i}", Size = 100 }).ToArray();
        var progress = new DownloadProgressAggregator(files, reports.Add, () => clock);
        progress.Report(0, 80);
        progress.Report(1, 20);
        Assert.Empty(reports);
        clock = Stopwatch.Frequency / 4;
        progress.Report(0, 10); // restarted request, not another 10 bytes
        Assert.Single(reports);
        Assert.Equal(100, reports[0].BytesDownloaded);
        progress.Report(1, 100, completed: true);
        Assert.Single(reports); // per-file completion does not bypass global throttle
        progress.Report(0, 100, completed: true);
        Assert.Equal(200, reports[^1].BytesDownloaded);
        Assert.Equal(65, reports[^1].Percent);
        Assert.Equal(2, reports[^1].FileIndex);
    }

    [Fact]
    public async Task BoundedWorkers_CancelAndJoinSiblingsBeforeFailureReturns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, finished = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => BoundedFileWorkers.RunAsync(20, 2, async (index, token) =>
        {
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            try
            {
                await entered.Task.WaitAsync(token);
                if (index == 0) throw new IOException("fatal");
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); }
        }, CancellationToken.None));
        Assert.Equal(0, active);
        Assert.Equal(2, finished);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public async Task ParallelDownload_RespectsLimitAndKeepsPlanOrder(int concurrency)
    {
        using var fixture = new Fixture(9);
        fixture.Config.Performance.DownloadConcurrency = concurrency;
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads);
        using var http = new HttpClient(handler);
        var progress = new List<LauncherProgress>();
        var logger = new FileLogger(Path.Combine(fixture.Root, "logs"));
        using var engine = new LauncherEngine(fixture.Config, progress.Add, logger, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(concurrency, handler.Peak);
        Assert.Equal(fixture.Manifest.Files.Select(f => f.Path), prepared.Plan.DownloadOrRepair.Select(f => f.Path));
        Assert.Equal(fixture.Payloads.Values.Sum(b => b.LongLength), engine.PerformanceMetrics.NetworkBytes);
        var updates = progress.Where(p => p.BytesDownloaded.HasValue).ToArray();
        Assert.NotEmpty(updates);
        Assert.Equal(updates.Select(p => p.BytesDownloaded).Order(), updates.Select(p => p.BytesDownloaded));
        Assert.Equal(fixture.Payloads.Values.Sum(b => b.LongLength), updates[^1].BytesDownloaded);
        Assert.False(File.Exists(fixture.Config.InstallStatePath));
        await engine.CommitPreparedAsync(prepared);
        Assert.DoesNotContain(progress, p => p.Stage == "Performance");
        var summary = Assert.Single(await File.ReadAllLinesAsync(logger.CurrentLogPath), line => line.Contains("[Performance]"));
        var metrics = JsonSerializer.Deserialize<LauncherPerformanceMetrics>(summary[(summary.IndexOf("[Performance]", StringComparison.Ordinal) + 14)..], JsonFiles.Options)!;
        Assert.Equal(engine.PerformanceMetrics.NetworkBytes, metrics.NetworkBytes);
        foreach (var file in fixture.Manifest.Files)
            Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(fixture.Config.InstallDir, file.Path), file.Sha256));
    }

    [Fact]
    public async Task ExistingFiles_HashScanDetectsSameSizeCorruptionDeterministically()
    {
        using var fixture = new Fixture(9);
        fixture.Config.Performance.HashConcurrency = 4;
        Directory.CreateDirectory(fixture.Config.InstallDir);
        foreach (var (path, payload) in fixture.Payloads)
            await File.WriteAllBytesAsync(Path.Combine(fixture.Config.InstallDir, path), payload);
        await JsonFiles.WriteAsync(fixture.Config.InstalledManifestPath, fixture.Manifest);
        var corrupt = fixture.Payloads["file3.bin"].ToArray(); corrupt[0] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(fixture.Config.InstallDir, "file3.bin"), corrupt);
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads);
        using var http = new HttpClient(handler);
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal("file3.bin", Assert.Single(prepared.Plan.DownloadOrRepair).Path);
        Assert.Equal(corrupt.Length, engine.PerformanceMetrics.NetworkBytes);
        Assert.True(engine.PerformanceMetrics.HashMilliseconds > 0);
    }

    [Fact]
    public async Task RetryResume_DoesNotDoubleCountLogicalOrNetworkBytes()
    {
        using var fixture = new Fixture(1);
        fixture.Config.MaxRetryCount = 2;
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads) { InterruptFirst = true };
        using var http = new HttpClient(handler);
        var progress = new List<LauncherProgress>();
        using var engine = new LauncherEngine(fixture.Config, progress.Add, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.True(handler.SawRange);
        Assert.Equal(fixture.Payloads.Values.Single().Length, engine.PerformanceMetrics.NetworkBytes);
        Assert.Equal(fixture.Payloads.Values.Single().Length, progress.Last(p => p.BytesDownloaded.HasValue).BytesDownloaded);
    }

    [Fact]
    public async Task HashMismatch_RetriesAndCountsActualNetworkTraffic()
    {
        using var fixture = new Fixture(1);
        fixture.Config.MaxRetryCount = 2;
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads) { CorruptFirst = true };
        using var http = new HttpClient(handler);
        var progress = new List<LauncherProgress>();
        using var engine = new LauncherEngine(fixture.Config, progress.Add, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(fixture.Payloads.Values.Single().Length * 2, engine.PerformanceMetrics.NetworkBytes);
        Assert.Equal(fixture.Payloads.Values.Single().Length, progress.Last(p => p.BytesDownloaded.HasValue).BytesDownloaded);
    }

    [Fact]
    public async Task PermanentHttpFailure_CancelsAndJoinsOtherDownloads()
    {
        using var fixture = new Fixture(8);
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads) { FailFirst = true, Stall = true };
        using var http = new HttpClient(handler);
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        await Assert.ThrowsAnyAsync<Exception>(() => engine.PrepareAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, handler.Active);
        Assert.False(File.Exists(fixture.Config.InstallStatePath));
    }

    [Fact]
    public async Task ExternalCancellation_JoinsStreamsAndLeavesLiveInstallUntouched()
    {
        using var fixture = new Fixture(8);
        using var handler = new PayloadHandler(fixture.Manifest, fixture.Payloads) { Stall = true };
        using var http = new HttpClient(handler);
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var cancellation = new CancellationTokenSource();
        var preparing = engine.PrepareAsync(cancellation.Token);
        await handler.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(0, handler.Active);
        Assert.Empty(Directory.EnumerateFiles(fixture.Config.InstallDir));
        Assert.False(File.Exists(fixture.Config.InstallStatePath));
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-client-perf", Guid.NewGuid().ToString("N"));
        internal LauncherConfig Config { get; }
        internal LauncherManifest Manifest { get; }
        internal Dictionary<string, byte[]> Payloads { get; } = new();
        internal Fixture(int count)
        {
            Config = new LauncherConfig
            {
                ManifestUrl = "https://updates.example.com/manifest.json", ProjectId = "demo", TargetPlatform = "windows-x64",
                InstallDir = Path.Combine(Root, "install"), StateRootDir = Path.Combine(Root, "state"),
                StagingDir = Path.Combine(Root, "staging"), BackupDir = Path.Combine(Root, "backup"),
                InstalledManifestPath = Path.Combine(Root, "installed-manifest.json"), InstallStatePath = Path.Combine(Root, "install-state.json"),
                AppPidPath = Path.Combine(Root, "app.pid"), LaunchAfterUpdate = false, MaxRetryCount = 1
            };
            Manifest = new LauncherManifest { AppId = "demo", Version = "2.0.0", Platform = "windows-x64", EntryPoint = "file0.bin", BaseUrl = "https://updates.example.com/files/" };
            RuntimeTestSupport.Stopped(Config);
            for (var i = 0; i < count; i++)
            {
                var bytes = Enumerable.Repeat((byte)i, 65536).ToArray();
                var path = $"file{i}.bin"; Payloads.Add(path, bytes);
                Manifest.Files.Add(new ManifestFile { Path = path, Url = path, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            }
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    internal sealed class PayloadHandler(LauncherManifest manifest, Dictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        private int _active, _peak, _requests;
        internal int Active => _active;
        internal int Peak => _peak;
        internal bool InterruptFirst { get; init; }
        internal bool CorruptFirst { get; init; }
        internal bool FailFirst { get; init; }
        internal string? Signature { get; init; }
        internal bool Stall { get; init; }
        internal bool SawRange { get; private set; }
        internal TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Signature ?? "") };
            if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(manifest, JsonFiles.Options)) };
            var payload = payloads[Path.GetFileName(request.RequestUri.AbsolutePath)];
            var requestNumber = Interlocked.Increment(ref _requests);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = _peak; if (active <= observed) break; } while (Interlocked.CompareExchange(ref _peak, active, observed) != observed);
            if (active >= 2) TwoStarted.TrySetResult();
            if (FailFirst && requestNumber == 1)
            {
                await TwoStarted.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref _active);
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
            }
            if (CorruptFirst && requestNumber == 1) { payload = payload.ToArray(); payload[0] ^= 1; }
            var offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            SawRange |= offset > 0;
            var stream = new PayloadStream(payload, offset, InterruptFirst && requestNumber == 1,
                Stall, () => Interlocked.Decrement(ref _active));
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
            if (offset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
            return response;
        }
    }

    private sealed class PayloadStream(byte[] bytes, int offset, bool interrupt, bool stall, Action disposed) : MemoryStream(bytes, offset, bytes.Length - offset, false)
    {
        private int _readCount, _disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(stall ? Timeout.Infinite : 10, cancellationToken);
            if (interrupt && _readCount++ > 0) throw new IOException("Simulated interrupted body");
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) disposed();
            base.Dispose(disposing);
        }
    }
}
