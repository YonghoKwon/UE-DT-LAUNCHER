using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class SidecarIntakeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sidecar-tests-" + Guid.NewGuid().ToString("N"));
    private async Task<(string Zip, ReleaseSidecar Metadata)> Package(params string[] paths)
    {
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var path in paths)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("payload");
            }
        var metadata = new ReleaseSidecar { ProjectId = "demo", DisplayName = "Demo", Version = "1.0.0", EntryPoint = "game.exe" };
        await SidecarPackageValidator.GenerateAsync(zip, zip + ".json", metadata);
        return (zip, metadata);
    }

    [Fact]
    public async Task ExternalSidecarDoesNotModifyZipAndPayloadRootResolves()
    {
        var (zip, metadata) = await Package("Windows/game.exe");
        metadata.PayloadRoot = "Windows";
        var before = await Hashing.Sha256FileAsync(zip);
        var payload = await SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "out"));
        Assert.True(File.Exists(Path.Combine(payload, "game.exe")));
        Assert.Equal(before, await Hashing.Sha256FileAsync(zip));
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, e => e.Name == "release.json");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/escape")]
    [InlineData("C:/escape")]
    [InlineData("dir/NUL.txt")]
    public async Task UnsafeEntriesAreRejectedBeforeExtraction(string path)
    {
        var (zip, metadata) = await Package("game.exe", path);
        var destination = Path.Combine(root, "out");
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task HashSizeCaseCollisionAndExpansionLimitsAreChecked()
    {
        var (zip, metadata) = await Package("game.exe", "GAME.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "duplicate")));
        (zip, metadata) = await Package("game.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "large"), new ZipIntakeLimits(1)));
        metadata.PackageSha256 = new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "hash")));
        metadata.PackageSize++;
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "size")));
    }

    [Fact]
    public async Task SymlinkAndMissingEntrypointAreRejected()
    {
        var (zip, metadata) = await Package("game.exe");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        metadata.PackageSize = new FileInfo(zip).Length;
        metadata.PackageSha256 = await Hashing.Sha256FileAsync(zip);
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "link")));
        (zip, metadata) = await Package("other.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => SidecarPackageValidator.ValidateAndExtractAsync(zip, metadata, Path.Combine(root, "missing")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
