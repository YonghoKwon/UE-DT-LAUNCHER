# UE-DT Launcher

Unreal Engine Windows/Linux 패키징 프로그램을 사내 서버에 등록하고, 허용된 PC에서 설치·업데이트·실행하는 .NET 8 / Avalonia 배포 시스템입니다.

최종 목표는 **Unreal Engine DT 프로그램의 패키징 결과를 안전하게 배포하고, 회사에서 안정적으로 설치·업데이트·실행·복구할 수 있는 배포 시스템**입니다. 목표는 확정됐지만 회사 운영 승인 조건을 모두 충족한 상태는 아닙니다. 목표·승인 조건·미정 정책은 [PROJECT_GOALS](PROJECT_GOALS.md)에 있습니다.

## 현재 상태

- **합성 앱 기반 배포·설치·실행·복구는 로컬에서 활용 가능한 단계이며, 회사 정식 운영 인수(G1~G6)는 미완료입니다.**
- 실제 회사 인증서·서비스 계정·RHEL·실제 UE 업데이트/복구·MSI/RPM 설치 수명주기·원격 CI는 아직 검증하지 않았습니다.
- 항목별 진행률은 [개선 대장](IMPROVEMENTS.md), 최신 후보의 테스트·GUI 수용 수치는 [자동 생성 진행표](docs/reference/archive/validation/local-completion-progress.md), 남은 GUI 사례는 [단일 GUI 수용 체크리스트](docs/reference/guide-03-launcher-usage.md#현재-gui-수용-체크리스트)를 따릅니다. 진행률 평균을 제품 완성률로 해석하지 않습니다.
- 날짜·후보별 이전 검증 기록은 [현황 이력](docs/reference/archive/history/status-log.md)에 있습니다.

## 구성 요소

| 영역 | 내용 |
|---|---|
| 접수 | 업로드 폴더별 ZIP + 외부 release.json, 크기·SHA-256·안전한 ZIP 검사, SQLite 작업 기록 |
| 게시 | 관리자 승인, 디렉터리 자동 생성, Manifest·서명, 완료 전 비공개, 중단 게시 재개 |
| 권한 | 실제 IP/CIDR + PC 요청 서명(기존 HTTPS는 Bearer), 프로젝트·환경·채널·선택적 버전 제한, 기본 거부 |
| 전송 | 인증된 목록·Manifest·이미지·파일·Range, 서명·해시 검증, 재시도·이어받기 |
| 설치 | 정확한 릴리스, 버전별 설치·상태·잠금 분리, 실행 중/불명 상태의 update·repair·rollback·transaction 복구 차단 |
| 실행 | 사용자 세션 runtime-host, Windows Job / Linux x64 subreaper, 표준 후손 종료까지 추적, 자동 kill 없음 |
| 일반 화면 | 포스코DX 밝은 화면, 자동 확인·상태별 주 버튼·하단 진행·지원 ID, 이미지/fallback |
| 개발자 화면 | 포스코DX 다크 화면, 허용 배포/정확한 버전 선택, 접히는 유지보수·복사 가능한 정보/로그 |
| 운영 | Windows/Linux Agent·IPC·CLI, 진단 내보내기, 무인 서비스 모드, MSI/RPM 제작 구성 |

- 일반 `UeDtLauncher.exe`와 개발자 `UeDtLauncher.Developer.exe`는 별도 빌드이며, 설정으로 GUI 종류를 바꾸지 못합니다. 두 빌드의 CLI는 공통이고 다운로드 권한은 서버가 결정합니다.
- **관리형**은 Agent(화면 표기: 업데이트 서비스)가 보호 설정·credential·Manifest·설치 상태를 검사하고 설치하며, GUI/CLI는 화면 설정의 정확한 선택만 사용해 사용자 세션에서 실행합니다. **Portable**은 Agent 없이 현재 계정의 로컬 엔진과 runtime 기록을 사용합니다.
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
- `tools/publish_runtime_cohort.py --output <새폴더> --rid win-x64`는 두 EXE·Agent·서버와 두 클라이언트 ZIP을 함께 생성합니다. 기본은 HEAD의 커밋된 소스 snapshot이며, `--source-ref <커밋>`을 지정하면 제품 입력/역할별 파일 hash·에디션·SDK와 성공 여부를 `cohort.json`에 기록합니다. 시험 합성 앱은 별도 폴더에만 게시하며 배포 ZIP에 넣지 않습니다.
- 문서 구조·링크는 `python3 tools/check-documentation.py`, 개선 대장 집계는 `python3 tools/check-improvement-ledger.py`로 검사합니다. 개발 규칙은 [AGENTS](AGENTS.md)를 따릅니다.

## 처음 준비할 것

1. Linux 서버에 DistributionServer·nginx·systemd 설정. 신규 사내 HTTP는 request-signature-v1을 명시합니다.
2. 서버 서명 개인키와 PC별 IP/배포 권한 등록. 공개키만 PC에 배포.
3. PC별 개인키를 보호 저장하고 공개키만 서버에 등록합니다. 기존 HTTPS/Bearer 환경은 기존 토큰 절차를 유지합니다.
4. PC에 런처·Agent 설치, sample-config로 보호된 운영 설정을 생성하고 doctor로 확인합니다.
5. 일반 또는 개발자 빌드의 실행 파일을 배포하고, 런처 옆 설정에는 서버·프로젝트·표시 정보만 지정.

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
| 2 | 개발자 | release-metadata로 ZIP 옆 외부 JSON 생성 |
| 3 | 개발자 | incoming/새업로드ID/에 두 파일을 .uploading 이름으로 전송 후 최종 이름으로 변경 |
| 4 | 서버 | 파일 쌍·해시·ZIP 검사 후 승인 대기 |
| 5 | 관리자 | list / inspect 확인 후 approve |
| 6 | 서버 | 서명·디렉터리 생성 후 배포 목록 공개 |
| 7 | 개발자 | 허용된 exact 버전으로 검증 |
| 8 | 관리자 | promotion inspect의 revision 확인 후 promote |
| 9 | 사용자 | PC에 허용된 승격판으로 설치/업데이트 후 실행 |

```text
UeDtLauncher release-metadata --zip Windows.zip --project-id demo --version 1.2.0 --platform windows-x64 --payload-root Windows --entry-point Demo.exe --output release.json
```

payloadRoot는 ZIP 내부 프로그램 루트, entryPoint는 그 기준 경로입니다. ZIP을 변경하면 JSON도 다시 생성합니다. [업로드·승인 상세](docs/reference/feature-workflow.md)

서버 등록 위치는 `releases/<project>/<environment>/<channel>/<version>/<platform>/`입니다. 클라이언트는 필요한 개별 파일을 받습니다. 새 버전은 별도 설치 경로를 유지하면서 같은 배포 구분의 최근 설치 파일을 검증 후 복사할 수 있습니다. 원본을 공유하는 hard link나 공용 콘텐츠 캐시는 사용하지 않습니다.

## 최초 연결 점검과 문제 해결

| 실행 위치·담당 | 입력/행동 | 결과와 다음 조치 |
|---|---|---|
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --format text` | 파일을 생성/이전하지 않고 설정·키·권한을 점검. 온라인 연결은 미검증으로 표시 |
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --online --format text` | 인증·서명·현재 선택의 허용/추천 상태 확인. 필요한 anti-replay 신뢰 기록은 갱신 |
| 사용자·GUI | 설정 → 연결·준비 상태 점검 → 온라인 연결 점검 | 원인·사용자/관리자 조치·지원 ID 확인. 진단은 설치 상태나 주 버튼을 정상으로 덮어쓰지 않음 |
| 관리자 조치 후·GUI | 다시 확인 / 같은 조회 다시 시도 | 설정 재읽기·조회만 수행. 설치/실행은 주 버튼에서 별도 수행 |
| 사용자·GUI | 문제 해결 | 이미 설치된 선택 버전의 손상만 복구. 정상 설치는 점검, 미설치는 설치 안내 |

- Linux 명령은 `./UeDtLauncher`를 사용합니다. 기본 JSON 출력과 기존 0/1 종료 코드는 유지합니다.
- `healthy=true`와 종료0은 설치/실행 보장이 아닙니다. 준비도 `action-required`(조치 필요), `verification-pending`(추가 검증 필요), `checks-passed`(점검 완료)를 함께 확인하세요.
- `read-only-doctor-v1`을 지원하지 않는 구형 서비스는 갱신 안내로 처리합니다. 첫 승인만 있는 경우 관리자의 명시적 승격이 필요하며, 빈 배포 목록·401 인증 실패·403 권한 거부·서명 실패와 구분합니다.

명령·오류별 조치는 [클라이언트 가이드](docs/reference/guide-03-launcher-usage.md)를 따릅니다.

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

- latest는 같은 환경/채널/OS에서 해당 PC에 허용된 승격 이력의 마지막 판입니다. 승인만으로 추천이 바뀌지 않고, 첫 버전도 promote가 필요합니다. 개발자는 허용된 미승격판을 exact로 선택할 수 있습니다. [승격·기존 서버 이전](docs/reference/distribution-workflow.md)
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
