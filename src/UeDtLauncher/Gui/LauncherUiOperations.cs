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
    ManagedProjectStatus Status, RuntimeObservation? Runtime);
internal enum LauncherTroubleshootAction { OfferInstall, Complete, Repair }

internal interface ILauncherUiBackend
{
    Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress);
    Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger);
}
internal sealed class LauncherUiBackend : ILauncherUiBackend
{
    internal Task<LauncherUiOperationResult> ExecuteCancellableAsync(LauncherUiOperationContext context, LauncherConfig config, bool repair, bool launch,
        Action<LauncherProgress> progress, FileLogger? logger, CancellationToken token) => LauncherUiOperations.ExecuteAsync(context, config, repair, launch, progress, logger, token);
    public Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context,LauncherConfig config,Action<LauncherProgress> progress)=>LauncherUiOperations.CheckAsync(context,config,progress);
    public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context,LauncherConfig config,bool repair,bool launch,Action<LauncherProgress> progress,FileLogger? logger)=>LauncherUiOperations.ExecuteAsync(context,config,repair,launch,progress,logger);
}

internal static class LauncherUiOperations
{
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
        if(context.Managed)
        {
            var client=new ManagedAgentClient();
            var operationId=Guid.NewGuid().ToString("N");
            Task<ManagedAgentResponse>? cancellation=null;
            using var registration=token.Register(()=>cancellation=client.SendOperationAsync("cancel",operationId));
            var response=await client.SendStreamingAsync(repair?"repair":"update",context.ProjectId,
                value=>progress(value.ToLauncherProgress()),selection:context.Selection,operationId:operationId);
            if(cancellation is not null)await cancellation;
            response.ThrowIfFailed();BindManagedSelection(context,config,response);
            RuntimeObservation? runtime=null;
            if(launch&&!token.IsCancellationRequested) _=await ManagedAppLauncher.LaunchAsync(config,token);
            try {var observed=await client.SendRuntimeAsync("runtime-inspect",config,cancellationToken:token);observed.ThrowIfFailed();runtime=observed.Runtime;}
            catch(Exception ex) when(ex is not OperationCanceledException){runtime=LauncherDashboardViewModel.RequireRuntimeObservation(null);}
            return new(config,config.SelectedRelease,response.ProjectStatus ?? throw new InvalidDataException("설치 상태를 확인할 수 없습니다."),runtime);
        }
        return await Task.Run(async ()=>
        {
            using var http=SecureHttpClientFactory.Create(config);
            await CatalogResolver.ResolveAsync(config,http,(s,m,p)=>progress(new(s,m,p)),token);
            var registry=new OperationRegistry(Path.Combine(config.StateRootDir,"operations"));
            using var operation=registry.Begin(Guid.NewGuid().ToString("N"),RuntimeIdentities.Current(),config.SelectedRelease,token,repair?"repair":"update");
            LauncherEngine? running=null;
            using var engine=new LauncherEngine(config,value=>
            {
                if(running?.ManifestSha256 is { } digest)operation.Bind(config.SelectedRelease,digest);
                if(value.Stage=="Download")operation.Phase("Downloading");else if(value.Stage=="Apply")operation.Phase("Applying");
                progress(value);
            },logger,echoToConsole:false);
            running=engine;
            try{await engine.RunAsync(operation.Token);operation.Finish(true);}
            catch(OperationCanceledException){operation.Finish(engine.InstallationCommitted);throw;}
            catch{operation.Finish(engine.InstallationCommitted,failed:true);throw;}
            var installed=await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath,token);
            var result=new LauncherUiOperationResult(config,config.SelectedRelease,
                new(true,installed.Version,installed.Version,false,0,0,BackupManager.List(config.BackupDir).Count>0),RuntimeStore.Observe(config));
            context.Validate(result);return result;
        },token);
    }

    private static void BindManagedSelection(LauncherUiOperationContext context,LauncherConfig config,ManagedAgentResponse response)
    {
        if(context.Selection is null)return;
        if(response.SelectedRelease!=context.Selection)throw new InvalidDataException("업데이트 서비스가 다른 배포를 선택했습니다.");
        VersionedReleasePaths.Bind(config,context.Selection);
    }
}
