using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private async Task ExecutePrimaryActionAsync()
    {
        if(_running)return;
        if(_readOnlyRecoveryFollowUp){await RefreshInstallStatusAsync();return;}
        if(_configurationError is not null)
        {
            _agentStatusTimer.Stop();_selectedRuntimeConfig=null;_viewModel.ProjectStatus=null;_catalog=new();
            _presentation.ErrorCode=null;_presentation.SupportId=null;_presentation.Retry=null;
            LoadConfig();Build();
            if(_configurationError is not null)return;
            if(_allowAutomaticChecks && _config.IsManagedDeployment)_agentStatusTimer.Start();
            _startupInitialized=false;await InitializeStartupAsync();return;
        }
        switch (_viewModel.PrimaryAction)
        {
            case PrimaryActionKind.InstallAndLaunch:
            case PrimaryActionKind.UpdateAndLaunch:
            case PrimaryActionKind.Launch:
                await RunAsync(false, true);
                break;
            case PrimaryActionKind.RetryCheck:
                await RefreshAgentStatusAsync();
                if(await RefreshCatalog(false,suppressDialog:true) && HasProject)await RefreshInstallStatusAsync();
                break;
        }
    }

    private async Task<bool> RefreshCatalog(bool rebuild, bool suppressDialog = false)
    {
        var requested=DisplaySelectionContext();
        var ownsBusy = !_running;
        if (ownsBusy) { BeginOperation(LauncherUiOperation.Catalog); _running=true; SetBusy(true); }
        try
        {
            _catalogState = "카탈로그 확인 중...";
            if (rebuild) Build();
            var catalogConfig = await RunConfig(false, false);
            var catalog=await _catalogLoader(catalogConfig, CurrentPlatform, CancellationToken.None);
            if(requested!=DisplaySelectionContext())throw new InvalidDataException("배포 선택이 변경되었습니다. 상태를 다시 확인해 주세요.");
            _catalog=catalog;
            _presentation.ErrorCode=null;_presentation.SupportId=null;
            _catalogState = _catalog.Status;
            MergeCatalogProjects();
            if(HasProject && requested!=DisplaySelectionContext())InvalidateDisplayedInstallation();
            UpdateReleaseNotes();
            return true;
        }
        catch (Exception ex)
        {
            _catalogState = "카탈로그 오류";
            MarkError(ex,"카탈로그 확인 실패",showDialog: !suppressDialog);
            return false;
        }
        finally { if (ownsBusy) { _running=false; SetBusy(false); } if (rebuild || !HasProject) Build(); }
    }

    private void MergeCatalogProjects()
    {
        if (UsesDistributionServer)
            _config.Projects.RemoveAll(p => !_catalog.Projects.Any(c => c.ProjectId.Equals(p.ProjectId, StringComparison.OrdinalIgnoreCase)));
        foreach (var cp in _catalog.Projects)
        {
            var configured = _config.Projects.FirstOrDefault(p => string.Equals(p.ProjectId, cp.ProjectId, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
            {
                if (UsesDistributionServer) { configured.DisplayName = cp.DisplayName; configured.HeroPath = cp.HeroPath; configured.ThumbnailPath = cp.ThumbnailPath; }
                continue;
            }
            _config.Projects.Add(new ProjectUiConfig { ProjectId = cp.ProjectId, DisplayName = cp.DisplayName, Description = "카탈로그에서 발견된 프로젝트입니다.", Status = $"릴리스 {cp.ReleaseCount}개", InstallPath = $"apps/{cp.ProjectId}", Technology = CurrentPlatform, SortOrder = 100, VisibleToProfiles = new List<string> { _config.ClientProfile } });
            _config.Projects[^1].HeroPath = cp.HeroPath; _config.Projects[^1].ThumbnailPath = cp.ThumbnailPath;
        }
        if (_config.Projects.Count > 0) SelectProject();
        else
        {
            _selectedRuntimeConfig=null;_viewModel.ProjectStatus=null;_resumeOperation=null;
            _selectedProject = new ProjectUiConfig { ProjectId = "unavailable", DisplayName = "사용 가능한 프로젝트가 없습니다" };
            _viewModel.GeneralState = GeneralLauncherState.RecoverableError;
            _presentation.Title = "허용된 배포가 없습니다"; _presentation.Percent = null;
            _installState = "허용된 배포 없음";
            _installDetail = "이 PC에 허용된 배포가 없습니다. 관리자에게 문의해 주세요.";
            _presentation.Complete("이 PC에 허용된 배포가 없습니다. 관리자에게 문의해 주세요.");
        }
    }

    private IEnumerable<CatalogReleaseOption> MatchingReleases() => _catalog.Releases.Where(r => r.ProjectId.Equals(_selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase) && r.Platform.Equals(CurrentPlatform, StringComparison.OrdinalIgnoreCase) && r.Environment.Equals(_config.Environment, StringComparison.OrdinalIgnoreCase) && r.Channel.Equals(_config.Channel, StringComparison.OrdinalIgnoreCase));

    private void UpdateReleaseNotes()
    {
        var release = SelectedCatalogRelease();
        _releaseNotes = string.IsNullOrWhiteSpace(release?.Notes) ? _selectedProject.Description ?? "릴리스 노트가 없습니다." : release.Notes!;
    }

    private async Task<LauncherConfig> RunConfig(bool repair, bool launch)
    {
        var runtimePath=ConfigPath;
        var c = _config.IsManagedDeployment ? ManagedClientContext.Create(_config) : await _runtimeConfigLoader(runtimePath);
        c.ProjectId = _selectedProject.ProjectId; c.Environment = _config.Environment; c.Channel = _config.Channel; c.TargetPlatform = CurrentPlatform; c.VersionPolicy = _config.VersionPolicy; c.RequestedVersion = _config.RequestedVersion; c.RepairMode = repair; c.LaunchAfterUpdate = launch;
        c.ClientProfile = _viewModel.EffectiveProfile;
        c.SelfUpdate = null;
        if(!c.IsManagedDeployment)LauncherPaths.ResolveInPlace(c, runtimePath, UsesDistributionServer ? null : _selectedProject.InstallPath);
        return c;
    }

    private ReleaseSelection? CurrentReleaseSelection()
    {
        if (!UsesDistributionServer) return null;
        var release = SelectedCatalogRelease();
        if (release is null && _config.VersionPolicy != "exact" && _catalog.SelectionPolicy == CatalogRecommendation.ExplicitPolicy) throw new NoPromotedReleaseException();
        if (release is null) throw new InvalidOperationException("허용된 배포 버전을 먼저 선택해 주세요.");
        return new(release.ProjectId, release.Environment, release.Channel, release.Platform, release.Version);
    }

    private CatalogReleaseOption? SelectedCatalogRelease() => _config.VersionPolicy == "exact"
        ? MatchingReleases().FirstOrDefault(r => r.Version.Equals(_config.RequestedVersion, StringComparison.OrdinalIgnoreCase))
        : CatalogRecommendation.Find(_catalog.SelectionPolicy, MatchingReleases(), r => r.IsLatest);

    private void AwaitPromotion()
    {
        _selectedRuntimeConfig = null; _viewModel.ProjectStatus = null;
        _viewModel.GeneralState = GeneralLauncherState.AwaitingPromotion;
        _installState = "실행 버전 지정 대기"; _installDetail = "관리자가 실행 버전을 지정하지 않았습니다.";
        _presentation.ErrorCode = null; _presentation.SupportId = null; _presentation.Retry = null;
        _presentation.Complete(_installDetail); Build();
    }

    private async Task RunAsync(bool repair, bool launch, ReleaseSelection? expectedSelection = null)
    {
        if(_running)return;
        if(_readOnlyRecoveryFollowUp){await RefreshInstallStatusAsync();return;}
        _running=true;SetBusy(true);
        using var operationCancellation = new CancellationTokenSource();
        try
        {
            var context=CaptureUiOperation(expectedSelection);
            if(IsDeveloper && launch && !await ConfirmDevLaunch(context))return;
            BeginOperation(launch?LauncherUiOperation.Launch:repair?LauncherUiOperation.Repair:LauncherUiOperation.Update);
            Progress(0);ResetSpeedTracking();StartCancellableUiOperation(operationCancellation);
            var config=await RunConfig(repair,launch);
            var result=await _uiBackend.ExecuteAsync(context,config,repair,launch,CreateUiProgress(),_fileLogger,operationCancellation.Token);
            _resumeOperation=null;
            if(result.Completion==LauncherUiCompletion.CommittedRefreshRequired){CommittedUiRefreshRequired(failure:result.FollowUpFailure);return;}
            _presentation.Complete(result.Completion==LauncherUiCompletion.CommittedLaunchSkipped?"설치 완료 · 프로그램 실행은 생략했습니다.":launch?"실행 준비 완료":repair?"파일 복구 완료":"업데이트 확인 완료");
            if(ApplyUiResult(context,result))Build();
        }
        catch(LauncherUiCancelledException cancelled){RecordCancelledOperation(cancelled.Operation);}
        catch(OperationCanceledException){RecordCancelledOperation(null);}
        catch(Exception ex){MarkError(ex);}
        finally{FinishUiOperation();}
    }

    private async Task RefreshInstallStatusAsync(bool suppressDialog = false)
    {
        if(_running)return;
        if(!HasProject){await RefreshCatalog(true,suppressDialog:true);return;}
        if (_catalog.SelectionPolicy == CatalogRecommendation.ExplicitPolicy && _config.VersionPolicy != "exact" && SelectedCatalogRelease() is null)
        { AwaitPromotion(); return; }
        _running=true;SetBusy(true);
        try
        {
            var context=CaptureUiOperation();
            BeginOperation(LauncherUiOperation.Check);_viewModel.GeneralState=GeneralLauncherState.Checking;Progress(0);
            var result=await _uiBackend.CheckAsync(context,await RunConfig(false,false),CreateUiProgress());
            if(ApplyUiResult(context,result)){_presentation.Complete(_installState);Build();}
        }
        catch(Exception ex){MarkError(ex,"상태 확인 실패",showDialog:!suppressDialog);}
        finally{FinishUiOperation();}
    }

    private async Task TroubleshootAsync()
    {
        if(_running)return;
        if(_readOnlyRecoveryFollowUp){await RefreshInstallStatusAsync();return;}
        _running=true;SetBusy(true);
        BeginOperation(LauncherUiOperation.Troubleshoot);
        LauncherUiOperationContext? context=null;
        LauncherConfig? config=null;
        LauncherUiOperationResult? checkedResult=null;
        var repairAttempted=false;
        var repairCommitted=false;
        using var cancellation=new CancellationTokenSource();
        try
        {
            if(UsesDistributionServer && _catalog.Releases.Count==0)
            {
                if(!await RefreshCatalog(false,suppressDialog:true) || !HasProject)return;
            }
            context=CaptureUiOperation();_presentation.Retry=CurrentContext(LauncherUiOperation.Troubleshoot);config=await RunConfig(false,false);
            BeginOperation(LauncherUiOperation.Troubleshoot);Progress(0);StartCancellableUiOperation(cancellation);
            checkedResult=await _uiBackend.CheckAsync(context,config,CreateUiProgress(),cancellation.Token);
            ValidateUiResult(context,checkedResult);_viewModel.RequireRuntimeQuiescent(checkedResult.Runtime);
            var action=LauncherUiOperations.TroubleshootAction(checkedResult.Status);
            if(action==LauncherTroubleshootAction.Repair)
            {
                repairAttempted=true;
                var repaired=await _uiBackend.ExecuteAsync(context,config,true,false,CreateUiProgress(),_fileLogger,cancellation.Token);
                ValidateUiResult(context,repaired);
                repairCommitted=true;ClearResumeAfterMutation(context);
                if(repaired.Completion==LauncherUiCompletion.CommittedRefreshRequired || cancellation.IsCancellationRequested){CommittedUiRefreshRequired("파일 복구 완료",repaired.FollowUpFailure);return;}
                BeginReadOnlyFollowUp("파일 복구 완료");
                _viewModel.RequireRuntimeQuiescent(repaired.Runtime);
                checkedResult=await _uiBackend.CheckAsync(context,config,CreateUiProgress());
                if(checkedResult.Status.UpdateRequired)throw new InvalidDataException("파일 복구 후 검증을 완료하지 못했습니다.");
            }
            if(ApplyUiResult(context,checkedResult))
            {
                _presentation.Complete(action==LauncherTroubleshootAction.OfferInstall?"확인 완료 · 설치/업데이트가 필요합니다.":repairAttempted?"파일 복구 완료":"설치 상태 점검 완료");
                Build();
            }
        }
        catch(Exception ex)
        {
            if(repairCommitted)
            {
                _readOnlyRecoveryFollowUp=true;
                _presentation.Retry=CurrentContext(LauncherUiOperation.Check);
                MarkError(ex,"파일 복구 완료 · 상태 재확인 필요",showDialog:false);
                return;
            }
            var offer=LauncherDashboardViewModel.CanOfferRecoveryRollback(repairAttempted,checkedResult?.Status.HasBackup==true,ex);
            if(ex is LauncherUiCancelledException cancelled){RecordCancelledOperation(cancelled.Operation);return;}
            if(ex is OperationCanceledException){RecordCancelledOperation(null);return;}
            if(offer && context is not null && config is not null)
            {
                EndCancellableUiPhase();
                try
                {
                    var preview=context.Managed?await _managedRollbackPreview(config):await Task.Run(()=>RollbackPreviewService.ReadAsync(config));
                    if(preview is {CanRestore:true} && await ConfirmPreviewAsync(preview))await RestoreUiPreviewAsync(context,config,preview);
                    else MarkError(ex,"문제 해결 실패");
                }
                catch(Exception rollbackError){MarkError(rollbackError,"문제 해결 실패");}
            }
            else if(ex is OperationCanceledException)_presentation.Complete("작업 취소 완료 · 설치 상태를 다시 확인해 주세요.");
            else MarkError(ex,"문제 해결 실패");
        }
        finally{FinishUiOperation();}
    }

    private async Task RollbackLatestAsync()
    {
        if (_running) return;
        if(_readOnlyRecoveryFollowUp){await RefreshInstallStatusAsync();return;}
        var previousPresentation=_presentation.Capture();
        var previousState=_viewModel.GeneralState;
        _running=true;SetBusy(true);
        try
        {
            var context=CaptureUiOperation();
            _presentation.Retry=CurrentContext(LauncherUiOperation.Rollback);
            var config=await RunConfig(false,false);context.Pin(config);
            if(context.Selection is not null)
            {
                if(context.Managed)ManagedClientContext.Bind(config,context.Selection);
                else VersionedReleasePaths.Bind(config,context.Selection);
            }
            var preview=config.IsManagedDeployment
                ? await _managedRollbackPreview(config)
                : await Task.Run(()=>RollbackPreviewService.ReadAsync(config));
            if(preview is null || !preview.CanRestore)
            {_presentation.Retry=CurrentContext(LauncherUiOperation.Check);SetStatus("복원할 수 있는 백업 정보를 확인해 주세요.");return;}
            if(!await ConfirmPreviewAsync(preview))
            {_presentation.Restore(previousPresentation);_viewModel.GeneralState=previousState;return;}
            BeginOperation(LauncherUiOperation.Rollback);
            await RestoreUiPreviewAsync(context,config,preview);
        }
        catch(Exception ex){MarkError(ex,"백업 복원 실패");}
        finally{FinishUiOperation();}
    }

    private Task<bool> ConfirmRollback(string backupName, BackupInfo? info)
    {
        return ShowConfirmationAsync("백업 복원 확인", new StackPanel {Spacing=12,Children={
            Txt("선택한 설치의 백업 시점으로 복원합니다.",16,true),
            KeyValue("백업",backupName),KeyValue("현재 버전",info?.NewVersion??"기록 없음"),
            KeyValue("복원 상태",info?.PreviousVersion??"기록 없음"),
            Muted("다른 버전의 설치 경로로 전환하는 작업이 아닙니다. 설치 전 상태 백업은 선택 설치 파일을 제거할 수 있습니다.",14)}}, "백업 복원");
    }
    private Task<bool> ConfirmDevLaunch(LauncherUiOperationContext context)
    {
        return ShowConfirmationAsync("개발자 배포 실행 확인",new StackPanel {Spacing=12,Children={
            KeyValue("프로젝트",context.ProjectId),KeyValue("환경",context.Environment),KeyValue("채널",context.Channel),
            KeyValue("버전",context.Selection?.Version ?? context.RequestedVersion ?? "직접 Manifest"),KeyValue("OS",context.Platform)}}, "선택 버전 실행");
    }


}
