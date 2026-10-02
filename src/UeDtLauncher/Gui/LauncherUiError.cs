namespace UeDtLauncher.Gui;
public sealed record LauncherUiError(string Code,string Message,string SupportId,string Detail)
{
    public static LauncherUiError From(Exception error)
    {
        var code=LauncherFailure.Code(error);
        string? id=null;
        for(Exception? e=error;e is not null;e=e.InnerException)
        {
            if(e is AgentOperationException a)id=a.CorrelationId;
            id ??= e.Data["CorrelationId"] as string;
        }
        var message=code switch
        {
            "no-promoted-release" or "no-authorized-release" or "service-unavailable" or "server-unavailable" or "authentication-failed" or "access-denied" or "integrity-failed" or "runtime-data-unavailable" or "client-upgrade-required" or "diagnostic-response-invalid"=>LauncherGuidance.For(code).Message + " " + LauncherGuidance.For(code).NextAction,
            "file-access-denied"=>"파일 접근 권한을 확인해 주세요. 실행 중인 프로그램은 먼저 종료해 주세요.",
            "storage-failed"=>"파일을 처리할 수 없습니다. 저장 공간과 파일 사용 여부를 확인해 주세요.",
            "timeout"=>"연결 또는 작업 대기 시간이 초과됐습니다. 연결 상태를 확인한 뒤 같은 작업을 다시 시도해 주세요.",
            "not-configured" or "configuration-invalid"=>"런처 설정이 필요합니다. 관리자에게 문의해 주세요.",
            "runtime-blocked"=>(error.GetBaseException() as RuntimeBlockedException)?.Observation.State == RuntimeState.Running
                ? "실행 중—프로그램을 종료한 뒤 다시 시도해 주세요."
                : "실행 상태 확인이 필요합니다. 관리자 점검 후 다시 시도해 주세요.",
            "no-backup"=>"복원할 수 있는 백업이 없습니다.",
            "backup-preview-changed"=>"백업 정보가 변경됐습니다. 복원할 백업을 다시 확인해 주세요.",
            _=>"작업을 완료하지 못했습니다. 다시 확인하거나 지원 ID와 함께 관리자에게 문의해 주세요."
        };
        return new(code,message,id??Guid.NewGuid().ToString("N"),DiagnosticRedactor.Redact(error.ToString()));
    }
}
