namespace UeDtLauncher.Gui;

internal sealed record LauncherUiOperationContext(bool Managed, string ProjectId, string Environment,
    string Channel, string Platform, string VersionPolicy, string? RequestedVersion, ReleaseSelection? Selection)
{
    internal void Pin(LauncherConfig config)
    {
        if(config.IsManagedDeployment!=Managed || config.ProjectId!=ProjectId)
            throw new InvalidDataException("Launcher operation mode/project changed.");
        config.Environment=Environment;config.Channel=Channel;config.TargetPlatform=Platform;
        if(Selection is not null)
        {
            Selection.Validate();
            if(Selection.ProjectId!=ProjectId || Selection.Environment!=Environment || Selection.Channel!=Channel || Selection.Platform!=Platform)
                throw new InvalidDataException("Launcher release context is inconsistent.");
            config.VersionPolicy="exact";config.RequestedVersion=Selection.Version;
        }
        else {config.VersionPolicy=VersionPolicy;config.RequestedVersion=RequestedVersion;}
    }
    internal void Validate(LauncherUiOperationResult result)
    {
        if(result.Config.IsManagedDeployment!=Managed || result.Config.ProjectId!=ProjectId ||
            result.Config.Environment!=Environment || result.Config.Channel!=Channel || result.Config.TargetPlatform!=Platform ||
            (Selection is not null && (result.Selection!=Selection || result.Config.SelectedRelease!=Selection)))
            throw new InvalidDataException("Operation returned a different release or mode.");
    }
}

internal sealed record LauncherUiOperationResult(LauncherConfig Config, ReleaseSelection? Selection,
    ManagedProjectStatus Status, RuntimeObservation? Runtime,LauncherUiCompletion Completion=LauncherUiCompletion.Checked,
    LauncherUiPostCommitFailure? FollowUpFailure=null);
internal sealed record LauncherUiPostCommitFailure(string Stage,Exception Error);
internal enum LauncherUiCompletion{Checked,Completed,CommittedLaunchSkipped,CommittedRefreshRequired}
internal enum LauncherTroubleshootAction { OfferInstall, Complete, Repair }
internal enum LauncherRestoreDisposition { Applied,InspectionRequired,Unknown }
internal sealed record LauncherRestoreResult(LauncherRestoreDisposition Disposition,Exception? Error=null)
{
    internal static LauncherRestoreResult FromManaged(ManagedAgentResponse response,ReleaseSelection? expected)
    {
        if(!response.Success)
        {
            var code=response.ErrorCode??response.Status;
            if(response.Runtime is not null || code is "backup-preview-changed" or "no-backup" or "client-upgrade-required")response.ThrowIfFailed();
            return new(LauncherRestoreDisposition.Unknown,new AgentOperationException(code,response.CorrelationId,response.Message));
        }
        if(response.SelectedRelease!=expected)return new(LauncherRestoreDisposition.Unknown,new InvalidDataException("Restore selection was not confirmed."));
        return new(response.InstallationCommitted==true && response.ProjectStatus is null?LauncherRestoreDisposition.InspectionRequired:LauncherRestoreDisposition.Applied);
    }
}
internal sealed class LauncherUiCancelledException(OperationStatus operation):OperationCanceledException("Launcher operation cancelled")
{internal OperationStatus Operation {get;}=operation;}

internal interface ILauncherUiBackend
{
    Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default);
    Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default);
}
internal sealed class LauncherUiBackend : ILauncherUiBackend
{
    public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress,CancellationToken token=default)=>LauncherUiOperations.CheckAsync(context,config,progress,token);
    public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)=>LauncherUiOperations.ExecuteAsync(context,config,repair,launch,progress,logger,token);
}

internal static class LauncherUiOperations
{
    internal static bool CanResume(OperationStatus? operation, ReleaseSelection? selection) =>
        selection is not null && operation?.Selection == selection &&
        operation.Phase is "Cancelled" or "Interrupted" or "Failed" &&
        operation.ManifestSha256 is { Length: 64 } digest && digest.All(char.IsAsciiHexDigit);

    internal static OperationStatus? BindResume(LauncherUiOperationContext context,LauncherConfig config)
    {
        var resumed=config.UiResumeOperation;
        if(resumed is null)return null;
        if(!CanResume(resumed,context.Selection))throw new InvalidDataException("재개할 작업과 선택한 배포가 다릅니다.");
        config.RepairMode=resumed.Command=="repair";config.LaunchAfterUpdate=false;config.ExpectedResumeManifestSha256=resumed.ManifestSha256;
        return resumed;
    }
    internal static LauncherTroubleshootAction TroubleshootAction(ManagedProjectStatus status) =>
        !status.IsInstalled ? LauncherTroubleshootAction.OfferInstall : status.UpdateRequired ? LauncherTroubleshootAction.Repair : LauncherTroubleshootAction.Complete;

