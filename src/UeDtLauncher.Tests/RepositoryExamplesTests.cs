using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class RepositoryExamplesTests
{
    [Fact]
    public async Task AllExampleJsonFiles_AreValidJson()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(root, "examples"),
                "*.json",
                SearchOption.AllDirectories)
            .ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            using var document = await JsonDocument.ParseAsync(stream);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(current, "UeDtLauncher.sln")))
        {
            current = Path.GetDirectoryName(current)
                      ?? throw new DirectoryNotFoundException("Could not find the repository root.");
        }

        return current;
    }
}
