using System.Text.Json;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class RuntimeUxTests
{
    [Fact]
    public void LegacyResponseDoesNotInventRuntimeCapabilities()
    {
        var response = JsonSerializer.Deserialize<ManagedAgentResponse>("{\"success\":true}", JsonFiles.Options)!;
        Assert.Empty(response.AgentCapabilities);
        Assert.True(ManagedAgentProtocol.RequiresRuntimeCapability("repair"));
        Assert.False(ManagedAgentProtocol.RequiresRuntimeCapability("catalog"));
    }

    [Fact]
    public void GeneralRuntimeBlockHasActionableText()
    {
        var model = new LauncherDashboardViewModel();
        model.GeneralState = GeneralLauncherState.RuntimeBlocked;
        Assert.Equal(PrimaryActionKind.Disabled, model.PrimaryAction);
        Assert.Equal("프로그램 종료 후 다시 확인", model.PrimaryActionText);
        var error = new RuntimeBlockedException(new(RuntimeState.Running,"app-running","실행 중—프로그램을 종료한 뒤 다시 시도해 주세요."));
        Assert.Equal(error.Message, model.FriendlyError(error));
    }

    [Fact]
    public async Task ShortcutProfilePinsVersionAndPreservesInstallBase()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-shortcut-"+Guid.NewGuid().ToString("N"));
        try
        {
            var config = new LauncherConfig { InstallDir=Path.Combine(root,"apps"), StateRootDir=Path.Combine(root,"state"), DistributionServerUrl="https://example.com", ProjectId="demo" };
            LauncherPaths.ResolveInPlace(config,Path.Combine(root,"original.json"));
            var originalBase=config.InstallDir;
            VersionedReleasePaths.Bind(config,new("demo","prod","stable","windows-x64","1.2.0"));
            var file=WindowsIntegration.CreateLaunchProfile(config,"1.2.0",Path.Combine(root,"profiles"));
            var saved=await JsonFiles.ReadAsync<LauncherConfig>(file);
            Assert.Equal(originalBase,saved.InstallDir); Assert.Equal("exact",saved.VersionPolicy); Assert.Equal("1.2.0",saved.RequestedVersion);
            Assert.False(saved.WindowsIntegration.CreateDesktopShortcut);
            if (OperatingSystem.IsWindows())
            {
                var link=Path.Combine(root,"test.lnk");
                WindowsIntegration.CreateLauncherShortcut(link,Environment.ProcessPath!,"run --config \""+file+"\"",null);
                Assert.True(new FileInfo(link).Length>0);
                var bytes=File.ReadAllBytes(link);
                WindowsIntegration.CreateLauncherShortcut(link,Environment.ProcessPath!,"changed",null);
                Assert.Equal(bytes,File.ReadAllBytes(link));
            }
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