    internal static async Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,
        LauncherConfig config, Action<LauncherProgress> progress, CancellationToken token=default)
    {
        context.Pin(config);
        if(context.Managed)
        {
            var response=await new ManagedAgentClient().SendStreamingAsync("check",context.ProjectId,
                value=>progress(value.ToLauncherProgress()),timeout:TimeSpan.FromMinutes(5),cancellationToken:token,selection:context.Selection);
            response.ThrowIfFailed();
            BindManagedSelection(context,config,response);
            return new(config,config.SelectedRelease,response.ProjectStatus ?? throw new InvalidDataException("설치 상태를 확인할 수 없습니다."),response.Runtime);
        }
        return await Task.Run(async ()=>
        {
            using var http=SecureHttpClientFactory.Create(config);
            await CatalogResolver.ResolveAsync(config,http,(s,m,p)=>progress(new(s,m,p)),token);
            var document=await ManifestDownloader.DownloadAsync(config,http,cancellationToken:token);
            var status=await ManagedProjectStatusInspector.InspectAsync(config,document.Manifest,token);
            var result=new LauncherUiOperationResult(config,config.SelectedRelease,status,RuntimeStore.Observe(config));
            context.Validate(result);return result;
        },token);
    }

    internal static async Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,
        LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger,CancellationToken token=default)
    {
        context.Pin(config);config.RepairMode=repair;config.LaunchAfterUpdate=launch;
        var resumed=BindResume(context,config);
        if(resumed is not null)
        {
            repair=resumed.Command=="repair";config.RepairMode=repair;config.LaunchAfterUpdate=false;launch=false;
        }
        if(context.Managed)
        {
            var client=new ManagedAgentClient();
            var response=await client.SendCancellableAsync(resumed is null?repair?"repair":"update":"operation-resume",context.ProjectId,
                value=>progress(value.ToLauncherProgress()),selection:context.Selection,token:token,resumeOperationId:resumed?.Id);
            if(response.Operation is {Phase:"Cancelled"} cancelled)throw new LauncherUiCancelledException(cancelled);
            response.ThrowIfFailed();BindManagedSelection(context,config,response);
            RuntimeObservation? runtime=null;
            var committed=response.InstallationCommitted==true || response.Operation?.Phase=="Completed";
            var completed=token.IsCancellationRequested?LauncherUiCompletion.CommittedLaunchSkipped:LauncherUiCompletion.Completed;
            if(launch&&!token.IsCancellationRequested)
            {
                try{_=await ManagedAppLauncher.LaunchAsync(config,token);}
                catch(OperationCanceledException)when(committed){completed=LauncherUiCompletion.CommittedLaunchSkipped;}
                catch(Exception error)when(committed)
                {return CommittedFailure(config,response.ProjectStatus,"Launch",error);}
            }
            if(response.ProjectStatus is null && committed)
                return new(config,config.SelectedRelease,new(true,config.SelectedRelease?.Version,null,false,0,0,false),null,LauncherUiCompletion.CommittedRefreshRequired);
            try {var observed=await client.SendRuntimeAsync("runtime-inspect",config);observed.ThrowIfFailed();runtime=observed.Runtime;}
            catch(Exception)when(committed){return new(config,config.SelectedRelease,response.ProjectStatus!,null,LauncherUiCompletion.CommittedRefreshRequired);}
            return new(config,config.SelectedRelease,response.ProjectStatus ?? throw new InvalidDataException("설치 상태를 확인할 수 없습니다."),runtime,completed);
        }
        return await Task.Run(async ()=>
        {
            using var http=SecureHttpClientFactory.Create(config);
            await CatalogResolver.ResolveAsync(config,http,(s,m,p)=>progress(new(s,m,p)),token);
            var registry=new OperationRegistry(Path.Combine(config.StateRootDir,"operations"));
            using var operation=registry.Begin(Guid.NewGuid().ToString("N"),RuntimeIdentities.Current(),config.SelectedRelease,token,repair?"repair":"update");
            LauncherEngine? running=null;
            var completionStage="Inspection";
            using var engine=new LauncherEngine(config,value=>
            {
                if(value.Stage is "Integration" or "Launch" or "Complete")completionStage=value.Stage;
                if(running?.ManifestSha256 is { } digest)operation.Bind(config.SelectedRelease,digest);
                if(value.Stage=="Download")operation.Phase("Downloading");else if(value.Stage=="Apply")operation.Phase("Applying");
                progress(value);
            },logger,echoToConsole:false);
            running=engine;
            try{await engine.RunAsync(operation.Token);operation.Finish(true);}
            catch(OperationCanceledException)when(engine.InstallationCommitted){operation.Finish(true);}
            catch(OperationCanceledException){operation.Finish(false);throw new LauncherUiCancelledException(operation.Status);}
            catch(Exception error)when(engine.InstallationCommitted)
            {operation.Finish(true);return CommittedFailure(config,null,completionStage,error);}
            catch{operation.Finish(engine.InstallationCommitted,failed:true);throw;}
            LauncherManifest installed;
            try{installed=await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath);}
            catch(Exception)when(engine.InstallationCommitted){return new(config,config.SelectedRelease,new(true,config.SelectedRelease?.Version,null,false,0,0,false),null,LauncherUiCompletion.CommittedRefreshRequired);}
            var result=new LauncherUiOperationResult(config,config.SelectedRelease,
                new(true,installed.Version,installed.Version,false,0,0,BackupManager.List(config.BackupDir).Count>0),RuntimeStore.Observe(config),token.IsCancellationRequested?LauncherUiCompletion.CommittedLaunchSkipped:LauncherUiCompletion.Completed);
            context.Validate(result);return result;
        },token);
    }

    internal static LauncherUiOperationResult CommittedFailure(LauncherConfig config,ManagedProjectStatus? status,string stage,Exception error)=>
        new(config,config.SelectedRelease,status??new(true,config.SelectedRelease?.Version,null,false,0,0,false),null,
            LauncherUiCompletion.CommittedRefreshRequired,new(stage,error));

    private static void BindManagedSelection(LauncherUiOperationContext context,LauncherConfig config,ManagedAgentResponse response)
    {
        if(context.Selection is not null)
        {
            if(response.SelectedRelease!=context.Selection)throw new InvalidDataException("업데이트 서비스가 다른 배포를 선택했습니다.");
            ManagedClientContext.Bind(config,context.Selection);
        }
        response.ClientPresentation?.Validate(config.SelectedRelease);
        config.ManagedPresentation=response.ClientPresentation;
    }
}
