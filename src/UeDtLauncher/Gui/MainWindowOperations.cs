using Avalonia.Threading;
namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _operationCancellation;
    private OperationStatus? _resumeOperation;
    private long _uiProgressGeneration;
    private bool _readOnlyRecoveryFollowUp;

    private void StartCancellableUiOperation(CancellationTokenSource cancellation)
    {
        _operationCancellation=cancellation;
        _viewModel.GeneralState=GeneralLauncherState.Working;
        Build();
    }
    private void FinishUiOperation()
    {
        Interlocked.Increment(ref _uiProgressGeneration);
        _operationCancellation=null;
        _running=false;
        Build();
    }
    private void RecordCancelledOperation(OperationStatus? operation)
    {
        _resumeOperation=operation;
        _viewModel.GeneralState=GeneralLauncherState.RecoverableError;
        _presentation.Complete(CanResumeSelected()
            ? "작업 취소 완료 · 다시 확인하거나 다운로드를 이어받으세요."
            : "작업 취소 완료 · 다시 확인해 주세요.");
    }
    private bool CanResumeSelected()
    {try{return !_readOnlyRecoveryFollowUp && _viewModel.GeneralState!=GeneralLauncherState.RuntimeBlocked && LauncherUiOperations.CanResume(_resumeOperation,CurrentReleaseSelection());}catch{return false;}}
    private async Task ResumeUiOperationAsync()
    {
        if(_running || !CanResumeSelected())return;
        var previous=_resumeOperation!;_running=true;
        BeginOperation(LauncherUiOperation.Check);
        using var cancellation=new CancellationTokenSource();
        try
        {
            var context=CaptureUiOperation(previous.Selection);var config=await RunConfig(false,false);config.UiResumeOperation=previous;
            BeginOperation(LauncherUiOperation.Update);StartCancellableUiOperation(cancellation);
            var result=await _uiBackend.ExecuteAsync(context,config,previous.Command=="repair",false,CreateUiProgress(),_fileLogger,cancellation.Token);
            _resumeOperation=null;
            if(result.Completion==LauncherUiCompletion.CommittedRefreshRequired){CommittedUiRefreshRequired();return;}
            if(ApplyUiResult(context,result)){_presentation.Complete("다운로드 재개 및 검증 완료");Build();}
        }
        catch(LauncherUiCancelledException cancelled){RecordCancelledOperation(cancelled.Operation);}
        catch(OperationCanceledException){RecordCancelledOperation(previous);}
        catch(Exception error){MarkError(error,"작업 재개 실패");}
        finally{FinishUiOperation();}
    }
    private void CommittedUiRefreshRequired(string completedTitle="설치 완료")
    {
        _readOnlyRecoveryFollowUp=true;
        _presentation.Retry=CurrentContext(LauncherUiOperation.Check);
        MarkError(new AgentOperationException("status-refresh-required",Guid.NewGuid().ToString("N"),"설치는 완료됐습니다. 설치 상태를 다시 확인해 주세요."),completedTitle+" · 상태 재확인 필요",showDialog:false);
    }
    private void ClearResumeAfterMutation(LauncherUiOperationContext context)
    {
        if(_resumeOperation?.Selection==context.Selection)_resumeOperation=null;
    }
    private LauncherUiOperationContext DisplaySelectionContext()
    {
        var release=SelectedCatalogRelease();
        return new(_config.IsManagedDeployment,_selectedProject.ProjectId,_config.Environment,_config.Channel,CurrentPlatform,
            _config.VersionPolicy,_config.RequestedVersion,release is null?null:new(release.ProjectId,release.Environment,release.Channel,release.Platform,release.Version));
    }
    private void InvalidateDisplayedInstallation()
    {
        _selectedRuntimeConfig=null;_viewModel.ProjectStatus=null;_readOnlyRecoveryFollowUp=false;
        _viewModel.GeneralState=GeneralLauncherState.Checking;
        _installState="확인 필요";_installDetail="배포 선택이 변경되었습니다. 설치 상태를 확인하고 있습니다.";
        _presentation.Retry=null;
    }
    private void EndCancellableUiPhase()
    {
        Interlocked.Increment(ref _uiProgressGeneration);
        _operationCancellation=null;
        Build();
    }
    private void BeginReadOnlyFollowUp(string completedTitle)
    {
        EndCancellableUiPhase();
        _readOnlyRecoveryFollowUp=true;
        _presentation.BeginFollowUp(completedTitle+" · 설치 상태 확인 중");
        _presentation.Retry=CurrentContext(LauncherUiOperation.Check);
        Build();
    }
    private void RequestOperationCancellation()
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested) return;
        SetStatus("취소 요청 중 · 파일 작업과 안전한 복구가 끝날 때까지 기다려 주세요.");
        _operationCancellation.Cancel(); Build();
    }
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
    private Action<LauncherProgress> CreateUiProgress()
    {
        var generation=Interlocked.Increment(ref _uiProgressGeneration);
        return value=>
        {
            void Apply()
            {
                if(_running && Interlocked.Read(ref _uiProgressGeneration)==generation &&
                    _operationCancellation?.IsCancellationRequested!=true)EngineProgress(value);
            }
            if(Dispatcher.UIThread.CheckAccess())Apply();
            else Dispatcher.UIThread.Post(Apply);
        };
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
        {
            if(result.Runtime?.State==RuntimeState.Running)
            {_presentation.Retry=null;if(result.Config.ManagedIntegrationWarning is { } warning){_installDetail+=" "+warning;AppendLog(warning,true);}_presentation.Complete("프로그램 실행 중");Build();}
            else MarkError(new RuntimeBlockedException(LauncherDashboardViewModel.RequireRuntimeObservation(result.Runtime)),"실행 상태 확인",showDialog:false);
            return false;
        }
        if(result.Completion==LauncherUiCompletion.Checked)_readOnlyRecoveryFollowUp=false;
        return true;
    }
    private async Task RestoreUiPreviewAsync(LauncherUiOperationContext context,LauncherConfig config,RollbackPreview preview)
    {
        if(context!=CaptureUiOperation())throw new InvalidDataException("선택한 배포가 변경되었습니다.");
        EndCancellableUiPhase();
        var restoration=await _restorePreview(config,preview);
        if(restoration.Disposition==LauncherRestoreDisposition.Unknown)
        {
            ClearResumeAfterMutation(context);
            _readOnlyRecoveryFollowUp=true;_presentation.Retry=CurrentContext(LauncherUiOperation.Check);
            if(restoration.Error is not null)AppendLog(DiagnosticRedactor.Redact(restoration.Error.ToString()),true);
            MarkError(new AgentOperationException("recovery-outcome-unknown",(restoration.Error as AgentOperationException)?.CorrelationId??Guid.NewGuid().ToString("N"),"백업 복원 결과를 확인하지 못했습니다. 설치 상태를 먼저 확인해 주세요."),"복원 결과 확인 필요",showDialog:false);return;
        }
        ClearResumeAfterMutation(context);
        _readOnlyRecoveryFollowUp=true;
        _selectedRuntimeConfig=config;
        BeginReadOnlyFollowUp("백업 복원 완료");
        try
        {
            var result=await _uiBackend.CheckAsync(context,config,CreateUiProgress());
            if(!ApplyUiResult(context,result))return;
            _presentation.Complete("백업 복원 완료");Build();
        }
        catch(Exception ex)
        {
            _readOnlyRecoveryFollowUp=true;
            _presentation.Retry=CurrentContext(LauncherUiOperation.Check);
            MarkError(ex,"백업 복원 완료 · 상태 재확인 필요",showDialog:false);
        }
    }
}
