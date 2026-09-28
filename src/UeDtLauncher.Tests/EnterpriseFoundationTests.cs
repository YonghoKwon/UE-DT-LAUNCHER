using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;
public class EnterpriseFoundationTests
{
    [Fact] public void PoscoMainColorSupportsWhiteText()
    {
        Assert.Equal("#ff05507d",LauncherVisualTokens.Accent.ToString().ToLowerInvariant());
        Assert.True(LauncherVisualTokens.ContrastRatio(Avalonia.Media.Colors.White,LauncherVisualTokens.Accent)>=4.5);
    }
    [Fact] public void PresentationRetainsBoundedSanitizedLogsAndDoesNotRegressApply()
    {
        var state=new LauncherOperationPresentation(); state.Begin(LauncherUiOperation.Update);
        state.Stage("Download"); state.Stage("Apply"); state.Stage("Manifest");
        Assert.Equal("적용",state.CurrentStage);
        for(var i=0;i<510;i++) state.Append("line "+i);
        Assert.Equal(500,state.Logs.Count); state.Complete("복구 완료");
        Assert.Equal("복구 완료",state.Title); Assert.Equal(100d,state.Percent);
        Assert.DoesNotContain("secret",LauncherOperationPresentation.GeneralProgress("secret C:\\internal"));
    }
    [Fact] public async Task PreferencesUseOnlySupportedScaleAndPreserveOtherConfigs()
    {
        var root=Path.Combine(Path.GetTempPath(),"ui-prefs-"+Guid.NewGuid().ToString("N"));
        try { var path=Path.Combine(root,"ui.json"); await new LauncherUiPreferences(1.5,true).SaveAsync(path); Assert.Equal(new(1.5,true),LauncherUiPreferences.Load(path));
            File.WriteAllText(path,"invalid"); Assert.Equal(new(),LauncherUiPreferences.Load(path));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>new LauncherUiPreferences(3).SaveAsync(path)); }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
