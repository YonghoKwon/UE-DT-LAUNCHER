using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class LauncherConfigurationUiTests
{
    [AvaloniaTheory]
    [InlineData("{\"secret-path\":BROKEN}")]
    [InlineData("null")]
    [InlineData("{\"projects\":null}")]
    [InlineData("{\"projects\":[null]}")]
    [InlineData("{\"security\":null}")]
    public void MalformedConfigHasSafeMessageAndSupportId(string content)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-ui-bad-config-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"client.json");File.WriteAllText(path,content);
        var window=new MainWindow(new(path,LauncherConfigSource.Explicit,false),null,new(),false);
        try
        {
            window.Show();Dispatcher.UIThread.RunJobs();
            var text=string.Join("\n",window.GetLogicalDescendants().OfType<TextBlock>().Select(t=>t.Text));
            Assert.Contains("설정",text);Assert.Contains("지원 ID:",text);
            Assert.DoesNotContain("secret-path",text);Assert.DoesNotContain("BROKEN",text);Assert.DoesNotContain(root,text);
            var button=window.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="primary-action");
            Assert.Contains("다시 확인",AutomationProperties.GetName(button));
        }
        finally{window.Close();Directory.Delete(root,true);}
    }
}
