using Xunit;

namespace UeDtLauncher.Tests;

public class CatalogUpdaterTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _catalogPath;

    public CatalogUpdaterTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "uedt-catalog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _catalogPath = Path.Combine(_workDir, "catalog.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    private static CatalogReleaseUpdate Update(string version, string environment = "prod", string channel = "stable", string platform = "windows-x64", bool setLatest = false)
    {
        return new CatalogReleaseUpdate
        {
            ProjectId = "ue-dt-simulator",
            DisplayName = "UE-DT Simulator",
            Version = version,
            Environment = environment,
            Channel = channel,
            Platform = platform,
            ManifestUrl = $"https://updates.example.com/projects/ue-dt-simulator/{environment}/{channel}/{version}/{platform}/manifest.json",
            AllowedClientProfiles = new List<string> { "general", "developer" },
            SetLatest = setLatest
        };
    }

    [Fact]
    public async Task UpsertRelease_CreatesCatalogAndProject()
    {
        var catalog = await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.0.0", setLatest: true));

        Assert.True(File.Exists(_catalogPath));
        var project = Assert.Single(catalog.Projects);
        Assert.Equal("ue-dt-simulator", project.ProjectId);
        var release = Assert.Single(project.Releases);
        Assert.Equal("1.0.0", release.Version);
        Assert.True(release.IsLatest);
    }

    [Fact]
    public async Task UpsertRelease_ReplacesExistingReleaseWithSameKey()
    {
        await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.0.0"));
        var second = Update("1.0.0");
        second.Notes = "rebuilt";
        var catalog = await CatalogUpdater.UpsertReleaseAsync(_catalogPath, second);

        var release = Assert.Single(catalog.Projects.Single().Releases);
        Assert.Equal("rebuilt", release.Notes);
    }

    [Fact]
    public async Task UpsertRelease_SetLatest_ClearsOnlySameTrackSiblings()
    {
        await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.0.0", setLatest: true));
        await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("0.9.0", environment: "dev", channel: "dev", setLatest: true));
        var catalog = await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.1.0", setLatest: true));

        var releases = catalog.Projects.Single().Releases;
        Assert.False(releases.Single(r => r.Version == "1.0.0").IsLatest);
        Assert.True(releases.Single(r => r.Version == "1.1.0").IsLatest);
        Assert.True(releases.Single(r => r.Version == "0.9.0").IsLatest); // different env/channel track untouched
    }

    [Fact]
    public async Task RemoveRelease_RemovesAndOptionallyDropsEmptyProject()
    {
        await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.0.0"));
        var catalog = await CatalogUpdater.RemoveReleaseAsync(_catalogPath, "ue-dt-simulator", "1.0.0", "prod", "stable", "windows-x64", removeProjectIfEmpty: true);

        Assert.Empty(catalog.Projects);
    }

    [Fact]
    public async Task RemoveRelease_MissingRelease_IsNoOp()
    {
        await CatalogUpdater.UpsertReleaseAsync(_catalogPath, Update("1.0.0"));
        var catalog = await CatalogUpdater.RemoveReleaseAsync(_catalogPath, "ue-dt-simulator", "9.9.9", "prod", "stable", "windows-x64");

        Assert.Single(catalog.Projects.Single().Releases);
    }

    [Fact]
    public async Task UpsertRelease_ParsesExistingExampleCatalogs()
    {
        // The repo example catalogs must stay loadable by the updater.
        var exampleDir = FindExamplesDir();
        foreach (var example in Directory.EnumerateFiles(exampleDir, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_workDir, Path.GetFileName(example));
            File.Copy(example, target, overwrite: true);
            var catalog = await CatalogUpdater.UpsertReleaseAsync(target, Update("99.0.0"));
            Assert.Contains(catalog.Projects, p => p.ProjectId == "ue-dt-simulator");
        }
    }

    [Fact]
    public async Task UpsertRelease_RejectsMissingRequiredFields()
    {
        var update = Update("1.0.0");
        update.ManifestUrl = "";
        await Assert.ThrowsAsync<ArgumentException>(() => CatalogUpdater.UpsertReleaseAsync(_catalogPath, update));
    }

    private static string FindExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "examples", "catalogs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir is null
            ? throw new DirectoryNotFoundException("examples/catalogs not found above test base directory")
            : Path.Combine(dir, "examples", "catalogs");
    }
}
