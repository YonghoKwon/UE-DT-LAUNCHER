using Xunit;

namespace UeDtLauncher.Tests;
public class VersionedReleaseTests
{
    [Fact]
    public void VersionedPathsAreStableAndSeparateTracksAndVersions()
    {
        var root = Path.Combine(Path.GetTempPath(), "version-path-tests");
        var config = new LauncherConfig { DistributionServerUrl = "https://updates.example.com", InstallDir = "apps", StateRootDir = "state" };
        LauncherPaths.ResolveInPlace(config, Path.Combine(root, "launcher.config.json"));
        var first = new ReleaseSelection("demo", "prod", "stable", "windows-x64", "1.0.0");
        VersionedReleasePaths.Bind(config, first); var firstDir = config.InstallDir; var firstState = config.InstallStatePath;
        VersionedReleasePaths.Bind(config, first); Assert.Equal(firstDir, config.InstallDir);
        VersionedReleasePaths.Bind(config, first with { Version = "2.0.0" }); Assert.NotEqual(firstDir, config.InstallDir);
        Assert.NotEqual(firstState, config.InstallStatePath);
        VersionedReleasePaths.Bind(config, first with { Environment = "dev", Channel = "dev" }); Assert.NotEqual(firstDir, config.InstallDir);
        Assert.Throws<InvalidDataException>(() => VersionedReleasePaths.Bind(config, first with { Version = "../escape" }));
    }
    [Fact]
    public async Task SelectionSurvivesIpcRoundTrip()
    {
        var request = new ManagedAgentRequest { Command = "update", ProjectId = "demo", Selection = new("demo", "dev", "dev", "linux-x64", "1.2.3") };
        using var stream = new MemoryStream(); await ManagedAgentFrameCodec.WriteAsync(stream, request);
        stream.Position = 0; var decoded = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream);
        Assert.Equal(request.Selection, decoded.Selection);
    }
}
