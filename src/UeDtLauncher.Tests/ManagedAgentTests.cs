using Xunit;

namespace UeDtLauncher.Tests;

public class ManagedAgentTests
{
    [Fact]
    public void Protocol_RejectsUnknownCommandsAndVersions()
    {
        Assert.NotNull(ManagedAgentProtocol.Validate(new ManagedAgentRequest
        {
            ProtocolVersion = 99,
            Command = "status"
        }));
        Assert.NotNull(ManagedAgentProtocol.Validate(new ManagedAgentRequest
        {
            Command = "run-arbitrary-process"
        }));
        Assert.Null(ManagedAgentProtocol.Validate(new ManagedAgentRequest
        {
            Command = "status"
        }));
    }

    [Fact]
    public async Task FrameCodec_RoundTripsRequestAndRejectsOversizedLength()
    {
        var request = new ManagedAgentRequest { Command = "check", ProjectId = "project-a" };
        await using var stream = new MemoryStream();
        await ManagedAgentFrameCodec.WriteAsync(stream, request);
        stream.Position = 0;

        var loaded = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream);

        Assert.Equal(request.CorrelationId, loaded.CorrelationId);
        Assert.Equal("check", loaded.Command);
        Assert.Equal("project-a", loaded.ProjectId);

        var invalidPrefix = BitConverter.GetBytes(ManagedAgentProtocol.MaxFrameBytes + 1);
        await using var invalid = new MemoryStream(invalidPrefix);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(invalid));
    }

    [Fact]
    public void ManagedPaths_UseExplicitTestRootWithoutEscapingIt()
    {
        var original = Environment.GetEnvironmentVariable("UE_DT_AGENT_DATA_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "uedt-agent-path-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("UE_DT_AGENT_DATA_ROOT", root);
            var paths = ManagedLauncherPathLayout.Current();
            foreach (var path in new[] { paths.ConfigRoot, paths.StateRoot, paths.AppsRoot, paths.LogRoot, paths.CredentialRoot })
            {
                Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("UE_DT_AGENT_DATA_ROOT", original);
        }
    }

    [Fact]
    public async Task PortableMigration_PlansAndAppliesWithoutGuessingPaths()
    {
        using var temp = new TempDirectory();
        var sourceConfig = Path.Combine(temp.Path, "portable", "launcher.config.json");
        var sourceState = Path.Combine(temp.Path, "portable", ".state", "project-a", "windows-x64");
        Directory.CreateDirectory(sourceState);
        await File.WriteAllTextAsync(Path.Combine(sourceState, "install-state.json"), "{}");
        await JsonFiles.WriteAsync(sourceConfig, new LauncherConfig
        {
            ProjectId = "project-a",
            StateRootDir = ".state",
            InstallDir = "app",
            LogDir = "logs"
        });
        var managedRoot = Path.Combine(temp.Path, "managed");
        var layout = new ManagedLauncherPathLayout(
            managedRoot,
            Path.Combine(managedRoot, "config"),
            Path.Combine(managedRoot, "state"),
            Path.Combine(managedRoot, "apps"),
            Path.Combine(managedRoot, "logs"),
            Path.Combine(managedRoot, "credentials"));

        var plan = await PortableMigrationService.PlanAsync(sourceConfig, layout);
        Assert.False(plan.TargetAlreadyExists);
        Assert.Contains("project-a", plan.ProjectIds);
        await PortableMigrationService.ApplyAsync(plan);

        Assert.True(File.Exists(plan.TargetConfigPath));
        Assert.True(File.Exists(Path.Combine(layout.StateRoot, "project-a", "windows-x64", "install-state.json")));
        var migrated = await JsonFiles.ReadAsync<LauncherConfig>(plan.TargetConfigPath);
        Assert.Equal(layout.StateRoot, migrated.StateRootDir);
        Assert.True(Path.IsPathRooted(migrated.InstallDir));
    }

    [Fact]
    public async Task ManagedLaunch_RequiresInstalledManifestBeforeStartingAnything()
    {
        using var temp = new TempDirectory();
        var config = new LauncherConfig
        {
            DeploymentMode = "managed-agent",
            InstallDir = Path.Combine(temp.Path, "app"),
            InstalledManifestPath = Path.Combine(temp.Path, "state", "installed-manifest.json")
        };
        Assert.True(config.IsManagedDeployment);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ManagedAppLauncher.LaunchAsync(config));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-agent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
