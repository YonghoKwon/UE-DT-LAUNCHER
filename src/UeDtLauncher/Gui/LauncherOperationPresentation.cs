using System.Collections.ObjectModel;
namespace UeDtLauncher.Gui;

public enum LauncherUiOperation { None, Check, Catalog, Update, Repair, Launch, Troubleshoot, Rollback, ClearStaging, PruneBackups }
public sealed record LauncherRetryContext(LauncherUiOperation Operation, string ProjectId, string Environment, string Channel, string? Version, ReleaseSelection? Selection = null);
internal sealed record LauncherPresentationSnapshot(string Title,double? Percent,LauncherUiOperation Kind,string? ErrorCode,string? SupportId,string Stage,LauncherRetryContext? Retry);

public sealed class LauncherOperationPresentation
{
    public string Title { get; set; } = "상태를 확인하고 있습니다";
    public double? Percent { get; set; }
    public LauncherUiOperation Kind { get; private set; }
    public string? ErrorCode { get; set; }
    public string? SupportId { get; set; }
    public string CurrentStage { get; private set; } = "확인";
    internal bool Committing { get; private set; }
    internal bool ShowCompletion { get; private set; }
    internal string? InstallationNotice { get; set; }
    public string Announcement => ErrorCode is not null || Percent == 100 || Kind == LauncherUiOperation.None
        ? Title : CurrentStage switch
        {
            "재확인" => Title,
            "파일 준비" => "필요한 파일을 준비하고 있습니다.",
            "적용" => "프로그램 파일을 적용하고 있습니다.",
            "복구" => "파일을 복구하고 있습니다.",
            "실행" => "프로그램을 실행하고 있습니다.",
            _ => "배포와 설치 상태를 확인하고 있습니다."
        };
    public ObservableCollection<string> Logs { get; } = [];
    internal ObservableCollection<LauncherHistoryEntry> History { get; } = [];
    internal bool TracksHistory { get; set; }
    public LauncherRetryContext? Retry { get; set; }
    public static LauncherUiOperation RetryOperation(LauncherRetryContext? failed, LauncherRetryContext current, bool runtimeBlocked = false) =>
        runtimeBlocked ? LauncherUiOperation.Check : failed is null ? LauncherUiOperation.None : failed == current ? failed.Operation : LauncherUiOperation.Check;
    public void Begin(LauncherUiOperation operation)
    {
        Kind = operation; Percent = null; CurrentStage = "확인"; ErrorCode = SupportId = null;
        ShowCompletion = Committing = false;
        InstallationNotice = null;
        Title = operation == LauncherUiOperation.Rollback ? "백업 복원 중" : "작업을 확인하고 있습니다";
        RecordHistory(LauncherHistoryResult.Started);
    }
    internal LauncherPresentationSnapshot Capture() => new(Title,Percent,Kind,ErrorCode,SupportId,CurrentStage,Retry);
    internal void Restore(LauncherPresentationSnapshot value)
    {
        Title=value.Title;Percent=value.Percent;Kind=value.Kind;ErrorCode=value.ErrorCode;
        SupportId=value.SupportId;CurrentStage=value.Stage;Retry=value.Retry;
    }
    internal void BeginFollowUp(string title)
    {
        Kind=LauncherUiOperation.Check;Percent=null;CurrentStage="재확인";ShowCompletion=Committing=false;
        ErrorCode=SupportId=null;Title=title;
        RecordHistory(LauncherHistoryResult.Started);
    }
    public void Stage(string stage)
    {
        var previous = CurrentStage;
        // Metadata is read before download AND written after apply. Never infer completion by enum order.
        if (stage is "Apply" or "Backup" or "Rollback") CurrentStage = "적용";
        else if (stage == "Launch") CurrentStage = "실행";
        else if (stage is "Download" or "DownloadProgress" or "Package" && CurrentStage == "확인") CurrentStage = "파일 준비";
        else if (stage is "Recovery" or "Repair") CurrentStage = "복구";
        if (stage is "Apply" or "Backup") Committing = true;
        if (CurrentStage != previous) RecordHistory(LauncherHistoryResult.InProgress);
    }
    public void Complete(string title, bool showCompletion = true)
        => Complete(title, showCompletion, LauncherHistoryResult.Completed);

    internal void Complete(string title, bool showCompletion, LauncherHistoryResult outcome)
    {
        RecordHistory(outcome);
        Title = title; Percent = 100; CurrentStage = "완료"; ErrorCode = SupportId = null;
        Committing = false;
        ShowCompletion = showCompletion && Kind is not (LauncherUiOperation.None or LauncherUiOperation.Check or LauncherUiOperation.Catalog);
    }

    internal void RecordHistory(LauncherHistoryResult result)
    {
        if (!TracksHistory) return;
        var operation = Kind switch
        {
            LauncherUiOperation.Catalog => "배포 목록",
            LauncherUiOperation.Update => "업데이트",
            LauncherUiOperation.Repair => "복구",
            LauncherUiOperation.Launch => "실행",
            LauncherUiOperation.Troubleshoot => "문제 해결",
            LauncherUiOperation.Rollback => "백업 복원",
            LauncherUiOperation.ClearStaging => "임시 파일 정리",
            LauncherUiOperation.PruneBackups => "백업 정리",
            _ => "상태 확인"
        };
        // Summaries use only application labels, never raw log messages, IDs or paths.
        if (History.Count == 500) History.RemoveAt(0);
        History.Add(new(DateTimeOffset.Now, operation, CurrentStage, result));
    }

    internal string DownloadSteps => CurrentStage switch
    {
        "파일 준비" => "● 다운로드  ○ 검증  ○ 설치  ○ 완료",
        "검증" => "✓ 다운로드  ● 검증  ○ 설치  ○ 완료",
        "적용" or "실행" => "✓ 다운로드  ✓ 검증  ● 설치  ○ 완료",
        "완료" => "✓ 다운로드  ✓ 검증  ✓ 설치  ● 완료",
        _ => "○ 다운로드  ○ 검증  ○ 설치  ○ 완료"
    };

    internal void VerifyingDownloads() => CurrentStage = "검증";
    public void Append(string text)
    {
        Logs.Add(DiagnosticRedactor.Redact(text));
        while (Logs.Count > 500) Logs.RemoveAt(0);
    }
    public string LogText => string.Join(Environment.NewLine, Logs);
    public static string GeneralProgress(string stage) => stage switch
    {
        "Catalog" => "허용된 배포를 확인하고 있습니다.",
        "Manifest" or "Plan" => "파일 정보를 확인하고 있습니다.",
        "Download" or "DownloadProgress" or "Package" => "필요한 파일을 다운로드하고 검증하고 있습니다.",
        "Apply" or "Backup" => "프로그램 파일을 적용하고 있습니다.",
        "Recovery" or "Repair" => "파일을 복구하고 있습니다.",
        "Rollback" => "선택한 설치의 백업을 복원하고 있습니다.",
        "Retry" => "같은 작업을 다시 시도하고 있습니다.",
        "Launch" => "프로그램을 실행하고 있습니다.",
        "Complete" => "작업이 완료되었습니다.",
        _ => "작업을 진행하고 있습니다."
    };
}
