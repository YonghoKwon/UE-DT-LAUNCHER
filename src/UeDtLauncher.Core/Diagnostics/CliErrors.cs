namespace UeDtLauncher;

/// <summary>Public CLI errors use allowlisted causes; exception messages remain in redacted crash reports.</summary>
public static class CliErrors
{
    public static string Write(TextWriter output, Exception error, bool details = false, bool server = false)
    {
        var code = LauncherFailure.Code(error);
        var guidance = LauncherGuidance.For(error);
        var supportId = Guid.NewGuid().ToString("N");
        var cause = guidance.Message;
        var next = guidance.NextAction;
        if (server)
        {
            (cause, next) = error switch
            {
                FileNotFoundException or DirectoryNotFoundException => ("설정 또는 대상 파일이 없습니다.", "--config와 선택한 작업의 입력 파일을 확인하세요."),
                UnauthorizedAccessException => ("작업 경로에 필요한 권한이 없습니다.", "관리자에게 소유자와 보호 ACL 확인을 요청하세요."),
                IOException => ("저장 작업을 완료할 수 없거나 다른 작업이 잠금을 보유합니다.", "serve/watch와 유지보수 작업 상태 및 저장 공간을 확인한 뒤 다시 조회하세요."),
                InvalidOperationException => ("대상 상태 또는 승격 revision이 현재 조건과 맞지 않습니다.", "대상과 현재 revision을 다시 조회한 뒤 새로 확인하세요."),
                ArgumentException or System.Text.Json.JsonException => ("명령 입력 또는 설정 형식이 올바르지 않습니다.", "--help와 설정 파일의 필수 항목을 확인하세요."),
                _ => (cause, next)
            };
        }
        var type = server ? error.GetType().Name + ": " : string.Empty;
        output.WriteLine($"ERROR [{code}] {type}{cause} 다음 조치: {next} 지원 ID: {supportId}");
        if (details)
        {
            var types = new List<string>();
            for (Exception? current = error; current is not null && types.Count < 16; current = current.InnerException)
                types.Add(current.GetType().Name);
            output.WriteLine("Details: " + DiagnosticRedactor.Redact(string.Join(" -> ", types)));
        }
        return supportId;
    }
}
