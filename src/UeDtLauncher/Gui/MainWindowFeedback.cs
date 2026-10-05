using Avalonia.Controls;
using Avalonia.Threading;
namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private LauncherRetryContext CurrentContext(LauncherUiOperation kind) => new(kind,_selectedProject.ProjectId,_config.Environment,_config.Channel,_config.VersionPolicy+":"+_config.RequestedVersion,
        UsesDistributionServer && SelectedCatalogRelease() is not null ? CurrentReleaseSelection() : null);
    private void BeginOperation(LauncherUiOperation kind)
    {
        _presentation.Begin(kind); _presentation.Retry=CurrentContext(kind); RefreshPresentation();
    }
    private async Task RetryCurrentAsync()
    {
        var context=_presentation.Retry;
        if(_running||context is null)return;
        var operation=LauncherOperationPresentation.RetryOperation(context,CurrentContext(context.Operation),_viewModel.GeneralState==GeneralLauncherState.RuntimeBlocked);
        switch(operation)
        {
            case LauncherUiOperation.Catalog: await RefreshSelectionStatusAsync();break;
            case LauncherUiOperation.Check: await RefreshInstallStatusAsync();break;
            case LauncherUiOperation.Update: await RunAsync(false,false,context.Selection);break;
            case LauncherUiOperation.Repair: await RunAsync(true,false,context.Selection);break;
            case LauncherUiOperation.Launch: await RunAsync(false,true,context.Selection);break;
            case LauncherUiOperation.Troubleshoot: await TroubleshootAsync();break;
            case LauncherUiOperation.Rollback: await RollbackLatestAsync();break; // New preview and confirmation every time.
        }
    }
    private async Task RefreshSelectionStatusAsync()
    {
        if(_running)return;
        if(UsesDistributionServer)
        {
            if(!await RefreshCatalog(false,suppressDialog:true) || !HasProject)return;
        }
        await RefreshInstallStatusAsync();
    }
    private async Task<RollbackPreview?> PreviewManagedRollbackAsync(LauncherConfig config)
    {
        var client=new ManagedAgentClient();
        var service=await client.SendAsync("status"); service.ThrowIfFailed();
        if(!service.AgentCapabilities.Contains(RollbackPreviewService.Capability))
            throw new RuntimeBlockedException(new(RuntimeState.Unknown,"client-upgrade-required","백업 정보를 확인하려면 업데이트 서비스를 갱신해 주세요."));
        var response=await client.SendStreamingAsync("rollback-preview",config.ProjectId,_=>{},selection:config.SelectedRelease ?? CurrentReleaseSelection());
        response.ThrowIfFailed();
        var preview=response.RollbackPreview;
        if(preview is null || !preview.CanRestore)
        {
            SetStatus(preview is null?"복원할 백업이 없습니다.":"백업의 복원 정보를 확인할 수 없습니다. 관리자에게 문의해 주세요.");
            return null;
        }
        return preview;
    }
    private async Task<bool> ConfirmPreviewAsync(RollbackPreview preview)
    {
        var info=new BackupInfo {PreviousVersion=preview.RestoresUninstalledState?"미설치 상태 (설치 파일 제거 가능)":preview.RestoreVersion,NewVersion=preview.CurrentVersion};
        return await ConfirmRollback(preview.BackupId+" · "+preview.CreatedAtUtc,info);
    }
    private async Task<LauncherRestoreResult> RestoreManagedPreviewAsync(LauncherConfig config,RollbackPreview preview)
    {
        try
        {
            var response=await new ManagedAgentClient().SendStreamingAsync("rollback",config.ProjectId,ReportManagedProgress,
                selection:config.SelectedRelease ?? CurrentReleaseSelection(),expectedBackup:preview);
            if(response.SelectedRelease!=config.SelectedRelease)return new(LauncherRestoreDisposition.Unknown,new InvalidDataException("Restore selection was not confirmed."));
            if(!response.Success && response.Runtime is null && response.Status is not ("backup-preview-changed" or "no-backup" or "client-upgrade-required"))
                return new(LauncherRestoreDisposition.Unknown,new AgentOperationException(response.ErrorCode??response.Status,response.CorrelationId,response.Message));
            response.ThrowIfFailed();
            return new(response.InstallationCommitted==true && response.ProjectStatus is null?LauncherRestoreDisposition.InspectionRequired:LauncherRestoreDisposition.Applied);
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException){return new(LauncherRestoreDisposition.Unknown,ex);}
    }
    private async Task ExecuteStatusActionAsync()
    {
        if(_readOnlyRecoveryFollowUp){await RefreshInstallStatusAsync();return;}
        if(!IsDeveloper&&_viewModel.GeneralState==GeneralLauncherState.RecoverableError)await TroubleshootAsync();
        else await RefreshSelectionStatusAsync();
    }
}
