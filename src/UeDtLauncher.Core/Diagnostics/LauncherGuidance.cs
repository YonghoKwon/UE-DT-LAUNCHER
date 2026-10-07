namespace UeDtLauncher;

public sealed record LauncherGuidance(string Message, string NextAction, string Owner)
{
    public static LauncherGuidance For(string code) => code switch
    {
        "no-promoted-release" => new("관리자가 실행 버전을 지정하지 않았습니다.", "관리자에게 해당 프로젝트의 추천 버전 승격을 요청해 주세요.", "admin"),
        "no-authorized-release" => new("현재 PC와 선택 조건에서 받을 수 있는 배포가 없습니다.", "관리자에게 배포 승인 여부와 PC별 허용 목록을 확인해 달라고 요청해 주세요.", "admin"),
        "service-unavailable" => new("업데이트 서비스에 연결할 수 없습니다.", "관리자에게 업데이트 서비스 실행 상태를 확인해 달라고 요청한 뒤 다시 확인해 주세요.", "admin"),
        "server-unavailable" => new("배포 서버에 연결할 수 없습니다.", "네트워크를 확인하고 다시 시도해 주세요. 계속되면 관리자에게 문의해 주세요.", "user"),
        "authentication-failed" or "credential" => new("PC 인증을 확인할 수 없습니다.", "관리자에게 인증키 등록·폐기·접근 권한을 확인해 달라고 요청해 주세요.", "admin"),
        "access-denied" => new("이 PC에 허용되지 않은 배포입니다.", "관리자에게 PC 주소와 선택 배포의 접근 권한을 확인해 달라고 요청해 주세요.", "admin"),
        "integrity-failed" or "signing-keys" => new("업데이트 보안 검증에 실패했습니다.", "검증을 끄지 말고 관리자에게 공개키와 배포 파일 확인을 요청해 주세요.", "admin"),
        "configuration-invalid" or "not-configured" => new("런처 설정이 필요합니다. 관리자에게 문의해 주세요.", "관리자가 설정을 확인한 뒤 다시 확인을 눌러 주세요.", "admin"),
        "client-upgrade-required" => new("업데이트 서비스의 상세 진단을 지원하지 않습니다.", "관리자에게 런처와 업데이트 서비스를 함께 갱신해 달라고 요청해 주세요.", "admin"),
        "diagnostic-response-invalid" => new("업데이트 서비스의 진단 응답을 확인할 수 없습니다.", "관리자에게 서비스 점검을 요청한 뒤 진단을 다시 실행해 주세요.", "admin"),
        "runtime-data-unavailable" => new("프로그램 저장 경로를 준비할 수 없습니다.", "관리자에게 저장 경로 설정과 폴더 권한 확인을 요청해 주세요.", "admin"),
        _ => new("검사를 완료하지 못했습니다.", "지원 ID와 검사 코드를 관리자에게 전달해 주세요.", "admin")
    };
}
