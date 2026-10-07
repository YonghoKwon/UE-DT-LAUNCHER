using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private DoctorReport? _lastDoctorReport;
    internal Window ShowReadinessDialog()
    {
        var path = ConfigPath;
        var target = _configurationError is null ? DoctorTarget.From(_config) with { ProjectId = _selectedProject.ProjectId } : null;
        var developer = IsDeveloper;
        var cancellation = new CancellationTokenSource();
        var resultBody = Identify(new StackPanel { Spacing = 12 }, "readiness-results", "준비 상태 점검 결과");
        var summary = Identify(Txt("점검 중", 18, true), "readiness-summary", "준비 상태");
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        var support = Identify(new TextBox { IsReadOnly = true, Text = "", MinHeight = 38 }, "readiness-support", "점검 지원 ID");
        Window? dialog = null;
        var close = Identify(SecondaryButton("닫기", (_, _) => dialog!.Close(), 40), "dialog-cancel", "닫기");
        Button? onlineButton = null;
        var generation = 0;
        async Task CheckAsync(bool online)
        {
            var currentGeneration = ++generation;
            onlineButton!.IsEnabled = false;
            summary.Text = online ? "온라인 연결 점검 중" : "설정 점검 중";
            resultBody.Children.Clear(); support.Text = "";
            try
            {
                var report = DoctorResponse.Validate(await _doctor(path, online, target, cancellation.Token), target);
                if (cancellation.IsCancellationRequested || currentGeneration != generation) return;
                var currentTarget = _configurationError is null ? DoctorTarget.From(_config) with { ProjectId = _selectedProject.ProjectId } : null;
                if (path != ConfigPath || target != currentTarget || (report.Target is not null && target != report.Target))
                { summary.Text = "선택이 변경됐습니다. 창을 닫고 다시 점검해 주세요."; return; }
                summary.Text = DoctorPresentation.Summary(report);
                _lastDoctorReport = report;
                support.Text = report.SupportId ?? "상세 진단 미지원";
                resultBody.Children.Add(Muted("설정과 연결을 점검한 결과입니다. 설치 파일·실행 환경은 작업 시 추가 검증합니다.", 13));
                foreach (var check in report.Checks)
                {
                    var label = check.State switch { "passed" => "통과", "failed" => "확인 필요", "waiting" => "관리자 조치 대기", "not-applicable" => "해당 없음", _ => "추가 검증 필요" };
                    if (!developer && check.State is "passed" or "not-applicable") continue;
                    var guidance = LauncherGuidance.For(check.Code ?? "unknown");
                    var message = developer ? DiagnosticRedactor.Redact(check.Message) : check.State is "failed" or "waiting" ? guidance.Message :
                        check.Code == "online-not-checked" ? "온라인 연결은 아직 점검하지 않았습니다." : "실행 직전 사용자 환경 검사가 추가로 필요합니다.";
                    resultBody.Children.Add(Txt(label + " · " + message, 14, false));
                    if (developer) resultBody.Children.Add(Muted($"{check.Subject ?? "미검증"} / {check.Code ?? check.Name}", 12));
                    if (check.NextAction is not null) resultBody.Children.Add(Muted((check.ActionOwner == "admin" ? "관리자: " : "사용자: ") + check.NextAction, 13));
                }
                if (report.PreparationState is null) resultBody.Children.Add(Txt("업데이트 서비스가 상세 진단을 지원하지 않습니다. 관리자에게 갱신을 요청해 주세요.", 14, false));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (cancellation.IsCancellationRequested) return;
                var safe = LauncherUiError.From(error);
                summary.Text = "점검을 완료하지 못했습니다"; support.Text = safe.SupportId;
                resultBody.Children.Add(Txt(safe.Message, 14, false));
            }
            finally { if (!cancellation.IsCancellationRequested && currentGeneration == generation) onlineButton.IsEnabled = true; }
        }
        onlineButton = Identify(SecondaryButton("온라인 연결 점검", async (_, _) => await CheckAsync(true), 40), "readiness-online", "온라인 연결 점검");
        var copy = Identify(SecondaryButton("지원 ID 복사", async (_, _) => { if (Clipboard is not null) await Clipboard.SetTextAsync(support.Text ?? ""); }, 40), "readiness-copy", "점검 지원 ID 복사");
        dialog = AccessibleDialog("연결·준비 상태 점검", new StackPanel { Spacing = 14, Children = { summary, resultBody, support } }, close, copy, onlineButton);
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.Opened += async (_, _) => await CheckAsync(false);
        dialog.Show(this);
        return dialog;
    }
}
