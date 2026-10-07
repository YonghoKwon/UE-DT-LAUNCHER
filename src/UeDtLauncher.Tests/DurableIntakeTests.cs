using System.IO.Compression;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class DurableIntakeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "intake-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EitherArrivalOrderWaitsAndApprovalUsesPrivateSnapshot(bool zipFirst)
    {
        var store = new IntakeStore(new DistributionSettings { Root = root });
        var directory = Path.Combine(root, "incoming", "upload1");
        Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, "Windows.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("game.exe").Open())) writer.Write("original");
        var metadata = new ReleaseSidecar { ProjectId = "demo", Version = "1.0.0", EntryPoint = "game.exe" };
        var json = Path.Combine(directory, "release.json");
        await SidecarPackageValidator.GenerateAsync(zip, json, metadata);
        var lateFile = zipFirst ? json : zip;
        File.Move(lateFile, lateFile + ".uploading");
        Assert.Equal("waiting", (await store.IngestAsync(directory)).State);
        File.Move(lateFile + ".uploading", lateFile);
        var job = await store.IngestAsync(directory);
        Assert.Equal("pending", job.State);
        File.WriteAllText(zip, "replaced by uploader");
        var reopened = new IntakeStore(new DistributionSettings { Root = root });
        Assert.Equal(job, await reopened.IngestAsync(directory));
        Assert.Single(reopened.List());
        Assert.True(await Hashing.Sha256MatchesAsync(Path.Combine(job.Snapshot!, metadata.PackageFile), metadata.PackageSha256));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "releases")));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        reopened.Settings.SigningKeyPath = Path.Combine(root, "key.pem");
        File.WriteAllText(reopened.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var publisher = new ApprovedPublisher(reopened);
        var release = await publisher.ApproveAsync(job.Id);
        Assert.True(File.Exists(Path.Combine(release.Directory, "files", "game.exe")));
        var signedBytes = File.ReadAllText(Path.Combine(release.Directory, "manifest.json"));
        var signature = await JsonFiles.ReadAsync<DetachedSignatureEnvelope>(Path.Combine(release.Directory, "manifest.json.sig"));
        Assert.True(key.VerifyData(System.Text.Encoding.UTF8.GetBytes(signedBytes), Convert.FromBase64String(signature.Signature), System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.Equal(release.ReleaseId, (await publisher.ApproveAsync(job.Id)).ReleaseId);
        Assert.Single(publisher.List());
    }
    [Fact]
    public async Task BadMetadataRequiresExplicitRetryAndWatchRecovers()
    {
        var store = new IntakeStore(new DistributionSettings { Root = root });
        var directory = Path.Combine(root, "incoming", "bad"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "release.json"), "broken");
        await store.ScanAsync(default);
        var failed = Assert.Single(store.List()); Assert.Equal("failed", failed.State);
        store.Retry(failed.Id); Assert.Equal("waiting", store.Get(failed.Id).State);
        await store.ScanAsync(default); Assert.Equal("failed", store.Get(failed.Id).State);
    }
    [Fact]
    public async Task InterruptedApprovalResumesWithoutPublishingPartialFiles()
    {
        var store = new IntakeStore(new DistributionSettings { Root = root, SigningKeyPath = Path.Combine(root, "sign.pem") });
        var upload = Path.Combine(root, "incoming", "crash"); Directory.CreateDirectory(upload);
        var zip = Path.Combine(upload, "Linux.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("game.sh").Open())) writer.Write("#!/bin/sh\nexit 0\n");
        await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(upload, "release.json"), new ReleaseSidecar
        { ProjectId = "demo", Version = "1.0.0", Platform = "linux-x64", EntryPoint = "game.sh" });
        var job = await store.IngestAsync(upload); var publisher = new ApprovedPublisher(store);
        await Assert.ThrowsAsync<FileNotFoundException>(() => publisher.ApproveAsync(job.Id));
        Assert.Empty(publisher.List()); Assert.Equal("publishing", store.Get(job.Id).State);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        File.WriteAllText(store.Settings.SigningKeyPath, key.ExportECPrivateKeyPem());
        var released = await new ApprovedPublisher(new IntakeStore(store.Settings)).ApproveAsync(job.Id);
        Assert.True(File.Exists(Path.Combine(released.Directory, "manifest.json.sig")));
        Assert.Single(publisher.List());
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
