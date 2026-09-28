using System.Net;
using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;
public class EnterpriseFeedbackTests
{
    [Theory][InlineData(LauncherUiOperation.Check)][InlineData(LauncherUiOperation.Catalog)]
    public void ReadRetryCannotBecomeInstallOrLaunch(LauncherUiOperation operation)
    {
        var context=new LauncherRetryContext(operation,"a","prod","stable","exact:1");
        Assert.Equal(operation,LauncherOperationPresentation.RetryOperation(context,context));
        Assert.Equal(LauncherUiOperation.Check,LauncherOperationPresentation.RetryOperation(context,context with {ProjectId="b"}));
        Assert.Equal(LauncherUiOperation.None,LauncherOperationPresentation.RetryOperation(null,context));
    }
    [Theory][InlineData(HttpStatusCode.Unauthorized,"authentication-failed")][InlineData(HttpStatusCode.Forbidden,"access-denied")]
    public void TypedErrorsPreserveCodeAndSupportIdWithoutLeaking(HttpStatusCode status,string code)
    {
        Assert.Equal(code,LauncherFailure.Code(new HttpRequestException("secret",null,status)));
        var response=new ManagedAgentResponse {Success=false,ErrorCode=code,CorrelationId="support-123",Message="Authorization: Bearer secret"};
        var exception=Assert.Throws<AgentOperationException>(()=>response.ThrowIfFailed());
        var error=LauncherUiError.From(exception);Assert.Equal(code,error.Code);Assert.Equal("support-123",error.SupportId);
        Assert.DoesNotContain("secret",error.Message);Assert.DoesNotContain("secret",error.Detail);
    }
    [Fact]
    public void RuntimeBlockedRetryCanOnlyCheck()
    {
        foreach(var operation in new[]{LauncherUiOperation.Launch,LauncherUiOperation.Update,LauncherUiOperation.Repair,LauncherUiOperation.Rollback})
        {
            var context=new LauncherRetryContext(operation,"demo","prod","stable","1");
            Assert.Equal(LauncherUiOperation.Check,LauncherOperationPresentation.RetryOperation(context,context,true));
        }
    }
    [Fact]
    public void GeneralRuntimeMessageDoesNotExposeInternalObservation()
    {
        var error=LauncherUiError.From(new RuntimeBlockedException(new(RuntimeState.Unknown,"unknown","internal C:\\protected\\runtime.json")));
        Assert.DoesNotContain("internal",error.Message);Assert.DoesNotContain("protected",error.Message);
        Assert.Contains("점검",error.Message);
    }
    [Fact]
    public async Task BackupPreviewIsReadOnlyAndChangedPreviewCannotRestore()
    {
        var root=Path.Combine(Path.GetTempPath(),"rollback-preview-"+Guid.NewGuid().ToString("N"));
        var c=new LauncherConfig {ProjectId="demo",InstallDir=Path.Combine(root,"app"),BackupDir=Path.Combine(root,"backup"),InstallStatePath=Path.Combine(root,"state","install.json"),InstalledManifestPath=Path.Combine(root,"state","manifest.json")};
        try
        {
            Assert.Null(await RollbackPreviewService.ReadAsync(c));Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(c.InstallDir);RuntimeTestSupport.Stopped(c);
            var payload=Path.Combine(c.InstallDir,"data");File.WriteAllText(payload,"good");
            await JsonFiles.WriteAsync(c.InstalledManifestPath,new LauncherManifest {AppId="demo",Version="2",Files=[new(){Path="data",Size=4,Sha256=await Hashing.Sha256FileAsync(payload)}]});
            var backup=BackupManager.CreateBackupRoot(c.BackupDir);File.Copy(payload,Path.Combine(backup,"data"));
            await BackupManager.WriteBackupMetadataAsync(backup,new BackupInfo {PreviousVersion="2",NewVersion="2"},c.InstalledManifestPath,c.InstallStatePath);
            var preview=(await RollbackPreviewService.ReadAsync(c))!;Assert.True(preview.CanRestore);Assert.Equal("2",preview.RestoreVersion);
            File.WriteAllText(payload,"damaged");
            await RollbackPreviewService.RestoreExpectedAsync(c,preview.BackupId,preview.MetadataFingerprint);
            Assert.Equal("good",File.ReadAllText(payload));
            File.WriteAllText(payload,"damaged");
            await File.AppendAllTextAsync(Path.Combine(backup,BackupManager.MetaDirName,"backup-info.json")," ");
            await Assert.ThrowsAsync<RollbackPreviewChangedException>(()=>RollbackPreviewService.RestoreExpectedAsync(c,preview.BackupId,preview.MetadataFingerprint));
            Assert.Equal("damaged",File.ReadAllText(payload));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
