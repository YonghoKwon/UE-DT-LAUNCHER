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
            case LauncherUiOperation.ClearStaging: await RunMaintenanceAsync(false);break;
            case LauncherUiOperation.PruneBackups: await RunMaintenanceAsync(true);break;
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
        var selection=config.SelectedRelease ?? CurrentReleaseSelection();
        var response=await client.SendStreamingAsync("rollback-preview",config.ProjectId,_=>{},selection:selection);
        var preview=ValidateManagedRollbackPreview(response,selection);
        if(preview is null || !preview.CanRestore)
        {
            SetStatus(preview is null?"복원할 백업이 없습니다.":"백업의 복원 정보를 확인할 수 없습니다. 관리자에게 문의해 주세요.");
            return null;
        }
        return preview;
    }
    internal static RollbackPreview? ValidateManagedRollbackPreview(ManagedAgentResponse response,ReleaseSelection? expected)
    {
        response.ThrowIfFailed();
        if(expected is null || response.SelectedRelease!=expected)
            throw new InvalidDataException("업데이트 서비스가 요청한 백업의 배포 선택을 확인하지 못했습니다. 상태를 다시 확인해 주세요.");
        return response.RollbackPreview;
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
            return LauncherRestoreResult.FromManaged(response,config.SelectedRelease);
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
