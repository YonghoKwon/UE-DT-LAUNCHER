using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class PostCommitBackendTests
{
    [Theory][InlineData("Launch")][InlineData("Integration")]
    public async Task RealPortableBackendPreservesInstalledFilesAndCompletedRecordAfterCompletionFailure(string stage)
    {
        var root=Path.Combine(Path.GetTempPath(),"uedt-post-commit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();var app=builder.Build();app.Urls.Add("http://127.0.0.1:0");
        var payload="post-commit-payload"u8.ToArray();LauncherManifest? manifest=null;
        app.MapGet("/manifest.json",()=>Results.Json(manifest,JsonFiles.Options));app.MapGet("/payload",()=>Results.Bytes(payload));
        try
        {
            await app.StartAsync();var origin=app.Urls.Single();
            manifest=new(){AppId="demo",Version="1.0.0",EntryPoint="game.exe",Files=[new(){Path="game.exe",Url=origin+"/payload",Size=payload.Length,Sha256=Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}]};
            var config=new LauncherConfig{ProjectId="demo",ManifestUrl=origin+"/manifest.json",InstallDir=Path.Combine(root,"app"),StateRootDir=Path.Combine(root,"state"),LogDir=Path.Combine(root,"logs"),
                WindowsIntegration=new(){CreateDesktopShortcut=stage=="Integration"}};
            LauncherPaths.ResolveInPlace(config,Path.Combine(root,"config.json"));
            var context=new LauncherUiOperationContext(false,"demo",config.Environment,config.Channel,config.TargetPlatform,"latest",null,null);
            var failure=new AgentOperationException("synthetic-completion-failed","synthetic-post-commit","synthetic completion failure");
            var result=await LauncherUiOperations.ExecuteAsync(context,config,false,stage=="Launch",progress=>{if(progress.Stage==stage)throw failure;},null);
            Assert.Equal(LauncherUiCompletion.CommittedRefreshRequired,result.Completion);Assert.Same(failure,result.FollowUpFailure!.Error);Assert.Equal(stage,result.FollowUpFailure.Stage);
            Assert.Equal(payload,await File.ReadAllBytesAsync(Path.Combine(config.InstallDir,"game.exe")));
            Assert.Equal("1.0.0",(await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath)).Version);
            var records=Directory.GetFiles(Path.Combine(config.StateRootDir,"operations"),"*.json");
            var record=await JsonFiles.ReadAsync<OperationStatus>(Assert.Single(records));Assert.Equal("Completed",record.Phase);
        }
        finally{await app.DisposeAsync();Directory.Delete(root,true);}
    }
}
