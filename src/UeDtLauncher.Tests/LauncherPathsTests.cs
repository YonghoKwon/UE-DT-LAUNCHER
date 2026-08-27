using Xunit;

namespace UeDtLauncher.Tests;

public class LauncherPathsTests
{
    [Fact]
    public void ResolveInPlace_IsolatesStateByProjectAndPlatform()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "config", "launcher.config.json");
        var first = Config("project-a");
        var second = Config("project-b");

        LauncherPaths.ResolveInPlace(first, configPath);
        LauncherPaths.ResolveInPlace(second, configPath);

        Assert.Equal(Path.Combine(temp.Path, "config", "app"), first.InstallDir);
        Assert.Equal(
            Path.Combine(temp.Path, "config", ".state", "project-a", "windows-x64", "staging"),
            first.StagingDir);
        Assert.Equal(
            Path.Combine(temp.Path, "config", ".state", "project-b", "windows-x64", "staging"),
            second.StagingDir);
        Assert.NotEqual(first.InstallStatePath, second.InstallStatePath);
        Assert.NotEqual(LauncherPaths.UpdateLockPath(first), LauncherPaths.UpdateLockPath(second));
    }

    [Fact]
    public void ResolveInPlace_UsesConfigDirectoryForAllRelativeRuntimePaths()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "nested", "launcher.config.json");
        var config = Config("project-a");
        config.LogDir = "runtime-logs";
        config.CatalogPublicKeyPath = "keys/catalog.pem";
        config.ManifestPublicKeyPath = "keys/manifest.pem";
        config.WindowsIntegration.IconPath = "assets/app.ico";

        LauncherPaths.ResolveInPlace(config, configPath, "installs/project-a");

        Assert.Equal(Path.Combine(temp.Path, "nested", "installs", "project-a"), config.InstallDir);
        Assert.Equal(Path.Combine(temp.Path, "nested", "runtime-logs"), config.LogDir);
        Assert.Equal(Path.Combine(temp.Path, "nested", "keys", "catalog.pem"), config.CatalogPublicKeyPath);
        Assert.Equal(Path.Combine(temp.Path, "nested", "keys", "manifest.pem"), config.ManifestPublicKeyPath);
        Assert.Equal(Path.Combine(temp.Path, "nested", "assets", "app.ico"), config.WindowsIntegration.IconPath);
    }

    [Fact]
    public async Task LoadResolvedAsync_MigratesLegacySingleProjectMetadataAndBackups()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "launcher.config.json");
        await JsonFiles.WriteAsync(configPath, Config("project-a"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "installed-manifest.json"), """{"version":"1.0.0"}""");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "install-state.json"), """{"version":"1.0.0"}""");
        var legacyBackupFile = Path.Combine(temp.Path, ".backup", "20260101000000", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyBackupFile)!);
        await File.WriteAllTextAsync(legacyBackupFile, "old");

        var resolved = await LauncherPaths.LoadResolvedAsync(configPath);

        Assert.True(File.Exists(resolved.InstalledManifestPath));
        Assert.True(File.Exists(resolved.InstallStatePath));
        Assert.True(File.Exists(Path.Combine(resolved.BackupDir, "20260101000000", "game.exe")));
    }

    [Fact]
    public async Task LoadResolvedAsync_DoesNotGuessLegacyOwnerForMultipleProjects()
    {
        using var temp = new TempDirectory();
        var configPath = Path.Combine(temp.Path, "launcher.config.json");
        var config = Config("project-a");
        config.Projects =
        [
            new ProjectUiConfig { ProjectId = "project-a" },
            new ProjectUiConfig { ProjectId = "project-b" }
        ];
        await JsonFiles.WriteAsync(configPath, config);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "installed-manifest.json"), """{"version":"1.0.0"}""");

        var resolved = await LauncherPaths.LoadResolvedAsync(configPath);

        Assert.False(File.Exists(resolved.InstalledManifestPath));
        Assert.True(File.Exists(Path.Combine(temp.Path, "installed-manifest.json")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("project/a")]
    [InlineData(".")]
    [InlineData("")]
    public void For_RejectsUnsafeProjectStateKey(string projectId)
    {
        using var temp = new TempDirectory();
        var config = Config(projectId);

        Assert.Throws<InvalidOperationException>(() =>
            LauncherPaths.For(config, Path.Combine(temp.Path, "launcher.config.json")));
    }

    private static LauncherConfig Config(string projectId) => new()
    {
        ProjectId = projectId,
        TargetPlatform = "windows-x64",
        InstallDir = "app",
        StateRootDir = ".state",
        StagingDir = ".staging",
        BackupDir = ".backup",
        InstalledManifestPath = "installed-manifest.json",
        InstallStatePath = "install-state.json",
        AppPidPath = "app.pid",
        LogDir = "logs"
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-launcher-path-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
