namespace UeDtLauncher.Distribution;

internal static class OperationsConsoleTables
{
    internal static string ShortId(string id) => id[..Math.Min(8, id.Length)];
    internal static string State(string state) => state switch
    {
        "waiting" => "대기",
        "validating" => "검사 중",
        "pending" => "승인 대기",
        "publishing" => "게시 중",
        "failed" => "실패",
        "rejected" => "거절",
        "published" => "게시됨",
        _ => "미확인"
    };
    internal static void Summary(TextWriter output, IReadOnlyList<IntakeJob> jobs)
        => output.WriteLine($"승인 대기 {jobs.Count(j => j.State == "pending")} · 검사 중 {jobs.Count(j => j.State == "validating")} · 실패 {jobs.Count(j => j.State == "failed")} · 게시됨 {jobs.Count(j => j.State == "published")} · 대기 {jobs.Count(j => j.State == "waiting")}");

    private static string Cell(string? text, int max = 24)
    {
        var safe = HumanText.Safe(text);
        return safe.Length > max ? safe[..(max - 1)] + "…" : safe;
    }
    internal static void Jobs(TextWriter output, IReadOnlyList<ConsoleJobDetail> rows)
    {
        output.WriteLine("번호 | 상태(한국어) | 프로젝트 | 버전 | 플랫폼 | 크기 | 진행 | 작업 ID 앞 8자");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var progress = row.TotalBytes > 0 ? Math.Clamp((double)row.ProcessedBytes / row.TotalBytes * 100, 0, 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : Cell(row.Phase ?? "대기");
            output.WriteLine($"{i + 1,4} | {State(row.State),-9} | {Cell(row.ProjectId),-15} | {Cell(row.Version),-12} | {Cell(row.Platform),-12} | {(row.PackageSize is { } size ? HumanText.Size(size) : "미확인"),9} | {progress,-14} | {ShortId(row.Id)}");
        }
        if (rows.Count == 0) output.WriteLine("해당 작업이 없습니다.");
    }

    internal static void Detail(TextWriter output, ConsoleJobDetail row)
    {
        output.WriteLine("작업 ID: " + HumanText.Safe(row.Id));
        output.WriteLine("상태: " + State(row.State));
        output.WriteLine("프로젝트: " + HumanText.Safe(row.ProjectId));
        output.WriteLine("버전: " + HumanText.Safe(row.Version));
        output.WriteLine("환경/채널: " + HumanText.Safe(row.Environment) + "/" + HumanText.Safe(row.Channel));
        output.WriteLine("플랫폼: " + HumanText.Safe(row.Platform));
        output.WriteLine("실행 파일: " + HumanText.Safe(row.EntryPoint));
        output.WriteLine("ZIP 크기: " + (row.PackageSize is { } size ? HumanText.Size(size) : "미확인"));
        if (row.Error is not null) output.WriteLine("오류 원인: " + HumanText.Safe(row.Error));
        if (row.State == "failed") output.WriteLine("다음 할 일: 원인 수정 후 새 업로드 ID로 다시 올리거나 5번 재시도를 선택하세요.");
    }

    internal static void Releases(TextWriter output, IReadOnlyList<PublishedRelease> releases, IReadOnlyList<PromotionEvent> events)
    {
        output.WriteLine("번호 | 버전 | 현재 추천 | ReleaseId");
        string? previous = null;
        for (var i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            var meta = release.Metadata;
            var track = ReleasePromotions.Track(new(meta.ProjectId, meta.Environment, meta.Channel, meta.Platform, meta.Version));
            if (track != previous) { output.WriteLine("트랙: " + HumanText.Safe(track)); previous = track; }
            var current = events.LastOrDefault(e => e.Track == track)?.ReleaseId;
            output.WriteLine($"{i + 1,4} | {Cell(meta.Version),-12} | {(current == release.ReleaseId ? "* 추천" : ""),-8} | {HumanText.Safe(release.ReleaseId)}");
        }
        if (releases.Count == 0) output.WriteLine("게시된 버전이 없습니다.");
    }
}
