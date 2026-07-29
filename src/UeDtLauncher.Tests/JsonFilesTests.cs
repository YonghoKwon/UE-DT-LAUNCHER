using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class JsonFilesTests
{
    [Fact]
    public async Task WriteAsync_ReplacesExistingJsonAndLeavesNoTempFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        await File.WriteAllTextAsync(path, """{"version":"old"}""");

        await JsonFiles.WriteAsync(path, new InstallState { Version = "new" });

        var state = await JsonFiles.ReadAsync<InstallState>(path);
        Assert.Equal("new", state.Version);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_SerializationFailurePreservesExistingJson()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        var original = """{"version":"old"}""";
        await File.WriteAllTextAsync(path, original);
        var cyclic = new CyclicValue();
        cyclic.Self = cyclic;

        await Assert.ThrowsAsync<JsonException>(() => JsonFiles.WriteAsync(path, cyclic));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_CancellationPreservesExistingJson()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        var original = """{"version":"old"}""";
        await File.WriteAllTextAsync(path, original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            JsonFiles.WriteAsync(path, new InstallState { Version = "new" }, cancellation.Token));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    private sealed class CyclicValue
    {
        public CyclicValue? Self { get; set; }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-json-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
