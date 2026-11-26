using System.Globalization;

namespace UeDtLauncher.Gui;

internal enum LauncherHistoryResult { Started, InProgress, Completed, Cancelled, Failed, Waiting }

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
                _ => "대기"
            };
            return $"{Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  ·  {Operation} / {Stage}  ·  {outcome}";
        }
    }
}
