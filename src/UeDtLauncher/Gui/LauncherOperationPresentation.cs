using System.Collections.ObjectModel;
namespace UeDtLauncher.Gui;

public enum LauncherUiOperation { None, Check, Catalog, Update, Repair, Launch, Troubleshoot, Rollback }
public sealed record LauncherRetryContext(LauncherUiOperation Operation, string ProjectId, string Environment, string Channel, string? Version);

public sealed class LauncherOperationPresentation
{
    public string Title { get; set; } = "상태를 확인하고 있습니다";
    public double? Percent { get; set; }
    public LauncherUiOperation Kind { get; private set; }
    public string? ErrorCode { get; set; }
    public string? SupportId { get; set; }
    public string CurrentStage { get; private set; } = "확인";
    public ObservableCollection<string> Logs { get; } = [];
    public LauncherRetryContext? Retry { get; set; }
    public void Begin(LauncherUiOperation operation)
    {
        Kind = operation; Percent = null; CurrentStage = "확인"; ErrorCode = SupportId = null;
        Title = operation == LauncherUiOperation.Rollback ? "백업 복원 중" : "작업을 확인하고 있습니다";
    }
    public void Stage(string stage)
    {
        // Metadata is read before download AND written after apply. Never infer completion by enum order.
        if (stage is "Apply" or "Backup" or "Rollback") CurrentStage = "적용";
        else if (stage == "Launch") CurrentStage = "실행";
        else if (stage is "Download" or "DownloadProgress" or "Package" && CurrentStage == "확인") CurrentStage = "파일 준비";
        else if (stage is "Recovery" or "Repair") CurrentStage = "복구";
    }
    public void Complete(string title) { Title = title; Percent = 100; CurrentStage = "완료"; ErrorCode = SupportId = null; }
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
