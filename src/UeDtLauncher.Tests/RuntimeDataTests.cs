using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed partial class RuntimeDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uedt-runtime-data-" + Guid.NewGuid().ToString("N"));
    public RuntimeDataTests()
    {
        if (OperatingSystem.IsWindows())
        {
            // The workstation temp directory may intentionally grant sandbox accounts write access.
            // Provision only this freshly-created test namespace; never relax product checks.
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var current = WindowsIdentity.GetCurrent().User!; security.SetOwner(current);
            foreach (var sid in new[] { current, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public void Dispose() => Directory.Delete(root, true);
    private LauncherConfig Config()
    {
        var config = new LauncherConfig
    {
        SchemaVersion = 3, Environment = "dev", DistributionServerUrl = "https://distribution.invalid",
        InstallDir = Path.Combine(root, "apps", "v1"), VersionedInstallRoot = Path.Combine(root, "apps"),
        StateRootDir = Path.Combine(root, "state"), StagingDir = Path.Combine(root, "state", "staging"),
        BackupDir = Path.Combine(root, "state", "backup"), LogDir = Path.Combine(root, "launcher-logs"),
        InstallStatePath = Path.Combine(root, "state", "install-state.json"),
        SelectedRelease = new("test", "dev", "stable", OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64", "v1"),
        RuntimeData = new() { Enabled = true, RootDirectory = Path.Combine(root, "data") }
        };
        VersionedReleasePaths.Bind(config, config.SelectedRelease!);
        return config;
    }
    private RuntimeDataPlan Plan(LauncherConfig? c = null) => RuntimeDataPolicy.Plan(c ?? Config(), RuntimeIdentities.Current(), Guid.NewGuid().ToString("N"))!;

    [Fact]
    public void LegacyAbsentOrDisabledSettingsPreserveArguments()
    {
        var c = Config(); c.SchemaVersion = 1; c.RuntimeData = null; c.LaunchArguments = ["-UserDir=legacy"];
        Assert.Null(RuntimeDataPolicy.Plan(c, RuntimeIdentities.Current(), Guid.NewGuid().ToString("N")));
        c.RuntimeData = new() { Enabled = false }; RuntimeDataPolicy.ValidateConfiguration(c);
    }
    [Theory]
    [InlineData("-UserDir=/old")]
    [InlineData("-userdir")]
    [InlineData("-ABSLOG=/old/log")]
    public void ReservedArgumentConflictIsRejectedBeforeDataCreation(string arg)
    {
        var c = Config(); c.LaunchArguments = [arg];
        Assert.Throws<RuntimeDataException>(() => Plan(c)); Assert.False(Directory.Exists(c.RuntimeData!.RootDirectory));
    }
    [Fact]
    public void OnlySchema3DistributionPerUserPerReleaseModeIsSupported()
    {
        var c = Config(); c.SchemaVersion = 2; Assert.Throws<RuntimeDataException>(() => Plan(c));
        c.SchemaVersion = 3; c.RuntimeData!.Policy = "shared"; Assert.Throws<RuntimeDataException>(() => Plan(c));
        c.RuntimeData.Policy = "per-user-per-release"; c.RuntimeData.Adapter = "unknown"; Assert.Throws<RuntimeDataException>(() => Plan(c));
        c.RuntimeData.Adapter = "unreal-engine"; c.DistributionServerUrl = null; Assert.Throws<RuntimeDataException>(() => Plan(c));
    }
    [Theory]
    [InlineData("apps")]
    [InlineData("state")]
    [InlineData("state/backup")]
    [InlineData("launcher-logs")]
    [InlineData(".")]
    public void DataAndManagedStorageMayNotOverlap(string relative)
    {
        var c = Config(); c.RuntimeData!.RootDirectory = Path.GetFullPath(Path.Combine(root, relative));
        Assert.Throws<RuntimeDataException>(() => Plan(c));
    }
    [Fact]
    public void PathsPartitionOwnersReleasesAndAttemptsWithoutCreatingDirectories()
    {
        var p = Plan(); var first = RuntimeDataPolicy.Resolve(p);
        var nextAttempt = RuntimeDataPolicy.Resolve(p with { AttemptId = Guid.NewGuid().ToString("N") });
        var nextRelease = RuntimeDataPolicy.Resolve(p with { ReleaseId = p.ReleaseId.Replace("/v1/", "/v2/") });
        var otherOwner = RuntimeDataPolicy.Resolve(p with { Owner = "unrelated-owner" });
        Assert.Equal(first.UserDirectory, nextAttempt.UserDirectory); Assert.NotEqual(first.LogFile, nextAttempt.LogFile);
        Assert.NotEqual(first.UserDirectory, nextRelease.UserDirectory); Assert.NotEqual(first.UserDirectory, otherOwner.UserDirectory);
        Assert.False(Directory.Exists(first.Root));
    }
    [Fact]
    public void AuthenticatedHostPreparesPrivateDirectoryAndSeparateArguments()
    {
        var p = Plan(); var original = new RuntimeHostRequest(Environment.ProcessPath!, root, ["-windowed"], p);
        var result = RuntimeDataPolicy.PrepareHost(original, RuntimeIdentities.Current());
        var paths = RuntimeDataPolicy.Resolve(p);
        Assert.Null(result.RuntimeData); Assert.Equal(root, result.WorkingDirectory); Assert.Equal("-windowed", result.Arguments[0]);
        Assert.Contains(result.Arguments, a => a.StartsWith("-UserDir=")); Assert.Contains(result.Arguments, a => a.StartsWith("-abslog="));
        Assert.True(Directory.Exists(paths.UserDirectory)); Assert.Empty(Directory.GetFiles(paths.UserDirectory));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.UserDirectory));
    }
    [Fact]
    public void DifferentOwnerCannotPrepareData()
    {
        var p = Plan() with { Owner = "different" };
        Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), RuntimeIdentities.Current()));
        Assert.False(Directory.Exists(p.RootDirectory));
    }
    [Fact]
    public void NativeRunnerRejectsUnpreparedDataPlan()
    {
        Assert.Throws<InvalidDataException>(() => NativeProcessFamily.Run(new(Environment.ProcessPath!, root, [], Plan()), _ => throw new Exception("must not start")));
    }
    [Fact]
    public void RelativeAndLinkedRootsAreRejected()
    {
        var c = Config(); c.RuntimeData!.RootDirectory = "relative"; Assert.Throws<RuntimeDataException>(() => Plan(c));
        if (!OperatingSystem.IsLinux()) return;
        var link = Path.Combine(root, "linked"); Directory.CreateSymbolicLink(link, root);
        c.RuntimeData.RootDirectory = Path.Combine(link, "data"); Assert.Throws<RuntimeDataException>(() => Plan(c));
    }
    [Fact]
    public void ModifiedPlanCannotEscapeOrIntroduceManagedStorageOverlap()
    {
        var p = Plan(); Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.Resolve(p with { ReleaseId = "../outside" }));
        Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.Resolve(p with { RootDirectory = Path.Combine(root, "apps") }));
    }
    [Fact]
    public void ExistingRequestAndTicketJsonRemainReadableWithoutOptionalDataFields()
    {
        var request = JsonSerializer.Deserialize<RuntimeHostRequest>("{\"executable\":\"app\",\"workingDirectory\":\"cwd\",\"arguments\":[]}", JsonFiles.Options)!;
        Assert.Null(request.RuntimeData);
        var ticket = JsonSerializer.Deserialize<RuntimeLaunchTicket>("{\"attemptId\":\"a\",\"token\":\"t\",\"installationId\":\"i\",\"hostExecutable\":\"h\"}", JsonFiles.Options)!;
        Assert.False(ticket.RequiresRuntimeData);
    }
}
