using Xunit;

namespace UeDtLauncher.Tests;

public class CatalogResolverTests
{
    private static DistributionCatalog BuildCatalog(params DistributionRelease[] releases)
    {
        return new DistributionCatalog
        {
            Projects = new List<DistributionProject>
            {
                new()
                {
                    ProjectId = "ue-dt-simulator",
                    DisplayName = "UE-DT Simulator",
                    Releases = releases.ToList()
                }
            }
        };
    }

    private static DistributionRelease Release(string version, string environment = "prod", string channel = "stable", string platform = "windows-x64", bool isLatest = false, params string[] profiles)
    {
        return new DistributionRelease
        {
            Version = version,
            Environment = environment,
            Channel = channel,
            Platform = platform,
            IsLatest = isLatest,
            ManifestUrl = $"https://updates.example.com/{version}/{platform}/manifest.json",
            AllowedClientProfiles = (profiles.Length == 0 ? new[] { "general", "developer" } : profiles).ToList()
        };
    }

    private static LauncherConfig Config(string profile = "developer", string environment = "prod", string channel = "stable", string platform = "windows-x64", string versionPolicy = "latest", string? requestedVersion = null)
    {
        return new LauncherConfig
        {
            ProjectId = "ue-dt-simulator",
            ClientProfile = profile,
            Environment = environment,
            Channel = channel,
            TargetPlatform = platform,
            VersionPolicy = versionPolicy,
            RequestedVersion = requestedVersion
        };
    }

    [Fact]
    public void SelectRelease_UnknownProject_Throws()
    {
        var catalog = BuildCatalog(Release("1.0.0"));
        var config = Config();
        config.ProjectId = "missing-project";
        Assert.Throws<InvalidOperationException>(() => CatalogResolver.SelectRelease(catalog, config));
    }

    [Fact]
    public void SelectRelease_MissingProjectId_Throws()
    {
        var catalog = BuildCatalog(Release("1.0.0"));
        var config = Config();
        config.ProjectId = null;
        Assert.Throws<InvalidOperationException>(() => CatalogResolver.SelectRelease(catalog, config));
    }

    [Fact]
    public void SelectRelease_FiltersByPlatformEnvironmentChannel()
    {
        var catalog = BuildCatalog(
            Release("1.0.0", platform: "windows-x64"),
            Release("2.0.0", platform: "linux-x64"),
            Release("3.0.0", environment: "dev", channel: "dev"));

        var selected = CatalogResolver.SelectRelease(catalog, Config(platform: "linux-x64"));
        Assert.Equal("2.0.0", selected.Version);
    }

    [Fact]
    public void SelectRelease_ExcludesDisallowedProfile()
    {
        var catalog = BuildCatalog(
            Release("1.0.0", profiles: "developer"),
            Release("0.9.0", profiles: "general", isLatest: true));

        var selected = CatalogResolver.SelectRelease(catalog, Config(profile: "general"));
        Assert.Equal("0.9.0", selected.Version);
    }

    [Fact]
    public void SelectRelease_PrefersIsLatestFlag()
    {
        var catalog = BuildCatalog(
            Release("2.0.0"),
            Release("1.5.0", isLatest: true));

        var selected = CatalogResolver.SelectRelease(catalog, Config());
        Assert.Equal("1.5.0", selected.Version);
    }

    [Fact]
    public void SelectRelease_FallsBackToHighestVersion()
    {
        var catalog = BuildCatalog(
            Release("1.2.0"),
            Release("1.10.0"),
            Release("1.9.9"));

        var selected = CatalogResolver.SelectRelease(catalog, Config());
        Assert.Equal("1.10.0", selected.Version);
    }

    [Fact]
    public void SelectRelease_ExactPolicy_RequiresRequestedVersion()
    {
        var catalog = BuildCatalog(Release("1.0.0"));
        Assert.Throws<InvalidOperationException>(() => CatalogResolver.SelectRelease(catalog, Config(versionPolicy: "exact")));
    }

    [Fact]
    public void SelectRelease_ExactPolicy_SelectsRequestedVersion()
    {
        var catalog = BuildCatalog(Release("1.0.0"), Release("1.1.0", isLatest: true));
        var selected = CatalogResolver.SelectRelease(catalog, Config(versionPolicy: "exact", requestedVersion: "1.0.0"));
        Assert.Equal("1.0.0", selected.Version);
    }

    [Fact]
    public void SelectRelease_NoMatch_Throws()
    {
        var catalog = BuildCatalog(Release("1.0.0", platform: "windows-x64"));
        Assert.Throws<InvalidOperationException>(() => CatalogResolver.SelectRelease(catalog, Config(platform: "linux-x64")));
    }

    [Fact]
    public void SelectRelease_NoMatch_MessageListsAvailableReleasesAndPlatformHint()
    {
        // Mimics the real "windows-64" typo: catalog has windows-64, client wants windows-x64.
        var catalog = BuildCatalog(Release("0.0.1", platform: "windows-64", isLatest: true));
        var ex = Assert.Throws<InvalidOperationException>(() => CatalogResolver.SelectRelease(catalog, Config(platform: "windows-x64")));

        Assert.Contains("0.0.1", ex.Message);
        Assert.Contains("windows-64", ex.Message);   // what the catalog has
        Assert.Contains("windows-x64", ex.Message);  // what the client requested
        Assert.Contains("platform", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hint", ex.Message);
    }

    [Fact]
    public void SelectRelease_ExactVersionMissing_MessageListsAvailableVersions()
    {
        var catalog = BuildCatalog(Release("1.0.0"), Release("1.1.0"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CatalogResolver.SelectRelease(catalog, Config(versionPolicy: "exact", requestedVersion: "9.9.9")));
        Assert.Contains("1.0.0", ex.Message);
        Assert.Contains("1.1.0", ex.Message);
    }

    [Theory]
    [InlineData("linux-x64", "prod", "stable", "latest")]
    [InlineData("windows-x64", "dev", "stable", "latest")]
    [InlineData("windows-x64", "prod", "dev", "latest")]
    [InlineData("windows-x64", "prod", "stable", "exact")]
    public void ValidateClientSelection_GeneralProfile_RejectsRestrictedCombos(string platform, string environment, string channel, string versionPolicy)
    {
        var config = Config(profile: "general", environment: environment, channel: channel, platform: platform, versionPolicy: versionPolicy);
        Assert.Throws<InvalidOperationException>(() => CatalogResolver.ValidateClientSelection(config));
    }

    [Fact]
    public void ValidateClientSelection_GeneralProfile_AllowsProdStableLatest()
    {
        var config = Config(profile: "general");
        CatalogResolver.ValidateClientSelection(config);
    }

    [Fact]
    public void ValidateClientSelection_DeveloperProfile_AllowsDevSelections()
    {
        var config = Config(profile: "developer", environment: "dev", channel: "dev", platform: "linux-x64", versionPolicy: "exact", requestedVersion: "1.0.0");
        CatalogResolver.ValidateClientSelection(config);
    }
}
