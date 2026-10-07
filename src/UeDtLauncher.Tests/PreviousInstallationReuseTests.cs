using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Fixture = UeDtLauncher.Tests.ClientPerformanceTests.Fixture;
using PayloadHandler = UeDtLauncher.Tests.ClientPerformanceTests.PayloadHandler;

namespace UeDtLauncher.Tests;

public class PreviousInstallationReuseTests
{
    [Fact]
    public async Task NewVersion_ReusesIndependentVerifiedCopiesAndSavesAtLeast90Percent()
    {
        using var fixture = new Fixture(20);
        var signature = await ConfigureVersionedAsync(fixture);
        var oldManifest = Clone(fixture.Manifest);
        var oldPayloads = fixture.Payloads.ToDictionary(p => p.Key, p => p.Value.ToArray());
        oldManifest.Version = "1.0.0";
        oldPayloads["file19.bin"][0] ^= 1;
        oldManifest.Files[19].Sha256 = Hash(oldPayloads["file19.bin"]);
        var source = await CreateInstallationAsync(fixture, oldManifest, oldPayloads);
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        var total = fixture.Payloads.Values.Sum(b => b.LongLength);
        Assert.Equal(19 * 65536, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
        Assert.True(engine.PerformanceMetrics.NetworkBytes <= total / 10);
        await engine.CommitPreparedAsync(prepared);
        // Writing the new installation must not modify the source (hard links would fail this).
        await File.WriteAllBytesAsync(Path.Combine(fixture.Config.InstallDir, "file0.bin"), new byte[] { 7 });
        Assert.Equal(oldPayloads["file0.bin"], await File.ReadAllBytesAsync(Path.Combine(source, "file0.bin")));
    }

    [Theory]
    [InlineData("corrupt")] [InlineData("missing")] [InlineData("locked")]
    public async Task UnavailableSource_FallsBackToHttpAndNeverChangesSource(string kind)
    {
        using var fixture = new Fixture(2);
        var signature = await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        var source = await CreateInstallationAsync(fixture, old, fixture.Payloads);
        var file = Path.Combine(source, "file0.bin");
        if (kind == "missing") File.Delete(file);
        else if (kind == "corrupt")
        {
            var content = fixture.Payloads["file0.bin"].ToArray(); content[0] ^= 1;
            await File.WriteAllBytesAsync(file, content);
        }
        using var locked = kind == "locked" ? new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(65536, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
        Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(fixture.Config.StagingDir, "file0.bin"), fixture.Manifest.Files[0].Sha256));
        if (kind == "missing") Assert.False(File.Exists(file));
        if (kind == "corrupt") Assert.False(await Hashing.Sha256MatchesAsync(file, fixture.Manifest.Files[0].Sha256));
    }

    [Theory]
    [InlineData("install-state.json", "malformed")]
    [InlineData("install-state.json", "null")]
    [InlineData("install-state.json", "oversized")]
    [InlineData("installed-manifest.json", "malformed")]
    [InlineData("installed-manifest.json", "null")]
    [InlineData("installed-manifest.json", "oversized")]
    public async Task InvalidPreviousMetadata_FallsBackToHttpForAuthenticatedTarget(string metadataFile, string kind)
    {
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        fixture.Config.Security.MaxManifestBytes = 1024;
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        var source = await CreateInstallationAsync(fixture, old, fixture.Payloads);
        var selection = fixture.Config.SelectedRelease! with { Version = old.Version };
        var metadataPath = SafePath.ResolveInside(fixture.Config.StateRootDir, selection.ReleaseId + "/" + metadataFile);
        var maximumBytes = metadataFile == "install-state.json" ? 64 * 1024 : fixture.Config.Security.MaxManifestBytes;
        var invalidMetadata = kind switch
        {
            "malformed" => "{",
            "null" => "null",
            _ => new string(' ', maximumBytes + 1)
        };
        await File.WriteAllTextAsync(metadataPath, invalidMetadata);
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);

