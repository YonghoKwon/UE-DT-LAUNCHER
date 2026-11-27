# 개발 PC에서 Windows 배포 시험

개발용 Windows x64 PC와 테스트 LAN을 사용합니다. HTTP 요청은 서명하지만 전송 내용은 평문입니다. 테스트 프로젝트·키·패키지를 사용하세요.

## 1. 서버 준비와 시작

- 같은 소스의 자체 포함 **win-x64 DistributionServer**와 **일반 win-x64 런처** 게시 폴더를 준비합니다. [.NET 게시 명령](../../../README.md)을 참고하세요. 서버 게시에는 README의 서버 명령에서 RID를 `win-x64`로 지정합니다.
- [공식 Caddy 2.11.9 Windows amd64 ZIP](https://github.com/caddyserver/caddy/releases/download/v2.11.9/caddy_2.11.9_windows_amd64.zip)을 압축 상태로 준비합니다. 도구가 ZIP과 실행 파일의 SHA-256을 확인합니다.
- 현재 사용자로 Windows PowerShell 5.1을 엽니다. 조직 정책이 서명 스크립트만 허용하면 승인된 서명본을 사용하세요.

```powershell
$Lab = '.\tools\windows-lab\WindowsLab.ps1'
& $Lab Start -Address 192.168.50.10 -ServerExe .\publish\server-win-x64\UeDtLauncher.DistributionServer.exe -LauncherExe .\publish\client-win-x64\UeDtLauncher.exe -CaddyZip .\caddy_2.11.9_windows_amd64.zip
```

예시 IP를 개발 PC의 테스트 LAN IPv4로 바꾸세요. 폴더 기본값은 `%LOCALAPPDATA%\UeDtWindowsLab`입니다. 다른 위치를 쓰면 매번 같은 `-Root`를 사용합니다. `Start` 재실행은 저장된 주소·키·프로세스를 재사용합니다. 주소를 바꾸려면 새 시험 폴더를 만드세요.

**방화벽.** 다른 PC 접속이 필요하면 Start가 출력한 명령을 관리자 PowerShell에서 실행합니다. 기본은 Private 프로필의 TCP 18080 한 포트입니다. 필요한 PC 주소로 `RemoteAddress`를 좁힐 수 있습니다. 백엔드 18500은 루프백입니다.

## 2. 다른 PC 등록

대상 PC에서 고유 PC ID로 다음을 실행하고 `pc01.enroll.json`만 개발 PC에 전달합니다. Linux는 `./UeDtLauncher`를, Windows 관리형 스트리밍은 아래 설명대로 `--storage managed`를 사용합니다.

```powershell
.\UeDtLauncher.exe enroll prepare --name ue-dt-win-lab --key-id pc01-key --client pc01 --output .\enroll --storage portable
& $Lab Add-Client -Enrollment .\enroll\pc01.enroll.json -ClientAddress 192.168.50.21 -Project lab-demo -ClientKind WindowsClient
```

개발 PC에서 프록시가 볼 실제 PC IPv4를 넣습니다. `-ClientKind LinuxStreaming` 또는 `WindowsStreaming`을 선택할 수 있습니다. 기본 권한은 `prod/stable`입니다. 준비 때 다른 이름을 썼으면 `-CredentialName`에도 같은 이름을 넣으세요. 출력된 `client-pc01` 폴더의 공개 설정과 PEM을 대상 PC에 신뢰된 경로로 전달합니다. PC 요청 개인키는 대상 PC에 남습니다.

## 3. ZIP 올리기·승인·승격

`& $Lab Publish`가 incoming과 서버 콘솔 명령을 표시합니다. 직접 올릴 때는 매번 새 incoming 하위 폴더에 ZIP과 외부 `release.json`을 둡니다. 두 파일을 `.uploading` 이름으로 복사한 뒤 ZIP부터 최종 이름으로 바꾸세요. ZIP 이름은 `packageFile`과 일치해야 합니다.

```powershell
& $Lab Publish -Zip .\Demo.zip -Project lab-demo -Version 1.0.0 -PayloadRoot Demo -EntryPoint Demo.exe
```

Linux ZIP에는 `-Platform linux-x64 -EntryPoint Demo.sh -ExecutablePaths Demo.sh`처럼 실제 실행 경로를 지정하세요. 콘솔에서 **pending 확인 → 승인 → 현재 revision 확인 → 명시 승격**합니다. v2도 같은 순서입니다. 이후 v1을 다시 승격해 추천 변경을 시험합니다. [콘솔 운영 절차](../distribution-workflow.md) · [release.json 만들기](../feature-workflow.md)

## 4. 다른 PC에서 확인

### Windows 고성능 클라이언트

받은 묶음 옆에서 `UeDtLauncher.exe gui --config .\launcher.config.json`으로 열고 목록 → v1 설치·실행 → 앱 정상 종료 → v2 추천에서 업데이트를 확인합니다. v1 재승격 후 새로고침해 **권장 버전 변경 안내**를 확인합니다. 개발자 PC는 `Add-Client -Developer`로 `dev/beta`를 받고 개발자 EXE의 08D 화면에서 해당 트랙의 목록과 설치를 확인합니다.

### Linux 스트리밍 서버

`linux-x64` 런처와 받은 portable 설정을 실행 계정 경로에 둡니다. `./UeDtLauncher doctor --online --config launcher.config.json`으로 Catalog와 추천을 확인한 뒤 `run --config launcher.config.json --no-launch`로 설치하고 `run --config launcher.config.json`으로 실행합니다. 앱 정상 종료 후 v2에서 반복하고, v1 재승격 뒤에는 온라인 진단에서 추천이 바뀌는지 확인합니다.

### Windows 스트리밍 서버

기존 관리형 환경에서 관리자 PowerShell로 `enroll prepare --storage managed`를 사용합니다. 전달받은 `setup.json`의 origin·프로젝트·키 ID·credential 이름과 배포 공개키로 Agent 운영 설정을 적용하고, 받은 `launcher.config.json`은 선택 전용으로 둡니다. Agent 설정 반영 → `doctor --online` → `run --no-launch` → 로그인한 스트리밍 사용자 세션에서 `run` 순으로 확인합니다. v2에서도 앱을 정상 종료한 뒤 반복하고, v1 재승격은 먼저 진단으로 확인합니다. [관리형 등록](../intranet-auth.md) · [무인 실행과 서비스 세션](../service-mode.md)

## 5. 정리

`& $Lab Status` → `& $Lab Stop` → `& $Lab Status`로 소유 프로세스가 모두 종료됐는지 확인합니다. 보존할 자료를 확인한 뒤 시험 폴더를 정리하고 시험에 추가한 방화벽 규칙을 제거합니다.
