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
        var request = new ManagedAgentRequest { Command = "check", ProjectId = "project-a", StreamProgress = true };
        await using var stream = new MemoryStream();
        await ManagedAgentFrameCodec.WriteAsync(stream, request);
        stream.Position = 0;

        var loaded = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream);

        Assert.Equal(request.CorrelationId, loaded.CorrelationId);
        Assert.Equal("check", loaded.Command);
        Assert.Equal("project-a", loaded.ProjectId);
        Assert.True(loaded.StreamProgress);

        var invalidPrefix = BitConverter.GetBytes(ManagedAgentProtocol.MaxFrameBytes + 1);
        await using var invalid = new MemoryStream(invalidPrefix);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(invalid));
    }

    [Fact]
    public async Task StreamingResponses_ReportProgressUntilFinalResponse()
    {
        var request = new ManagedAgentRequest { Command = "update", ProjectId = "project-a", StreamProgress = true };
        await using var stream = new MemoryStream();
        await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
        {
            CorrelationId = request.CorrelationId,
            IsFinal = false,
            Status = "progress",
            Progress = [new ManagedAgentProgress("Download", "Downloading", 50)]
        });
        await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
        {
            CorrelationId = request.CorrelationId,
            IsFinal = true,
            Success = true,
            Status = "completed",
            ProjectStatus = new ManagedProjectStatus(true, "1.0.0", "1.0.0", false, 0, 0, true)
        });
        stream.Position = 0;
        var progress = new List<ManagedAgentProgress>();

        var response = await ManagedAgentClient.ReadStreamingResponsesAsync(stream, request, progress.Add);

        Assert.Single(progress);
        Assert.Equal("Download", progress[0].Stage);
        Assert.True(response.Success);
        Assert.False(response.ProjectStatus!.UpdateRequired);
    }

    [Fact]
    public async Task ProjectStatus_ReportsVersionAndFileDifferences()
    {
        using var temp = new TempDirectory();
        var install = Path.Combine(temp.Path, "app");
        var state = Path.Combine(temp.Path, "state");
        var backup = Path.Combine(temp.Path, "backup");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(install, "changed.bin"), "old");
        var config = new LauncherConfig
        {
            InstallDir = install,
            BackupDir = backup,
            InstalledManifestPath = Path.Combine(state, "installed-manifest.json")
        };
        await JsonFiles.WriteAsync(config.InstalledManifestPath, new LauncherManifest { Version = "1.0.0" });
        var manifest = new LauncherManifest
        {
            Version = "2.0.0",
            Files =
            {
                new ManifestFile { Path = "changed.bin", Sha256 = new string('0', 64), Size = 3 },
                new ManifestFile { Path = "missing.bin", Sha256 = new string('1', 64), Size = 1 }
            }
        };

        var status = await ManagedProjectStatusInspector.InspectAsync(config, manifest);

        Assert.True(status.IsInstalled);
        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Equal("2.0.0", status.AvailableVersion);
        Assert.True(status.UpdateRequired);
        Assert.Equal(1, status.MissingFiles);
        Assert.Equal(1, status.ChangedFiles);
        Assert.False(status.HasBackup);
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
