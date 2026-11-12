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
    private void EngineProgress(LauncherProgress p)
    {
        if (p.Stage == "DownloadProgress" && p.TotalBytes is > 0 && p.BytesDownloaded is not null)
        {
            // Logical file progress includes reused/resumed bytes; network speed does not.
            UpdateSpeed(p.Performance?.NetworkBytes ?? p.BytesDownloaded.Value);
            var overall = Math.Clamp(p.BytesDownloaded.Value / (double)p.TotalBytes.Value * 100, 0, 100);
            var speedText = _speedBytesPerSecond > 0 ? $"{FormatBytes((long)_speedBytesPerSecond)}/s" : "측정 중";
            if (_statusText is not null) SetStatus($"다운로드 중 · 파일 {p.FileIndex}/{p.FileCount} · {speedText} · 전체 {overall:0}%");
            Progress(overall, measured: true);
            return; // byte-level ticks update the status line only, not the log
        }

        UiProgress(p.Stage, p.Message, p.Percent);
    }

    private void ResetSpeedTracking() { _speedLastBytes = 0; _speedLastTickMs = 0; _speedBytesPerSecond = 0; }

    private void UpdateSpeed(long bytesDownloaded)
    {
        var now = Environment.TickCount64;
        if (_speedLastTickMs == 0 || bytesDownloaded < _speedLastBytes)
        {
            _speedLastTickMs = now; _speedLastBytes = bytesDownloaded;
            return;
        }

        var elapsedMs = now - _speedLastTickMs;
        if (elapsedMs < 400) return; // smooth the reading
        var instant = (bytesDownloaded - _speedLastBytes) * 1000.0 / elapsedMs;
        _speedBytesPerSecond = _speedBytesPerSecond <= 0 ? instant : _speedBytesPerSecond * 0.6 + instant * 0.4;
        _speedLastTickMs = now; _speedLastBytes = bytesDownloaded;
    }

    private Button Track(Button button) { _actionButtons.Add(button); return button; }

    private void SetBusy(bool busy)
    {
        foreach (var button in _actionButtons)
        {
            var id = AutomationProperties.GetAutomationId(button);
            var mutation = id is "primary-action" or "update" or "repair" or "rollback" or "resume-operation" or "cache-clear" or "backup-cleanup";
            button.IsEnabled = !busy && (!mutation || (HasProject && _viewModel.GeneralState != GeneralLauncherState.RuntimeBlocked && (!_readOnlyRecoveryFollowUp || id=="primary-action"))) &&
                (!Equals(button.Tag, "general-primary-action") || (HasProject && (IsDeveloper || _viewModel.PrimaryAction != PrimaryActionKind.Disabled)));
            if (_config.IsManagedDeployment && id is "cache-clear" or "backup-cleanup")
            {
                button.IsEnabled = false;
                ToolTip.SetTip(button, "관리형 캐시/백업 정리는 업데이트 서비스의 별도 관리 기능이 필요합니다.");
            }
            if(_configurationError is not null && id is "update" or "repair" or "rollback" or "resume-operation" or "cache-clear" or "backup-cleanup" or "open-folder")button.IsEnabled=false;
        }
        foreach (var control in _selectionControls) control.IsEnabled = !busy;
        RefreshPresentation();
    }

    private void UiProgress(string stage, string message, double? percent)
    {
        _viewModel.ApplyProgressStage(stage); _presentation.Stage(stage);
        if (_statusText is not null) SetStatus(IsDeveloper ? $"{stage}: {message}" : FriendlyProgress(stage, message));
        if (percent.HasValue) Progress(percent.Value); else Progress(stage switch { "Catalog" => 10, "Manifest" => 20, "Plan" => 35, "Download" => Math.Max(_progress?.Value ?? 0, 45), "Apply" => 85, "Package" => 88, "Launch" => 95, _ => Math.Max(_progress?.Value ?? 0, 5) });
        AppendLog(IsDeveloper ? $"[{stage}] {message}" : FriendlyProgress(stage, message));
    }

    private void ReportManagedProgress(ManagedAgentProgress progress)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            EngineProgress(progress.ToLauncherProgress());
            return;
        }

        Dispatcher.UIThread.InvokeAsync(() => EngineProgress(progress.ToLauncherProgress()))
            .GetAwaiter()
            .GetResult();
    }

    private void Progress(double v, bool measured = false) { _presentation.Percent = v >= 100 || measured ? Math.Clamp(v, 0, 100) : null; RefreshPresentation(); }
    private string FriendlyProgress(string stage, string message) => LauncherOperationPresentation.GeneralProgress(stage);
    private void MarkError(Exception ex, string status = "작업 실패", bool showDialog = true)
    {
        var uiError = LauncherUiError.From(ex);
        if (uiError.Code == "no-promoted-release") { AwaitPromotion(); return; }
        _presentation.Title=status;
        _presentation.ErrorCode=uiError.Code; _presentation.SupportId=uiError.SupportId;
        var runtimeBlocked = ex.GetBaseException() as RuntimeBlockedException;
        _installState = runtimeBlocked is null ? "오류" : runtimeBlocked.Observation.State == RuntimeState.Running ? "실행 중" : "실행 상태 확인 필요";
        _installDetail = FriendlyError(ex);
        _viewModel.GeneralState = runtimeBlocked is null ? GeneralLauncherState.RecoverableError : GeneralLauncherState.RuntimeBlocked;
        _viewModel.WorkflowStage = LauncherWorkflowStage.None;
        Build();
        if (_statusText is not null) SetStatus(status);
        AppendLog($"[{uiError.SupportId}] {uiError.Code}: {FriendlyError(ex)}", true);
        if (IsDeveloper) AppendLog(ex.ToString(), true);
        else if (showDialog && runtimeBlocked is null) ErrorDialog(status, FriendlyError(ex)+"\n지원 ID: "+uiError.SupportId);
    }
    private string FriendlyError(Exception ex) => _viewModel.FriendlyError(ex);
    private void UpdateInstallTile() { if (_installStateText is not null) { _installStateText.Text = _installState; _installStateText.Foreground = StatusBrush(_installState); } if (_installDetailText is not null) _installDetailText.Text = _installDetail; }

    private void ErrorDialog(string title,string message) => ShowEnterpriseError(title,message);
    private void GeneralSettings() => SettingsDialog(false);
    private void DeveloperSettings() => SettingsDialog(true);
    private void SettingsDialog(bool dev) => ShowEnterpriseSettings(dev);


}
