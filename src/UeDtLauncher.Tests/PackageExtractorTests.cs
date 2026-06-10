using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public class PackageExtractorTests : IDisposable
{
    private readonly string _workDir;

    public PackageExtractorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "uedt-extract-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task ExtractAsync_Zip_ExtractsNestedEntries()
    {
        var archivePath = Path.Combine(_workDir, "good.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("sub/dir/file.txt");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("hello");
        }

        var destination = Path.Combine(_workDir, "out");
        await PackageExtractor.ExtractAsync(archivePath, destination, "zip");

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "sub", "dir", "file.txt")));
    }

    [Fact]
    public async Task ExtractAsync_Zip_RejectsTraversalEntries()
    {
        var archivePath = Path.Combine(_workDir, "evil.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("escape");
        }

        var destination = Path.Combine(_workDir, "out");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageExtractor.ExtractAsync(archivePath, destination, "zip"));
        Assert.False(File.Exists(Path.Combine(_workDir, "evil.txt")));
    }

    [Fact]
    public async Task ExtractAsync_UnknownFormat_Throws()
    {
        var archivePath = Path.Combine(_workDir, "file.rar");
        await File.WriteAllTextAsync(archivePath, "not an archive");
        await Assert.ThrowsAsync<NotSupportedException>(() => PackageExtractor.ExtractAsync(archivePath, Path.Combine(_workDir, "out"), null));
    }
}
