# UE-DT Launcher

Unreal Engine Windows/Linux 패키징 프로그램을 사내 서버에 등록하고, 허용된 PC에서 설치·업데이트·실행하는 .NET 8 / Avalonia 배포 시스템입니다.

최종 목표는 **Unreal Engine DT 프로그램의 패키징 결과를 안전하게 배포하고, 회사에서 안정적으로 설치·업데이트·실행·복구할 수 있는 배포 시스템**입니다. 목표는 확정됐지만 회사 운영 승인 조건을 모두 충족한 상태는 아닙니다. 목표·승인 조건·미정 정책은 [PROJECT_GOALS](PROJECT_GOALS.md)에 있습니다.

## 현재 상태

- **합성 앱 기반 배포·설치·실행·복구는 로컬에서 활용 가능한 단계이며, 회사 정식 운영 인수(G1~G6)는 미완료입니다.**
- 실제 회사 인증서·서비스 계정·RHEL·실제 UE 업데이트/복구·MSI/RPM 설치 수명주기는 아직 검증하지 않았습니다. 원격 CI는 기준 cef9f53의 과거 build 성공을 확인했으며 이번 스프린트 커밋은 원격 실행하지 않았습니다.
- 2026-11-12 SPRINT-01: CLI 오류 정제·429/stale 안내·인수 검증, `upload`·서버 `console`, View 책임 분리를 추가했습니다. Windows 기본 환경 테스트890건과 고정 게시본 readiness8/headless3가 통과했습니다. 실제 GUI는 이번에 실행하지 않았고 과거 고정 게시본의 수용 이력은 별도로 보존합니다. [실행 범위·실패 이력](docs/reference/archive/validation/sprint-01-quality-admin-validation.md)
- 2026-11-12~13 fix-01: 1차 리뷰7커밋을 보존한 뒤, 한국어 업로드 출력·`upload check`·작업 사본 정리·번호식 콘솔을 보완했습니다. 제품 입력 `2e4b8f9`의 기본 환경 전체 테스트966건이 통과했습니다. 초기890건/e24aa93과 1차 리뷰926건/35dde8a는 서로 다른 검증 이력으로 유지합니다. 실제 SSH 성공·회사 운영 인수는 별도입니다.
- 항목별 진행률은 [개선 대장](IMPROVEMENTS.md), 최신 후보의 테스트·GUI 수용 수치는 [자동 생성 진행표](docs/reference/archive/validation/local-completion-progress.md), 남은 GUI 사례는 [단일 GUI 수용 체크리스트](docs/reference/guide-03-launcher-usage.md#현재-gui-수용-체크리스트)를 따릅니다. 진행률 평균을 제품 완성률로 해석하지 않습니다.
- 날짜·후보별 이전 검증 기록은 [현황 이력](docs/reference/archive/history/status-log.md)에 있습니다.

## 구성 요소

| 영역 | 내용 |
|---|---|
| 접수 | 업로드 폴더별 ZIP + 외부 release.json, 크기·SHA-256·안전한 ZIP 검사, 파일 세대로 작업 상태·감사 기록 |
| 게시 | 관리자 승인, 디렉터리 자동 생성, Manifest·서명, 완료 전 비공개, 중단 게시 재개 |
| 권한 | 실제 IP/CIDR + PC 요청 서명(기존 HTTPS는 Bearer), 프로젝트·환경·채널·선택적 버전 제한, 기본 거부 |
| 전송 | 인증된 목록·Manifest·이미지·파일·Range, 서명·해시 검증, 재시도·이어받기 |
| 배포 서버 저장소 | `server.json`의 전용 root에서 `authority/HEAD`가 접수·게시·승격·자격·순번을 함께 선택합니다. 전용 데이터베이스 없이 운영하며 새 배치는 빈 root에서 시작합니다. 구형·혼합·손상 root는 변경 전 거부합니다. |
| 설치 | 정확한 릴리스, 버전별 설치·상태·잠금 분리, 실행 중/불명 상태의 update·repair·rollback·transaction 복구 차단 |
| 실행 | 사용자 세션 runtime-host, Windows Job / Linux x64 subreaper, 표준 후손 종료까지 추적, 자동 kill 없음 |
| 일반 화면 | 포스코DX 밝은 화면, 첫 확인 중 자리 표시, 상태별 주 버튼, 전송 중 진행·예상 남은 시간, 오류별 지원 정보, 프로젝트 이름 타일 |
| 개발자 화면 | 포스코DX 다크 화면, 허용 버전·현재 추천 표시, 선택 배포의 설치 상태, 한국어 작업 기록·원문·로그 ZIP |
| 운영 | Windows/Linux Agent·IPC·CLI, 진단 내보내기, 무인 서비스 모드, MSI/RPM 제작 구성 |

- 일반 `UeDtLauncher.exe`와 개발자 `UeDtLauncher.Developer.exe`는 별도 빌드이며, 설정으로 GUI 종류를 바꾸지 못합니다. 두 빌드의 CLI는 공통이고 다운로드 권한은 서버가 결정합니다.
- **관리형**은 Agent(화면 표기: 업데이트 서비스)가 보호 설정·credential·Manifest·설치 상태를 검사하고 설치하며, GUI/CLI는 화면 설정의 정확한 선택만 사용해 사용자 세션에서 실행합니다. **Portable**은 Agent 없이 현재 계정의 로컬 엔진과 runtime 기록을 사용합니다.
- 일반 화면은 첫 실행에 설치·업데이트·지원 안내를 표시합니다. 설정의 일반·정보·화면·문제 해결 탭과 배포 목록의 현재·이전 버전 변경 내역은 [GUI 사용법](docs/reference/launcher-user-guide.md)을 참고하세요.
- 런처 창은 시작할 때 화면 작업 영역 가운데에 열립니다. 개발자 버전 목록에서 방향키로 선택하면 화면에 표시된 선택 행에 포커스를 유지합니다.
- 현재 OS에 허용된 프로그램이 여러 개면 일반 화면의 **내 프로그램**에서 각각의 설치·실행 상태를 확인하고 프로그램을 선택합니다. 하나면 해당 프로그램 화면이 바로 열립니다.
- 현재 권장 버전이 이 PC의 같은 배포 트랙에서 실행 중이거나 마지막으로 설치·실행한 버전보다 낮으면 일반 화면에 **권장 버전 변경**을 표시합니다. 권장 버전이 이미 설치된 경우에도 안내하며, 변경을 선택하면 실행 상태 확인 후 권장 버전을 실행합니다.
- 일반 GUI는 자동 점검만 하며 설치는 사용자 클릭 후 수행합니다. 관리형 런처 자체 갱신은 MSI/RPM, 게임 콘텐츠 갱신은 Agent 책임입니다.
- 기존 HTTPS/Bearer와 schema 3 사내 HTTP 요청 서명을 지원하며 Metadata 서명·해시·권한 검증을 유지합니다. nginx 뒤 API는 loopback에 바인딩합니다.

## 배포 흐름

```mermaid
flowchart LR
    package["개발자: ZIP와 외부 JSON"] --> inspect["서버: 자동 검사"]
    inspect --> approve["관리자: 승인"]
    approve --> publish["서버: 서명과 공개"]
    publish --> verifyRelease["개발자: 검증"]
    verifyRelease --> promote["관리자: 최신 추천 승격"]
    promote --> launch["허용된 PC: 설치와 실행"]
```

**어떤 명령을 어디서 실행하는지는 [기능 지도·단계별 실행 안내](docs/reference/feature-workflow.md)부터 읽으세요.** 최초 준비 A, 새 버전 배포 B, 사용자 실행 C, 문제 해결 D로 나누고 각 단계의 담당자·명령·정상 결과를 제공합니다. 배포 서버가 사용자 PC에 자동 설치를 밀어 넣는 방식은 아닙니다.

## 빌드·검증

.NET 8 SDK, 저장소 루트 기준:

```text
dotnet restore UeDtLauncher.sln
dotnet build UeDtLauncher.sln -c Release --no-restore
dotnet test src/UeDtLauncher.Tests/UeDtLauncher.Tests.csproj -c Release --no-build
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj -c Release -r win-x64 --self-contained true -o publish/client-win-x64
dotnet publish src/UeDtLauncher.Agent/UeDtLauncher.Agent.csproj -c Release -r win-x64 --self-contained true -o publish/agent-win-x64
dotnet publish src/UeDtLauncher.DistributionServer -c Release -r linux-x64 --self-contained true -o publish/distribution-server
```

- Linux 클라이언트/Agent는 `-r linux-x64`로 생성합니다. 네이티브 의존성이 있으므로 출력 폴더 전체를 배치합니다.
- 기본 빌드는 `LauncherEdition=General`입니다. 개발자는 `dotnet publish src/UeDtLauncher -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:LauncherEdition=Developer`로 생성하고 `--build-info`로 에디션을 확인합니다. Developer 배포에는 공통 runtime-host인 일반 실행 파일도 포함해야 합니다.
- `tools/publish_runtime_cohort.py --output <새폴더> --rid win-x64`는 두 EXE·Agent·서버와 두 클라이언트 ZIP을 함께 생성합니다. 기본 소스는 HEAD이며 `--source-ref <커밋>`으로 바꿀 수 있습니다. 선택한 커밋을 게시본에 내장하고 실행 가능한 런처의 `--build-info`와 대조합니다. `cohort.json`에 소스 커밋·입력/파일 hash·에디션·SDK와 성공 여부를 기록합니다. 합성 시험 앱은 별도 폴더에 게시합니다.
- 문서 구조·링크는 `python3 tools/check-documentation.py`, 개선 대장 집계는 `python3 tools/check-improvement-ledger.py`로 검사합니다. 개발 규칙은 [AGENTS](AGENTS.md)를 따릅니다.

## 처음 준비할 것

1. Linux 서버에 DistributionServer·nginx·systemd 설정. 신규 사내 HTTP는 request-signature-v1을 명시합니다.
2. 서버 배포 서명키를 준비하고 배포 서명 공개키를 PC 관리자에게 전달합니다.
3. 사내 HTTP PC는 `enroll prepare`로 공개키 묶음을 만들고, 서버 관리자가 `client-enroll`로 PC별 IP와 허용 트랙을 등록합니다. PC 개인키는 보호 저장소에 남습니다. 기존 HTTPS/Bearer는 토큰 절차를 사용합니다.
4. PC 관리자가 `enroll finish`로 운영 설정과 진단 결과를 저장합니다. 관리형은 시스템 Agent 설정 폴더에 관리자 보호 공개키를 먼저 배치한 뒤 `finish`를 실행합니다. 관리자가 운영 설정을 업데이트 서비스에 반영한 뒤 `doctor --online`을 다시 실행합니다. 개별 설정은 `sample-config`로 생성할 수 있습니다.
5. 일반 또는 개발자 빌드의 실행 파일을 배포합니다. 관리형은 런처 옆 화면 설정에 프로젝트·트랙·OS·버전 선택을 지정합니다.

관리형 사용자 PC의 화면 설정은 다음처럼 생성합니다. 기존 파일은 `--force` 없이 덮어쓰지 않으며, 운영 URL·credential·공개키·보호 경로·clientProfile은 출력하지 않습니다. Agent 운영 설정은 별도로 준비합니다.

```powershell
.\UeDtLauncher.exe sample-config --mode managed-client --project-id ue-dt-simulator --platform windows-x64 --output .\launcher.config.json
# 개발자 exact 선택용 설정 예시 (빌드 에디션/서버 권한은 바뀌지 않음)
.\UeDtLauncher.Developer.exe sample-config --mode managed-client --project-id ue-dt-simulator --version-policy exact --version 1.2.0 --output .\developer.config.json
```

신규 사내 HTTP 명령은 [요청 서명 안내](docs/reference/intranet-auth.md), 기존 HTTPS/Bearer와 공통 게시 과정은 [통합 운영 가이드](docs/reference/distribution-workflow.md)를 따릅니다. 예시 IP·계정·공개키를 실제 값으로 바꾸세요.

## 새 버전 배포

| 순서 | 담당 | 작업 |
|---:|---|---|
| 1 | 개발자 | Windows/Linux ZIP을 각각 준비 |
| 2 | 개발자 | 쉬운 방법: `upload`로 ZIP 검사·새 작업 폴더의 외부 JSON 생성 |
| 3 | 개발자 | 같은 명령이 incoming/새업로드ID/에 두 파일 전송 후 최종 이름 확인. 직접 실행은 release-metadata + SSH/SCP |
| 3 이후 | 개발자 | 출력된 업로드 ID로 `upload status --wait`를 실행해 서버 접수·승인 대기 상태 확인 |
| 4 | 서버 | 파일 쌍·해시·ZIP 검사 후 승인 대기 |
| 5 | 관리자 | 서버 `console` 1·2에서 번호로 snapshot 확인 → 3승인·화면의 버전 재입력. 직접 실행은 list / inspect / approve |
| 6 | 서버 | 서명·디렉터리 생성 후 배포 목록 공개 |
| 7 | 개발자 | 허용된 exact 버전으로 검증 |
| 8 | 관리자 | exact 검증 후 `console` 6에서 트랙별 현재 추천·revision·사유 확인 및 버전 재입력. 직접 실행은 promotion inspect / promote |
| 9 | 사용자 | PC에 허용된 승격판으로 설치/업데이트 후 실행 |

```text
UeDtLauncher release-metadata --zip Windows.zip --project-id demo --version 1.2.0 --platform windows-x64 --payload-root Windows --entry-point Demo.exe --output release.json
```

Windows PowerShell에서 처음 한 번 접속 프로필과 프로젝트 프로필을 준비합니다. `--profile`에는 서버·계정·원격 경로·포트·identity 경로를 지정하고 `--project-profile`에는 ZIP 내부 경로와 배포할 프로젝트 값을 지정합니다. OpenSSH, 기존 known-host 등록과 업로드 권한이 필요합니다. 두 파일에 비밀번호·키 내용은 넣지 않습니다.

```powershell
.\UeDtLauncher.exe upload init --output upload.profile.json
.\UeDtLauncher.exe upload project-init --output ma0t10.project.json
# 접속 프로필과 프로젝트 프로필을 실제 대상에 맞게 편집
# 전용 호스트 키 파일을 쓸 경우 접속 프로필에 "knownHostsFile": "C:/deploy/ssh/known_hosts" 추가
.\UeDtLauncher.exe upload check --profile upload.profile.json
.\UeDtLauncher.exe upload --profile upload.profile.json --project-profile ma0t10.project.json --zip Windows.zip --version 1.4.0 --notes-file notes.md --dry-run
# 계획 확인 후 매 배포는 같은 명령에서 --dry-run을 빼고 실행
# 전송 완료 출력에 표시된 업로드 ID를 지정
.\UeDtLauncher.exe upload status --profile upload.profile.json --upload-id upload-001 --wait
```

upload는 키 인증 전용(BatchMode)으로 비밀번호를 묻지 않습니다. 최초 호스트 키 등록은 터미널 `ssh 계정@호스트`로 직접 확인합니다. `upload check`는 ssh/scp 존재·접속·incoming 디렉터리/쓰기 권한만 조회하며 원격 파일·폴더를 만들지 않습니다. 기본 한국어 요약과 stderr 진행 안내를 사용하고, 도구 연동은 `--format json`을 명시합니다. 성공 후 이번 작업 폴더만 정리하며 `--keep-work` 또는 실패 시 보존합니다. 종료255는 주소·포트·키 인증·호스트 키 확인, 전송 실패는 네트워크·디스크 점검 후 새 ID로 재시도합니다.

명령줄 값이 프로젝트 프로필보다 우선하고, 생략한 선택 값에는 기본값을 적용합니다. `--notes-file`은 BOM을 포함해 최대 64 KiB인 UTF-8 텍스트입니다. `--notes`와 함께 지정하면 종료2입니다. 노트 옵션은 `release-metadata`에서도 사용할 수 있습니다. 프로젝트 프로필의 필드와 작성 방법은 [기능 지도 B2](docs/reference/feature-workflow.md#b2-releasejson-만들기--개발-pc-powershell)와 [합성 예시](examples/ma0t10.project.json)를 참고하세요.

업로드 JSON의 로컬 경로는 홈 기준 `~/…` 또는 파일·폴더명으로 표시합니다. 접속 프로필 오류나 ZIP 내부 경로 불일치는 작업 사본을 만들거나 원격에 연결하기 전에 종료2로 안내합니다.

선택 필드 `knownHostsFile`에는 확인한 호스트 키를 등록한 기존 파일의 절대 경로를 지정합니다. Windows에서는 드라이브 문자가 있는 절대 경로를 사용하며 UNC와 장치 경로는 거부합니다. 생략하면 OpenSSH의 기본 호스트 키 파일을 사용합니다. 호스트 키 확인은 항상 엄격하게 수행하며 전용 파일 경로는 출력에서 가립니다. 전용 파일을 사용 중 SSH 종료가 255라면 서버 관리자가 제공한 지문과 대조한 뒤 지정한 파일을 갱신하세요. `--format json`은 실제 전송이 성공하면 `statusCommand`에 조회 명령을 제공하므로 실행 시 같은 접속 프로필을 지정하세요.

자동 업로드 ID는 릴리스 정보를 알아볼 수 있는 접두부를 길이에 맞춰 자르고 UTC 밀리초 시각·짧은 난수 접미부를 붙여 최대128자로 만듭니다. 프로젝트/버전 등 릴리스 메타데이터는 자르지 않습니다. 명시한 `--upload-id`는 그대로 검증하며 자동 수정하지 않습니다.

`upload status`는 SSH로 서버의 상태 파일을 조회합니다. 실서버 배치 예시는 `remoteIncoming=/dt/store/incoming`과 `remoteStatus=/dt/store/upload-status`입니다. `--wait`는 첫 상태 파일이 생기기 전에도 기본 5초마다 최대 30분간 조회하며 승인 대기·게시 처리 중·게시됨·현재 추천판에 도달하면 종료합니다. 이후 상태는 같은 ID로 다시 확인할 수 있습니다. 서버 관리자는 `/dt/store/upload-status/`에 업로더 읽기 권한과 서버 서비스 쓰기 권한을 부여합니다. [B3 상태 조회 옵션·권한 설정](docs/reference/feature-workflow.md#b3-서버로-두-파일-올리기--개발-pc)

서버 운영자는 Linux 터미널에서 `./UeDtLauncher.DistributionServer console --config /dt/config/server.json`을 실행합니다. 운영 콘솔은 터미널 입력만 허용하며 승인과 승격은 별도 확인입니다. [B2~B6 전체 명령·결과](docs/reference/feature-workflow.md)

payloadRoot는 ZIP 내부 프로그램 루트, entryPoint는 그 기준 경로입니다. ZIP을 변경하면 JSON도 다시 생성합니다. [업로드·승인 상세](docs/reference/feature-workflow.md)

서버 등록 위치는 `releases/<project>/<environment>/<channel>/<version>/<platform>/`입니다. 클라이언트는 필요한 개별 파일을 받습니다. 새 버전은 별도 설치 경로를 유지하면서 같은 배포 구분의 최근 설치 파일을 검증 후 복사할 수 있습니다. 원본을 공유하는 hard link나 공용 콘텐츠 캐시는 사용하지 않습니다.

## 최초 연결 점검과 문제 해결

| 실행 위치·담당 | 입력/행동 | 결과와 다음 조치 |
|---|---|---|
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --format text` | 파일을 생성/이전하지 않고 설정·키·권한을 점검. 온라인 연결은 미검증으로 표시 |
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --online --format text` | 인증·서명·현재 선택의 허용/추천 상태 확인. 필요한 anti-replay 신뢰 기록은 갱신 |
| 사용자·GUI | 설정 → 문제 해결 → 연결·준비 상태 점검 → 온라인 연결 점검 | 원인·사용자/관리자 조치·지원 ID 확인. 개발자 설정은 연결·준비 상태 점검을 바로 열 수 있음 |
| 관리자 조치 후·GUI | 다시 확인 / 같은 조회 다시 시도 | 설정 재읽기·조회만 수행. 설치/실행은 주 버튼에서 별도 수행 |
| 사용자·GUI | 문제 해결 | 이미 설치된 선택 버전의 손상만 복구. 정상 설치는 점검, 미설치는 설치 안내 |
| CLI·doctor·GUI | `rate-limited` / 429 | 잠시 후 다시 확인. Retry-After가 있으면 제한된 숫자 안내를 참고하고, 반복되면 관리자에게 요청 한도 확인 요청 |
| CLI·doctor·GUI | `stale-challenge` | 자동 재발급1회 뒤에도 실패한 상태. 관리자에게 PC 시계·서버 재시작·프록시 확인 요청 |
| CLI | 값 누락·다음 옵션·중복 옵션 | 작업 전 종료2. 기본 오류는 코드·안전한 원인·다음 조치·지원 ID이며 `--error-details`는 정제된 타입 체인만 출력 |
| Windows CLI | 직접 터미널 출력과 리디렉션 | 직접 실행은 부모 콘솔에 연결한 뒤 출력합니다. 실제 파일/파이프 출력은 UTF-8이며 무효 핸들에는 writer를 설치하지 않습니다. runtime-host 프로토콜과 GUI 시작은 별도 경로입니다 |

- Linux 명령은 `./UeDtLauncher`를 사용합니다. 기본 JSON 출력과 기존 0/1 종료 코드는 유지합니다.
- `healthy=true`와 종료0은 설치/실행 보장이 아닙니다. 준비도 `action-required`(조치 필요), `verification-pending`(추가 검증 필요), `checks-passed`(점검 완료)를 함께 확인하세요.
- `read-only-doctor-v1`을 지원하지 않는 구형 서비스는 갱신 안내로 처리합니다. 첫 승인만 있는 경우 관리자의 명시적 승격이 필요하며, 빈 배포 목록·401 인증 실패·403 권한 거부·서명 실패와 구분합니다.

명령·오류별 조치는 [클라이언트 가이드](docs/reference/guide-03-launcher-usage.md)를 따릅니다.

알림 영역을 지원하는 데스크톱에서 일반·개발자 런처 창을 닫으면 아이콘으로 숨깁니다. 아이콘 메뉴에서 창을 열거나 업데이트를 확인하거나 런처를 종료할 수 있습니다. 자동 확인은 기본 30분 간격의 조회이며 설치와 실행은 사용자 동작으로 시작합니다.

## 주요 동작 원칙

| 주제 | 핵심 | 상세 |
|---|---|---|
| 실행 중 변경 | 실행 중·Pending·Unknown 상태에서는 설치 변경을 차단합니다. GUI를 닫아도 프로그램은 종료되지 않으며, 추적 불명 상태는 관리자 점검과 명시적 정지 확인이 필요합니다. 런처와 Agent를 함께 갱신하세요 | [실행 안전성](docs/reference/runtime-safety.md) |
| 취소·이어받기 | 관리형은 `operation status\|cancel\|resume\|discard --id ID`, portable은 같은 명령에 `--config`를 추가합니다. 적용 전 취소는 안전 종료, commit 뒤 취소는 설치 완료·미시작 실행 생략입니다. 새 실행은 온라인 권한 확인이 필수이며 실행 중 앱을 원격 종료하지 않습니다 | [기능 지도](docs/reference/feature-workflow.md) |
| 자동 점검 | 관리자가 `scheduledCheck`를 명시한 경우에만 `scheduled-check` 한 회차로 조회만 수행합니다. 설치/실행/버전 전환은 하지 않습니다 | [서비스 모드](docs/reference/service-mode.md) |
| 서버 유지보수 | 정리(`retention`)는 관리자 확인형이며 공개판·승격·진행 자료를 지우지 않습니다. 백업은 오프라인, 복원은 최신 원본의 보안·승격·순번 기록과 대조 후 공개합니다 | [통합 운영 가이드](docs/reference/distribution-workflow.md) |
| 자격·요청 한도 | 만료는 관리자 명시(생략 시 무기한), 교체는 새 등록 → 실제 연결 → 이전 폐기 순서입니다. 요청 한도는 opt-in이며 초과 시 429를 반환합니다 | [요청 서명 안내](docs/reference/intranet-auth.md) |
| UE 사용자 데이터 | schema 3의 `runtimeData`를 명시적으로 켜면 실행 사용자·정확한 릴리스별 `-UserDir`와 실행별 `-abslog`를 준비합니다. 기본 비활성이며 버전 간 자동 복사·공유가 없고, repair/복원은 사용자 데이터를 되돌리지 않습니다. 앱이 다른 경로(예: DTCore CustomLogs)에 쓰면 강제 이동할 수 없습니다 | [클라이언트 가이드](docs/reference/guide-03-launcher-usage.md) |
| 설치본 | MSI/RPM은 정확한 실행별 package와 payload hash를 확인합니다. 회사 인수용 미실행 계획은 `tools/prepare-company-acceptance.py`로 생성하며 현재 호스트에 설치/서비스/계정을 만들지 않습니다 | [설치본·운영 인수](docs/reference/commercial-deployment.md) |

## 성능 기본값

- 다운로드·해시 기본 동시 처리 수는 각각 2개입니다. 설정으로 순차 처리(1개)로 되돌릴 수 있습니다.
- 서명된 배포의 새 버전 설치는 최근 3개 설치 후보에서 동일 파일을 재사용합니다. repair는 기존 다운로드 복구를 유지합니다.
- 서버 정책은 매 요청 다시 읽고 토큰도 매 요청 검사합니다. Catalog 서명 응답이나 권한 허용 결과를 캐시하지 않습니다.
- 접수 worker는 기본 1개, 선택적으로 2개입니다. 작업별 잠금·활성 임시 폴더 보호·진행/디스크 예상량을 제공합니다.

재현 시험 결과와 한계는 [성능 검증](docs/reference/archive/validation/performance-validation.md)에 있습니다. PERF-03의 10개 연결 지연 기준은 아직 미달이며, 재현 결과는 회사 성능 보장이 아닙니다.

## 검증 범위와 제약

- latest는 같은 환경/채널/OS에서 해당 PC에 허용된 승격 이력의 마지막 판입니다. 승인만으로 추천이 바뀌지 않고, 첫 버전도 promote가 필요합니다. 개발자는 허용된 미승격판을 exact로 선택할 수 있습니다. 첫 기동 전에 관리자가 빈 저장소를 준비합니다. 서버는 저장소 마운트 안에서 초기 HEAD를 게시합니다. `promotion migrate`는 초기화된 파일 저장소의 형식을 읽기 전용으로 확인합니다. [승격·파일 저장소 확인](docs/reference/distribution-workflow.md)
- 신규 프로젝트 게시가 PC 권한을 자동 부여하지 않습니다.
- cleanup은 임시 작업 폴더 대상이며 공개 버전·참조 원본은 삭제하지 않습니다.
- `Agent migrate --apply`의 동일 설치 공유는 현재 안전상 차단합니다. dry-run만 지원하며 소유권 이전은 별도 계획이 필요합니다. 기존 설치의 복사는 명시적 import-install입니다. UE 사용자 데이터는 실제 저장 경로에 맞춰 별도 보존합니다.
- 회사 백엔드 API는 인터페이스만 있고 현재는 파일 정책 구현입니다.
- 기설치 앱 원격 삭제·실행 금지는 범위 밖입니다.
- Agent 설치만으로 정기 업데이트가 시작되지는 않습니다. 현재 managed service-run은 한 회차 실행이며 주기 운영은 추가 설계가 필요합니다.
- 사내 HTTP 요청 서명은 암호화가 아닙니다. Windows LocalService·회사 RHEL/UE와 실제 코드서명 인증서 검증은 별도입니다.
- UI-01은 2026-09-29 사용자 합의로 1920×1080·OS 배율100%·앱 글자100%·고대비 끔 범위에서만 수용했습니다. 다른 해상도·DPI·큰 글자·고대비 최악 조건은 추가 테스트가 필요합니다.

운영 전 우선 인수: 실제 회사 인증서와 설치된 payload 서명, 서비스 계정 credential 접근, 실제 UE 데이터·복구·RHEL/망 경로. 서명 순서·실행 수명·소유권 보호는 구현돼 있지만 현장 인수와 장애 전수는 남아 있습니다. [보완 목록](IMPROVEMENTS.md)의 OPS-08/09·SEC-03을 확인하세요.

## 문서 지도

| 문서 | 관리 내용 |
|---|---|
| [README](README.md) | 제품 소개·구성·빌드·시작 방법 (이 문서) |
| [AGENTS](AGENTS.md) | 개발·검증·커밋·문서 갱신 규칙 |
| [개선 진행 현황](IMPROVEMENTS.md) | 완료 이력·항목별 진행률·남은 성능/사용자/UI/보안/운영 보완 |
| [최종 프로젝트 목표](PROJECT_GOALS.md) | 확정 목표·회사 운영 승인 조건·결정 필요 항목 |
| [참고 문서 모음](docs/reference/README.md) | 현행 상세 가이드 9개 |
| [아카이브](docs/reference/archive/README.md) | 검증 증거·이전 현황·구 운영 자료 (폐기가 아닌 보존) |

개선 문서의 퍼센트는 **추가 보완의 체크포인트 진척**이며, 기존 기능 구현도나 회사 운영 승인율이 아닙니다. 과거 서버 절차를 신규 설치 지침으로 사용하지 않습니다.
