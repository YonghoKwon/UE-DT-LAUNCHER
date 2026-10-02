using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed partial class RuntimeDataTests
{
    [Fact]
    public void MissingRootIsUnknownButExplicitDefaultRootRemainsValid()
    {
        var c = Config(); var record = RuntimeTestSupport.Active(c, RuntimeState.LaunchPending);
        record.RuntimeData = RuntimeDataPolicy.Plan(c, record.Requester!, record.AttemptId!); RuntimeStore.Write(c, record);
        var path = RuntimeStore.RecordPath(c); var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["runtimeData"]!.AsObject().Remove("rootDirectory"); File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(c).State);
        json["runtimeData"]!["rootDirectory"] = null; File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(RuntimeState.LaunchPending, RuntimeStore.Observe(c).State);
    }

    [Theory]
    [InlineData("test/dev/stable/v2/windows-x64")]
    [InlineData("other/dev/stable/v1/windows-x64")]
    public void ValidButDifferentReleaseCannotAuthorizeThisInstallation(string release)
    {
        var c = Config(); var record = RuntimeTestSupport.Active(c, RuntimeState.LaunchPending);
        record.RuntimeData = RuntimeDataPolicy.Plan(c, record.Requester!, record.AttemptId!)! with { ReleaseId = release };
        RuntimeStore.Write(c, record);
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(c).State);
        // The legacy restore overload has no SelectedRelease; its install path must still prove the track.
        c.SelectedRelease = null; c.VersionedInstallRoot = null;
        Assert.Equal(RuntimeState.Unknown, RuntimeStore.Observe(c).State);
    }

    [Fact]
    public void LegacyRestoreContextCanReadCorrectBoundRecord()
    {
        var c = Config(); var record = RuntimeTestSupport.Active(c, RuntimeState.LaunchPending);
        record.RuntimeData = RuntimeDataPolicy.Plan(c, record.Requester!, record.AttemptId!); RuntimeStore.Write(c, record);
        c.SelectedRelease = null; c.VersionedInstallRoot = null;
        Assert.Equal(RuntimeState.LaunchPending, RuntimeStore.Observe(c).State);
    }

    [Fact]
    public void HostOwnCredentialPathIsProtectedEvenIfAgentPlanDoesNotContainIt()
    {
        var p = Plan() with { ProtectedDirectories = [Path.Combine(root, "unrelated")],
            RootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UE-DT Launcher", "credentials") };
        Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.Resolve(p));
    }

    [Fact]
    public void AgentResolvedSelectionIsPinnedAcrossLaterCatalogChanges()
    {
        var c = Config(); c.ProjectId = "test"; c.SelectedRelease = null; c.VersionPolicy = "latest";
        var selection = new ReleaseSelection("test", "dev", "stable", c.TargetPlatform, "v2");
        RuntimeLauncher.PinManagedSelection(c, new() { SelectedRelease = selection });
        Assert.Equal(selection, c.SelectedRelease); Assert.Equal("exact", c.VersionPolicy); Assert.Equal("v2", c.RequestedVersion);
        Assert.EndsWith(selection.ReleaseId.Replace('/', Path.DirectorySeparatorChar), c.InstallDir);
        Assert.Throws<InvalidDataException>(() => RuntimeLauncher.PinManagedSelection(c, new() { SelectedRelease = selection with { Version = "v3" } }));
    }

    [Fact]
    public void MissingManagedReleaseSelectionDoesNotStartOrBindAnything()
    {
        var c = Config(); var oldPath = c.InstallDir;
        Assert.Throws<InvalidDataException>(() => RuntimeLauncher.PinManagedSelection(c, new()));
        Assert.Equal(oldPath, c.InstallDir);
    }

    [Theory]
    [InlineData(511)] // 0777
    [InlineData(1023)] // 01777
    public void WritableActualDataFolderIsRejectedRegardlessOfStickyBit(int mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var p = Plan(); var paths = RuntimeDataPolicy.Resolve(p);
        RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), RuntimeIdentities.Current());
        File.SetUnixFileMode(paths.UserDirectory, (UnixFileMode)mode);
        try { Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), RuntimeIdentities.Current())); }
        finally { File.SetUnixFileMode(paths.UserDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void NonwritableLogFolderFailsPreflightAndPreservesData()
    {
        var p = Plan(); var paths = RuntimeDataPolicy.Resolve(p); var actor = RuntimeIdentities.Current();
        RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), actor);
        var save = Path.Combine(paths.UserDirectory, "save.txt"); File.WriteAllText(save, "keep");
        var logs = Path.GetDirectoryName(paths.LogFile)!;
        if (OperatingSystem.IsWindows())
        {
            var original = new DirectoryInfo(logs).GetAccessControl(); var denied = new DirectoryInfo(logs).GetAccessControl();
            denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(actor.Owner), FileSystemRights.WriteData, AccessControlType.Deny));
            new DirectoryInfo(logs).SetAccessControl(denied);
            try { Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), actor)); }
            finally { new DirectoryInfo(logs).SetAccessControl(original); }
        }
        else if (OperatingSystem.IsLinux() && actor.Owner != "0")
        {
            File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Assert.Throws<RuntimeDataException>(() => RuntimeDataPolicy.PrepareHost(new(Environment.ProcessPath!, root, [], p), actor)); }
            finally { File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        Assert.Equal("keep", File.ReadAllText(save));
        Assert.Empty(Directory.GetFiles(paths.UserDirectory, ".write-check-*"));
        Assert.False(File.Exists(paths.LogFile));
    }
}
