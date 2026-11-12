using System.Globalization;

namespace UeDtLauncher;

public static class HumanText
{
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return size.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[index];
    }

    public static string Safe(string? text) => string.Concat(DiagnosticRedactor.Redact(text ?? "미확인")
        .Select(c => char.IsControl(c) ? ' ' : c == '|' ? '¦' : c));
}

public sealed record UploadProgress(int Number, string Message, long Bytes = 0);

public static class UploadPresentation
{
    public static string Stage(string name) => name switch
    {
        "mkdir" => "원격 폴더 준비", "zip-transfer" => "ZIP 전송", "json-transfer" => "설명 파일 전송",
        "zip-rename" => "ZIP 최종 이름 변경", "json-rename" => "설명 파일 최종 이름 변경", "verify" => "최종 파일 확인",
        _ => "업로드 준비"
    };

    public static void Progress(TextWriter output, UploadProgress progress)
    {
        var size = progress.Bytes > 0 ? " (" + HumanText.Size(progress.Bytes) + ")" : "";
        var hint = progress.Message == "ZIP 전송 중" ? " — 큰 파일은 오래 걸릴 수 있습니다" : "";
        output.WriteLine($"[{progress.Number}/6] {HumanText.Safe(progress.Message)}{size}{hint}");
    }

    public static void Write(TextWriter output, UploadResult result)
    {
        output.WriteLine(result.DryRun ? "업로드 계획 (전송·파일 변경 없음)" : result.Success ? "업로드 완료" : "업로드 중단");
        output.WriteLine("업로드 ID: " + HumanText.Safe(result.UploadId));
        output.WriteLine("원격 폴더: " + HumanText.Safe(result.RemoteDirectory));
        var failed = result.FailedStage is null ? -1 : result.Steps.ToList().FindIndex(s => s.Name == result.FailedStage);
        for (var i = 0; i < result.Steps.Count; i++)
        {
            var status = result.DryRun ? "미실행 (계획)" : result.Success || i < failed ? "완료" : i == failed ? "실패" : "미실행";
            output.WriteLine($"{i + 1}. {Stage(result.Steps[i].Name)}: {status}");
        }
        output.WriteLine("남은 상태: " + HumanText.Safe(result.Remaining));
        if (result.WorkDirectory is not null) output.WriteLine("로컬 작업 사본: " + HumanText.Safe(result.WorkDirectory));
        output.WriteLine("다음 할 일: " + HumanText.Safe(result.NextAction));
    }
}
