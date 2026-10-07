using Xunit;

namespace UeDtLauncher.Tests;

public class ManagedStatusPerformanceTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public async Task StatusHashing_RespectsConfiguredBoundAndDeterministicCounts(int concurrency)
    {
        using var fixture = new StatusFixture();
        fixture.Config.Performance.HashConcurrency = concurrency;
        await fixture.CreateAsync(10, 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        async Task<bool> Hash(string path, string _, CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = peak; if (current <= observed) break; }
            while (Interlocked.CompareExchange(ref peak, current, observed) != observed);
            if (current == concurrency) release.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                await Task.Yield();
                return int.Parse(Path.GetFileNameWithoutExtension(path)) % 3 != 0;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var status = await ManagedProjectStatusInspector.InspectCoreAsync(fixture.Config, fixture.Manifest, Hash)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(concurrency, peak);
        Assert.Equal(0, active);
        Assert.Equal(2, status.MissingFiles);
        Assert.Equal(4, status.ChangedFiles);
        Assert.True(status.UpdateRequired);
    }

    [Fact]
    public async Task StatusCancellation_JoinsAllHashWorkersBeforeReturning()
    {
        using var fixture = new StatusFixture();
        await fixture.CreateAsync(10, 0);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var finished = 0;
        async Task<bool> Hash(string _, string __, CancellationToken token)
        {
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return true; }
            finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref finished); }
        }
        var inspection = ManagedProjectStatusInspector.InspectCoreAsync(fixture.Config, fixture.Manifest, Hash, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
        Assert.Equal(0, active);
        Assert.Equal(2, finished);
    }

    [Fact]
    public async Task StatusSizeMismatchAvoidsHashAndUnsafePathIsRejected()
    {
        using var fixture = new StatusFixture();
        await fixture.CreateAsync(1, 0);
        fixture.Manifest.Files[0].Size = 2;
        Task<bool> UnexpectedHash(string _, string __, CancellationToken token) =>
            throw new InvalidOperationException("Hash should not run for a size mismatch or unsafe path.");
        var status = await ManagedProjectStatusInspector.InspectCoreAsync(fixture.Config, fixture.Manifest, UnexpectedHash);
        Assert.Equal(1, status.ChangedFiles);
        fixture.Manifest.Files[0].Path = "../outside.bin";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagedProjectStatusInspector.InspectCoreAsync(fixture.Config, fixture.Manifest, UnexpectedHash));
    }

    private sealed class StatusFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uedt-status-performance", Guid.NewGuid().ToString("N"));
        internal LauncherConfig Config { get; }
        internal LauncherManifest Manifest { get; } = new() { Version = "1.0.0" };

        internal StatusFixture()
        {
            Config = new LauncherConfig
            {
                InstallDir = Path.Combine(_root, "app"), BackupDir = Path.Combine(_root, "backup"),
                InstalledManifestPath = Path.Combine(_root, "state", "installed-manifest.json")
            };
        }

        internal async Task CreateAsync(int present, int missing)
        {
            Directory.CreateDirectory(Config.InstallDir);
            for (var index = 0; index < present + missing; index++)
            {
                Manifest.Files.Add(new ManifestFile { Path = $"{index}.bin", Size = 1, Sha256 = new string('a', 64) });
                if (index < present) await File.WriteAllBytesAsync(Path.Combine(Config.InstallDir, $"{index}.bin"), new byte[] { 1 });
            }
            await JsonFiles.WriteAsync(Config.InstalledManifestPath, Manifest);
        }

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
