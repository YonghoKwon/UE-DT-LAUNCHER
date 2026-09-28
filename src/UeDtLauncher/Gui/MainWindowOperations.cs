using Avalonia.Threading;
namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private LauncherUiOperationContext CaptureUiOperation(ReleaseSelection? expected=null)
    {
        var selection=CurrentReleaseSelection();
        if(expected is not null && expected!=selection)throw new InvalidDataException("선택한 배포가 변경되었습니다. 다시 확인해 주세요.");
        return new(_config.IsManagedDeployment,_selectedProject.ProjectId,_config.Environment,_config.Channel,CurrentPlatform,
            _config.VersionPolicy,_config.RequestedVersion,selection);
    }
    private void ValidateUiResult(LauncherUiOperationContext context,LauncherUiOperationResult result)
    {
        if(context!=CaptureUiOperation())throw new InvalidDataException("선택이 변경된 작업 결과를 표시할 수 없습니다.");
        context.Validate(result);
    }
    private void PostUiProgress(LauncherProgress value)
    {
        if(Dispatcher.UIThread.CheckAccess())EngineProgress(value);
        else Dispatcher.UIThread.Post(()=>EngineProgress(value));
    }
    private bool ApplyUiResult(LauncherUiOperationContext context,LauncherUiOperationResult result)
    {
        ValidateUiResult(context,result);
        _selectedRuntimeConfig=result.Config;_viewModel.ApplyProjectStatus(result.Status);
        _installState=_viewModel.GeneralState switch
        {GeneralLauncherState.NotInstalled=>"설치 필요",GeneralLauncherState.UpdateAvailable=>"업데이트 가능",_=>"최신 상태"};
        _installDetail=result.Status switch
        {
            {IsInstalled:false,PreviousInstallation:{ } previous} when !IsDeveloper=>$"기존 설치 {previous.Release.Version}을 보존하고 새 버전을 설치합니다.",
            {IsInstalled:false}=>"프로젝트를 처음 설치할 수 있습니다.",
            {UpdateRequired:true}=>$"확인이 필요한 파일: 누락 {result.Status.MissingFiles}개, 변경 {result.Status.ChangedFiles}개",
            _=>"설치된 파일이 선택한 배포와 일치합니다."
        };
        if(!_viewModel.ApplyRuntimeObservation(result.Runtime))
        {MarkError(new RuntimeBlockedException(LauncherDashboardViewModel.RequireRuntimeObservation(result.Runtime)),"실행 상태 확인",showDialog:false);return false;}
        return true;
    }
    private async Task RestoreUiPreviewAsync(LauncherUiOperationContext context,LauncherConfig config,RollbackPreview preview)
    {
        if(context!=CaptureUiOperation())throw new InvalidDataException("선택한 배포가 변경되었습니다.");
        if(context.Managed)await RestoreManagedPreviewAsync(config,preview);
        else await Task.Run(()=>RollbackPreviewService.RestoreExpectedAsync(config,preview.BackupId,preview.MetadataFingerprint));
        _selectedRuntimeConfig=config;
        BeginOperation(LauncherUiOperation.Check);
        SetStatus("백업 복원 완료 · 설치 상태 확인 중");
        try
        {
            var result=await _uiBackend.CheckAsync(context,config,PostUiProgress);
            if(!ApplyUiResult(context,result))return;
            _presentation.Complete("백업 복원 완료");Build();
        }
        catch(Exception ex)
        {
            _presentation.Retry=CurrentContext(LauncherUiOperation.Check);
            MarkError(ex,"백업 복원 완료 · 상태 재확인 필요",showDialog:false);
        }
    }
}
