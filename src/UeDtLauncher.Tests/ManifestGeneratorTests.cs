using Xunit;

namespace UeDtLauncher.Tests;

public class ManifestGeneratorTests : IDisposable
{
    private readonly string _packageDir;

    public ManifestGeneratorTests()
    {
        _packageDir = Path.Combine(Path.GetTempPath(), "uedt-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_packageDir, "Windows"));
        File.WriteAllText(Path.Combine(_packageDir, "Windows", "game.exe"), "binary");
        File.WriteAllText(Path.Combine(_packageDir, "readme.txt"), "hello");
    }

    public void Dispose()
    {
        if (Directory.Exists(_packageDir)) Directory.Delete(_packageDir, recursive: true);
    }

    [Fact]
    public async Task GenerateAsync_ProducesNormalizedPathsAndHashes()
    {
        var output = Path.Combine(_packageDir, "manifest.json");
        await ManifestGenerator.GenerateAsync(_packageDir, output, "https://updates.example.com/files/", "Windows/game.exe", "1.2.3", "stable", "windows-x64");

        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(output);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("Windows/game.exe", manifest.EntryPoint);
        Assert.Equal("https://updates.example.com/files", manifest.BaseUrl);
        Assert.Equal(2, manifest.Files.Count);

        var exe = manifest.Files.Single(f => f.Path == "Windows/game.exe");
        Assert.True(exe.Executable);
        Assert.Equal(new FileInfo(Path.Combine(_packageDir, "Windows", "game.exe")).Length, exe.Size);
        Assert.Equal(await Hashing.Sha256FileAsync(Path.Combine(_packageDir, "Windows", "game.exe")), exe.Sha256);
        Assert.DoesNotContain(manifest.Files, f => f.Path.Contains('\\'));
    }

    [Fact]
    public async Task GenerateAsync_MissingEntryPoint_Throws()
    {
        var output = Path.Combine(_packageDir, "manifest.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManifestGenerator.GenerateAsync(_packageDir, output, "https://updates.example.com/files", "Windows/missing.exe", "1.0.0", "stable", "windows-x64"));
    }

    [Fact]
    public async Task GenerateAsync_MissingDirectory_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            ManifestGenerator.GenerateAsync(Path.Combine(_packageDir, "nope"), "out.json", "https://u.example.com", "a.exe", "1.0.0", "stable", "windows-x64"));
    }
}
