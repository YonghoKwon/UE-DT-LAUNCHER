using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class ManagedDisplayTemplateTests
{
    [Theory][InlineData("windows-x64")][InlineData("linux-x64")]
    public void TemplateContainsOnlyDisplaySelectionAndLoadsAsManagedClient(string platform)
    {
        var template=LauncherConfigurationTemplates.ManagedClient("demo",platform);
        var json=JsonSerializer.Serialize(template,JsonFiles.Options);
        using var parsed=JsonDocument.Parse(json);
        Assert.Equal(new[]{"channel","deploymentMode","environment","manifestUrl","projectId","schemaVersion","targetPlatform","versionPolicy"},parsed.RootElement.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal));
        var config=JsonSerializer.Deserialize<LauncherConfig>(json,JsonFiles.Options)!;
        var context=ManagedClientContext.Create(config);
        Assert.Equal("managed-agent",context.DeploymentMode);Assert.Equal("demo",context.ProjectId);
        Assert.Null(context.DistributionServerUrl);Assert.Equal("",context.ManifestUrl);
    }
    [Fact]
    public async Task CliExactOutputPreservesExistingFilesAndNeverWritesOperationalFields()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-managed-display-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"display.json");
        try
        {
            var args=new[]{"sample-config","--mode","managed-client","--project-id","demo","--platform","linux-x64","--environment","dev","--channel","beta","--version-policy","exact","--version","2.0.0","--output",path};
            Assert.Equal(0,await Program.MainAsync(args));
            using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("2.0.0",doc.RootElement.GetProperty("requestedVersion").GetString());
            foreach(var name in new[]{"security","credentialName","installDir","stateRootDir","distributionServerUrl","clientProfile"})Assert.False(doc.RootElement.TryGetProperty(name,out _));
            await File.WriteAllTextAsync(path,"user-owned");
            Assert.Equal(1,await Program.MainAsync(args));Assert.Equal("user-owned",await File.ReadAllTextAsync(path));
            Assert.Equal(0,await Program.MainAsync([..args,"--FORCE"]));
            Assert.Empty(Directory.EnumerateFiles(root,"*.tmp"));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory][InlineData("--profile","developer")][InlineData("--server-url","https://example.invalid")][InlineData("--version-policy","exact")]
    public async Task InvalidOrOperationalOptionsDoNotCreateConfiguration(string option,string value)
    {
        var path=Path.Combine(Path.GetTempPath(),"uedt-invalid-display-"+Guid.NewGuid().ToString("N"),"display.json");
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--project-id","demo",option,value,"--output",path]));
        Assert.False(File.Exists(path));Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
    [Fact]
    public async Task MissingProjectAndInconsistentVersionDoNotGenerateAFile()
    {
        var path=Path.Combine(Path.GetTempPath(),"uedt-missing-display-"+Guid.NewGuid().ToString("N")+".json");
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--output",path]));
        Assert.Equal(1,await Program.MainAsync(["sample-config","--mode","managed-client","--project-id","demo","--version","2.0.0","--output",path]));
        Assert.False(File.Exists(path));
    }
    [Fact]
    public async Task AtomicNoOverwriteDoesNotReplaceAConcurrentWritersConfiguration()
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-display-race-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"display.json");
        try
        {
            async Task<bool> Write(string id){try{await JsonFiles.WriteAsync(path,LauncherConfigurationTemplates.ManagedClient(id),overwrite:false);return true;}catch(IOException){return false;}}
            var results=await Task.WhenAll(Write("first"),Write("second"));
            Assert.Single(results,v=>v);
            var value=JsonSerializer.Deserialize<LauncherConfig>(await File.ReadAllTextAsync(path),JsonFiles.Options)!;
            Assert.Contains(value.ProjectId,new[]{"first","second"});
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [AvaloniaTheory][InlineData(LauncherEdition.General)][InlineData(LauncherEdition.Developer)]
    public void ConfigurationRequiredScreenIncludesAccessibleCommandHelp(LauncherEdition edition)
    {
        var model=new LauncherDashboardViewModel(edition){GeneralState=GeneralLauncherState.ConfigurationRequired};
        var window=new MainWindow(new(Path.Combine(Path.GetTempPath(),"missing-display.json"),LauncherConfigSource.Missing,false),model,new(),false);
        try
        {
            window.Show();
            var command=window.GetLogicalDescendants().OfType<TextBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="configuration-example");
            Assert.True(command.IsReadOnly);Assert.Contains("--mode managed-client",command.Text);
            Assert.Equal(edition==LauncherEdition.Developer,model.IsDeveloper);
        }
        finally{window.Close();}
    }
}
