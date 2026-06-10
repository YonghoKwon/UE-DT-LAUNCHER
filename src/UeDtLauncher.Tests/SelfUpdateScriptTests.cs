using Xunit;

namespace UeDtLauncher.Tests;

public class SelfUpdateScriptTests
{
    [Fact]
    public void BuildUnixSwapScript_WaitsCopiesAndRestartsWithArgs()
    {
        var script = SelfUpdateManager.BuildUnixSwapScript("/opt/launcher", "/opt/launcher-update", "/opt/launcher/UeDtLauncher", 1234, "/opt/launcher/self-update-pending.json", new[] { "run", "--config", "my config.json" });

        Assert.Contains("while kill -0 1234", script);
        Assert.Contains("cp -rf \"/opt/launcher-update/.\" \"/opt/launcher/\"", script);
        Assert.Contains("chmod +x \"/opt/launcher/UeDtLauncher\"", script);
        Assert.Contains("rm -f \"/opt/launcher/self-update-pending.json\"", script);
        Assert.Contains("'run' '--config' 'my config.json'", script);
    }

    [Fact]
    public void BuildWindowsSwapScript_WaitsCopiesAndRestartsWithArgs()
    {
        var script = SelfUpdateManager.BuildWindowsSwapScript(@"C:\Launcher", @"C:\Launcher\launcher-update", @"C:\Launcher\UeDtLauncher.exe", 1234, @"C:\Launcher\self-update-pending.json", new[] { "gui" });

        Assert.Contains("PID eq 1234", script);
        Assert.Contains(@"xcopy /E /Y /I ""C:\Launcher\launcher-update\*"" ""C:\Launcher\""", script);
        Assert.Contains(@"del /F /Q ""C:\Launcher\self-update-pending.json""", script);
        Assert.Contains(@"start """" ""C:\Launcher\UeDtLauncher.exe"" ""gui""", script);
    }

    [Fact]
    public void TryApplyPendingUpdate_NoPendingFile_ReturnsFalse()
    {
        Assert.False(SelfUpdateManager.TryApplyPendingUpdate(new[] { "gui" }));
    }
}
