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

    [Fact]
    public async Task LegacyImportDryRunDoesNotChangeFilesAndApplyPreservesSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configPath = Path.Combine(root, "launcher.config.json");
            var config = new LauncherConfig { ProjectId = "demo", InstallDir = "legacy", StateRootDir = "state" };
            Directory.CreateDirectory(Path.Combine(root, "legacy"));
            await File.WriteAllTextAsync(Path.Combine(root, "legacy", "game.exe"), "test payload");
            await JsonFiles.WriteAsync(configPath, config);
            LauncherPaths.ResolveInPlace(config, configPath);
            var manifest = new LauncherManifest { AppId = "demo", Version = "1.0.0", Platform = config.TargetPlatform, EntryPoint = "game.exe",
                Files = { new ManifestFile { Path = "game.exe", Size = 12, Sha256 = await Hashing.Sha256FileAsync(Path.Combine(config.InstallDir, "game.exe")) } } };
            await JsonFiles.WriteAsync(config.InstalledManifestPath, manifest);
            var before = await Hashing.Sha256FileAsync(Path.Combine(config.InstallDir, "game.exe"));
            var plan = await LegacyInstallImport.RunAsync(configPath, Path.Combine(root, "new"), false);
            Assert.False(Directory.Exists(plan.Destination));
            await Assert.ThrowsAsync<RuntimeBlockedException>(() => LegacyInstallImport.RunAsync(configPath, Path.Combine(root, "new"), true));
            RuntimeTestSupport.Stopped(config);
            var applied = await LegacyInstallImport.RunAsync(configPath, Path.Combine(root, "new"), true);
            Assert.True(applied.Applied);
            Assert.Equal(before, await Hashing.Sha256FileAsync(Path.Combine(applied.Destination, "game.exe")));
            Assert.Equal(before, await Hashing.Sha256FileAsync(Path.Combine(config.InstallDir, "game.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