        await engine.RunAsync();

        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
        Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(fixture.Config.InstallDir, "file0.bin"), fixture.Manifest.Files[0].Sha256));
        Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(source, "file0.bin"), old.Files[0].Sha256));
        Assert.Equal(invalidMetadata, await File.ReadAllTextAsync(metadataPath));
    }

    [Theory]
    [InlineData("project")] [InlineData("environment")] [InlineData("channel")] [InlineData("platform")]
    public async Task DoesNotReuseAcrossTrackBoundaries(string field)
    {
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        var selected = fixture.Config.SelectedRelease! with { Version = "1.0.0" };
        selected = field switch
        {
            "project" => selected with { ProjectId = "other" },
            "environment" => selected with { Environment = "dev" },
            "channel" => selected with { Channel = "beta" },
            _ => selected with { Platform = "linux-x64" }
        };
        var old = Clone(fixture.Manifest); old.Version = selected.Version; old.AppId = selected.ProjectId;
        old.Channel = selected.Channel; old.Platform = selected.Platform;
        await CreateInstallationAsync(fixture, old, fixture.Payloads, selected);
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
    }

    [Theory]
    [InlineData("repair")] [InlineData("existing")] [InlineData("disabled")]
    [InlineData("unsigned-catalog")] [InlineData("unsigned-manifest")] [InlineData("legacy")]
    public async Task ReuseRequiresNewVersionAndAuthenticatedMetadata(string reason)
    {
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        await CreateInstallationAsync(fixture, old, fixture.Payloads);
        switch (reason)
        {
            case "repair": fixture.Config.RepairMode = true; break;
            case "existing": Directory.CreateDirectory(fixture.Config.InstallDir); break;
            case "disabled": fixture.Config.Performance.ReusePreviousInstallations = false; break;
            case "unsigned-catalog": fixture.Config.CatalogAuthenticated = false; break;
            case "unsigned-manifest": fixture.Config.ManifestSignatureUrl = null; fixture.Config.RequireSignedManifests = false; break;
            case "legacy": fixture.Config.SelectedRelease = null; break;
        }
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
    }

    [Fact]
    public async Task InvalidTargetSignatureFailsBeforeAnyReuseOrFileDownload()
    {
        using var fixture = new Fixture(1);
        await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        await CreateInstallationAsync(fixture, old, fixture.Payloads);
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = Convert.ToBase64String(new byte[64]) });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => engine.PrepareAsync());
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(0, engine.PerformanceMetrics.NetworkBytes);
    }

    [Fact]
    public async Task NewCatalogResolutionClearsPriorAuthenticationAndSelectionOnFailure()
    {
        using var fixture = new Fixture(1);
        await ConfigureVersionedAsync(fixture);
        fixture.Config.CatalogUrl = "https://updates.example.com/catalog.json";
        using var http = new HttpClient(new RejectMetadataHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogResolver.ResolveAsync(fixture.Config, http));
        Assert.False(fixture.Config.CatalogAuthenticated);
        Assert.Null(fixture.Config.SelectedRelease);
        fixture.Config.CatalogAuthenticated = true;
        fixture.Config.CatalogUrl = null;
        await CatalogResolver.ResolveAsync(fixture.Config, http);
        Assert.False(fixture.Config.CatalogAuthenticated);
    }

    [Fact]
    public async Task OnlyThreeLatestSuccessfulRecordsAreCandidates()
    {
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        // Only the oldest (fourth) record matches. It must not be searched as a candidate.
        for (var version = 1; version <= 4; version++)
        {
            var old = Clone(fixture.Manifest); old.Version = $"1.0.{version}";
            var payloads = fixture.Payloads.ToDictionary(p => p.Key, p => p.Value.ToArray());
            if (version != 1) { payloads["file0.bin"][0] = (byte)version; old.Files[0].Sha256 = Hash(payloads["file0.bin"]); }
            await CreateInstallationAsync(fixture, old, payloads, installedAt: DateTimeOffset.UtcNow.AddDays(-5 + version));
        }
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
    }

    [Fact]
    public async Task PendingTransactionRecordCannotBeReused()
    {
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        await CreateInstallationAsync(fixture, old, fixture.Payloads);
        var selection = fixture.Config.SelectedRelease! with { Version = old.Version };
        await JsonFiles.WriteAsync(SafePath.ResolveInside(fixture.Config.StateRootDir, selection.ReleaseId + "/transaction.json"),
            new UpdateTransactionJournal { Status = UpdateTransactionStatus.Applying });
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
    }

    [Fact]
    public async Task CancellationDuringCopiedTargetVerificationLeavesSourceIntact()
    {
        using var fixture = new Fixture(1);
        await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        var source = await CreateInstallationAsync(fixture, old, fixture.Payloads);
        var reuse = await PreviousInstallationReuse.DiscoverAsync(fixture.Config,
            new ManifestDocument(fixture.Manifest, "", true), false, CancellationToken.None);
        Assert.NotNull(reuse);
        Directory.CreateDirectory(fixture.Config.StagingDir);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counters = new ClientPerformanceCounters();
        var task = reuse.TryCopyAsync(fixture.Manifest.Files[0], Path.Combine(fixture.Config.StagingDir, "file0.bin"),
            async (_, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; }, counters, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, counters.Snapshot().ReusedBytes);
        Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(source, "file0.bin"), old.Files[0].Sha256));
    }

    [Fact]
    public async Task SourceSymlinkIsRejectedAndFallsBackToHttp()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation requires elevation.
        using var fixture = new Fixture(1);
        var signature = await ConfigureVersionedAsync(fixture);
        var old = Clone(fixture.Manifest); old.Version = "1.0.0";
        var source = await CreateInstallationAsync(fixture, old, fixture.Payloads);
        var outside = Path.Combine(fixture.Root, "outside.bin");
        await File.WriteAllBytesAsync(outside, fixture.Payloads["file0.bin"]);
        var sourceFile = Path.Combine(source, "file0.bin");
        File.Delete(sourceFile);
        File.CreateSymbolicLink(sourceFile, outside);
        using var http = new HttpClient(new PayloadHandler(fixture.Manifest, fixture.Payloads) { Signature = signature });
        using var engine = new LauncherEngine(fixture.Config, null, null, false, http);
        using var prepared = await engine.PrepareAsync();
        Assert.Equal(0, engine.PerformanceMetrics.ReusedBytes);
        Assert.Equal(65536, engine.PerformanceMetrics.NetworkBytes);
        Assert.Equal(fixture.Payloads["file0.bin"], await File.ReadAllBytesAsync(outside));
    }

    private static async Task<string> ConfigureVersionedAsync(Fixture fixture)
    {
        Directory.CreateDirectory(fixture.Root);
        fixture.Config.VersionedInstallRoot = Path.Combine(fixture.Root, "apps");
        fixture.Config.DistributionServerUrl = "https://updates.example.com";
        fixture.Config.CatalogAuthenticated = true;
        fixture.Config.RequireSignedManifests = true;
        fixture.Config.ManifestSignatureUrl = "https://updates.example.com/manifest.json.sig";
        fixture.Config.ManifestPublicKeyPath = Path.Combine(fixture.Root, "public.pem");
        VersionedReleasePaths.Bind(fixture.Config, new ReleaseSelection("demo", "prod", "stable", "windows-x64", fixture.Manifest.Version));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(fixture.Config.ManifestPublicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fixture.Manifest, JsonFiles.Options)), HashAlgorithmName.SHA256));
    }

    private static async Task<string> CreateInstallationAsync(Fixture fixture, LauncherManifest manifest,
        Dictionary<string, byte[]> payloads, ReleaseSelection? selection = null, DateTimeOffset? installedAt = null)
    {
        selection ??= fixture.Config.SelectedRelease! with { Version = manifest.Version };
        var install = SafePath.ResolveInside(fixture.Config.VersionedInstallRoot!, selection.ReleaseId);
        Directory.CreateDirectory(install);
        foreach (var (path, bytes) in payloads) await File.WriteAllBytesAsync(Path.Combine(install, path), bytes);
        await JsonFiles.WriteAsync(SafePath.ResolveInside(fixture.Config.StateRootDir, selection.ReleaseId + "/installed-manifest.json"), manifest);
        await JsonFiles.WriteAsync(SafePath.ResolveInside(fixture.Config.StateRootDir, selection.ReleaseId + "/install-state.json"), new InstallState
        {
            Version = selection.Version, Environment = selection.Environment, Channel = selection.Channel, Platform = selection.Platform,
            ManifestSha256 = new string('a', 64), InstalledAtUtc = (installedAt ?? DateTimeOffset.UtcNow).ToString("O")
        });
        return install;
    }

    private static LauncherManifest Clone(LauncherManifest manifest) => JsonSerializer.Deserialize<LauncherManifest>(JsonSerializer.Serialize(manifest, JsonFiles.Options), JsonFiles.Options)!;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class RejectMetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
    }
}
