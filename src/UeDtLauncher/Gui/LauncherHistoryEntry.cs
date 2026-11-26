using System.Globalization;

namespace UeDtLauncher.Gui;

internal enum LauncherHistoryResult { Started, InProgress, Completed, Cancelled, Failed, Waiting, Skipped, Unknown }

internal sealed record LauncherHistoryEntry(DateTimeOffset Time, string Operation, string Stage, LauncherHistoryResult Result)
{
    public string Text
    {
        get
        {
            var outcome = Result switch
            {
                LauncherHistoryResult.Started => "시작",
                LauncherHistoryResult.InProgress => "진행 중",
                LauncherHistoryResult.Completed => "완료",
                LauncherHistoryResult.Cancelled => "취소",
                LauncherHistoryResult.Failed => "오류",
                LauncherHistoryResult.Skipped => "생략",
                LauncherHistoryResult.Unknown => "결과 확인 필요",
                _ => "대기"
            };
            return $"{Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  ·  {Operation} / {Stage}  ·  {outcome}";
        }
    }
}

// A journal entry follows its caller's operation across presentation changes.
internal sealed class LauncherHistoryOperation
{
    private readonly string _operation;
    private readonly Action<string, string, LauncherHistoryResult> _record;
    private string _stage;
    private bool _finished;

    public LauncherHistoryOperation(string operation, string stage, Action<string, string, LauncherHistoryResult> record)
    {
        _operation = operation;
        _stage = stage;
        _record = record;
        _record(_operation, _stage, LauncherHistoryResult.Started);
    }

    public void Progress(string stage)
    {
        if (_finished) return;
        var label = stage switch
        {
            "Manifest" or "Plan" => "검증",
            "Download" or "DownloadProgress" => "파일 준비",
            "Apply" or "Backup" or "Package" or "Rollback" => "적용",
            "Recovery" or "Repair" => "복구",
            "Launch" => "실행",
            _ => _stage
        };
        if (label == _stage) return;
        _stage = label;
        _record(_operation, _stage, LauncherHistoryResult.InProgress);
    }

    public void Finish(LauncherHistoryResult outcome, string? stage = null)
    {
        if (_finished) return;
        _finished = true;
        _record(_operation, stage ?? _stage, outcome);
    }
}
