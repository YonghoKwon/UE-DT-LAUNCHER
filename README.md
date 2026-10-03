# UE-DT Launcher

Unreal Engine Windows/Linux 패키징 프로그램을 사내 서버에 등록하고, 허용된 PC에서 설치·업데이트·실행하는 .NET 8 / Avalonia 배포 시스템입니다.

문서 점검: **2026-10-03**, 작업 기준: `codex/headless-operations`. Core 물리 이동, 취소·재개/온라인 실행, 자격 수명, 오프라인 백업/통제된 복원, 확인형 정리, 조회 전용 자동 점검을 구현했습니다. Windows/WSL 각635개와 실제 게시 CLI·console Agent·서버 통합을 확인했습니다. [최신 검증/남은 조건](docs/reference/archive/validation/headless-operations-validation.md), [이전 GUI 이력](docs/reference/archive/validation/readiness-validation.md). GUI 조작 중단·성능 미달·회사 인수 조건은 유지합니다.

**현재 판단: 합성 앱 기반 배포·설치·실행·복구는 활용 가능한 단계이며, 회사 정식 운영 인수는 미완료입니다.** 현재 수치와 남은 작업은 [개선 진행 현황](IMPROVEMENTS.md), 상세 명령은 [운영 문서 색인](docs/reference/README.md)을 확인하세요.

## 프로젝트 목표와 처음 읽을 안내

취소 후 표시는 작업 결과를 따릅니다. 적용 전 취소는 안전 종료를 기다리고, 이미 설치가 완료됐다면 실행 생략 또는 상태 재확인 안내를 제공합니다. 실행이 시작되기 전 티켓만 해당 시도에 맞춰 중단할 수 있습니다.

현재 후속 작업은 `codex/operations-hardening-closure`에서 진행합니다. 시험 root 소유/경로·게시본 확인, 비밀 stdout 제외, 단계별 결과 기록을 보강했습니다. [후속 검증](docs/reference/archive/validation/operations-closure-validation.md).

현재 **완료6·부분19·대기3(총28, 열린22)** 항목입니다. 주요 headless 구현은 진행했지만 GUI/음성·장애 전수·원격 CI·회사 인수 및 PERF-03 미달은 남아 있습니다. [작업별 명령 표](docs/reference/feature-workflow.md)를 따라 실행하고, 이 숫자를 제품 전체 완성률로 해석하지 마세요.

성능 재측정은 요청 실패0·90% 콘텐츠 재사용을 유지했지만 10연결 지연·시간 목표가 미달해 PERF-03을 완료로 올리지 않았습니다. [최종 수치와 조건](docs/reference/archive/validation/headless-performance-results.json). 테스트 활용과 회사 성능/운영 수용을 구분하세요.

자동 점검은 `scheduledCheck: {"enabled": true, "intervalSeconds": 3600}`처럼 관리자가 명시한 경우에만 `scheduled-check --config ...` 한 회차로 수행합니다. Catalog·설치 상태 조회만 하고 설치/실행/버전 전환은 하지 않습니다. Windows/Linux 비활성 템플릿은 저장소에 있으며 이 PC에는 등록하지 않았습니다.

정리: `retention inspect` → `retention plan --jobs 실패ID --output 계획.json` 또는 `--temporary processing/선택폴더` → 서버/watch 중지 → `retention apply --plan 계획.json --confirm`. 공개판·승격·진행 자료는 지우지 않습니다. 계획/파일이 바뀌면 새 확인이 필요하며 완료 계획으로 재생성 자료를 삭제하지 않습니다.

오프라인 서버 유지보수: `backup plan/create --output 새폴더/verify --backup 폴더`, `restore plan/stage --backup 폴더 --target 빈폴더/activate --target 폴더 --confirm`. 서버/watch/작업자를 먼저 정상 중지하세요. 복원은 최신 원본의 보안·승격·순번 기록과 파일을 확인해야 공개되며, 원본 완전 유실 복구는 이번 범위가 아닙니다. private signing key는 별도로 보관합니다.

자격 관리: `token-list`, `token-revoke-id --id 관리ID`, `token-issue PC --expires-at ISO시각`, `client-key add ... --expires-at ISO시각`. 생략 시 무기한이며 기존 자격을 자동 삭제하지 않습니다. 교체는 새 등록 → 실제 연결 → 이전 폐기 순서입니다. server.json의 `maxApiRequestsPerSecond`, `maxConcurrentDownloads`는 null(기본 비활성) 또는 명시한 양수입니다.

취소·이어받기: CLI `run`은 작업 ID를 출력합니다. 관리형은 `operation status|cancel|resume|discard --id ID`, portable은 같은 명령에 `--config 설정.json`을 추가합니다. 취소 요청 후 작업·안전한 복구 종료를 기다리세요. 이어받기는 온라인 권한·Manifest를 다시 확인하며 `performance.resumeCacheBytes`를 명시한 경우에만 지속 캐시를 사용합니다. 새 프로그램 실행도 온라인 확인이 필수이며 기존 실행 중 앱을 원격 종료하지 않습니다.

런타임 엔진·보안·transaction·IPC 소스는 이제 `src/UeDtLauncher.Core/`에 직접 위치합니다. 링크 컴파일을 제거했으며 기존 CLI/IPC 계약은 유지합니다.

Headless 후속 작업은 `codex/headless-operations`에서 진행 중입니다. 마우스·키보드·실제 GUI 검증 중단을 유지하며 게시 CLI/console Agent/서버를 사용합니다. [묶음별 검증](docs/reference/archive/validation/headless-operations-validation.md). 측정 도구는 HTTP 요청 서명과 명시적 승격을 사용하도록 갱신했으며 운영 완료나 성능 목표 달성을 뜻하지 않습니다.

최종 목표는 **Unreal Engine DT 프로그램의 패키징 결과를 안전하게 배포하고, 회사에서 안정적으로 설치·업데이트·실행·복구할 수 있는 배포 시스템**입니다. 목표는 확정됐지만 회사 운영 승인 조건을 모두 충족한 상태는 아닙니다.

```mermaid
flowchart LR
    package["개발자: ZIP와 외부 JSON"] --> inspect["서버: 자동 검사"]
    inspect --> approve["관리자: 승인"]
    approve --> publish["서버: 서명과 공개"]
    publish --> verifyRelease["개발자: 검증"]
    verifyRelease --> promote["관리자: 최신 추천 승격"]
    promote --> launch["허용된 PC: 설치와 실행"]
```

**어떤 명령을 어디서 실행하는지 알고 싶다면 [기능 지도·단계별 실행 안내](docs/reference/feature-workflow.md)부터 읽으세요.** 최초 준비 A, 새 버전 배포 B, 사용자 실행 C, 문제 해결 D로 나누고 각 단계의 담당자·명령·정상 결과를 제공합니다. 배포 서버가 사용자 PC에 자동 설치를 밀어 넣는 방식은 아닙니다.

## 관리 문서 4개

| 문서 | 관리 내용 |
|---|---|
| [README](README.md) | 현재 기능·사용 흐름·시작 방법 |
| [AGENTS](AGENTS.md) | 개발·검증·커밋·문서 갱신 규칙 |
| [개선 진행 현황](IMPROVEMENTS.md) | 완료 이력·항목별 진행률·남은 성능/사용자/UI/보안/운영 보완 |
| [최종 프로젝트 목표](PROJECT_GOALS.md) | 확정한 DT 배포 시스템 목표·회사 운영 승인 조건·미정 세부 정책 |

개선 문서는 남은 항목만 모은 것이 아니라 완료·부분 진행·대기를 함께 관리합니다. 퍼센트는 **추가 보완의 체크포인트 진척**이며, 기존 기능 구현도나 회사 운영 승인율이 아닙니다. 최신 집계는 해당 문서 한 곳에서 확인합니다.

## 최초 연결 점검과 문제 해결

현재 GUI 자동 조작은 사용자 요청으로 중단 상태입니다. 구현·이전 게시본의 실제 통과·최신 후보 재검증을 구분하며, 재개 후 남은 범위는 [단일 GUI 수용 체크리스트](docs/reference/guide-03-launcher-usage.md#보류-중인-gui-수용-체크리스트)를 따릅니다.

| 실행 위치·담당 | 입력/행동 | 결과와 다음 조치 |
|---|---|---|
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --format text` | 파일을 생성/이전하지 않고 설정·키·권한을 점검. 온라인 연결은 미검증으로 표시 |
| PC 관리자·CLI | `UeDtLauncher.exe doctor --config launcher.config.json --online --format text` | 인증·서명·현재 선택의 허용/추천 상태 확인. 필요한 anti-replay 신뢰 기록은 갱신 |
| 사용자·GUI | 설정 → 연결·준비 상태 점검 → 온라인 연결 점검 | 원인·사용자/관리자 조치·지원 ID 확인. 진단은 설치 상태나 주 버튼을 정상으로 덮어쓰지 않음 |
| 관리자 조치 후·GUI | 다시 확인 / 같은 조회 다시 시도 | 설정 재읽기·조회만 수행. 설치/실행은 주 버튼에서 별도 수행 |
| 사용자·GUI | 문제 해결 | 이미 설치된 선택 버전의 손상만 복구. 정상 설치는 점검, 미설치는 설치 안내 |

Linux 명령은 `./UeDtLauncher`를 사용합니다. 기본 JSON 출력과 기존 0/1 종료 코드는 유지합니다. `healthy=true`와 종료0은 설치/실행 보장이 아닙니다. 준비도 `action-required`(조치 필요), `verification-pending`(추가 검증 필요), `checks-passed`(점검 완료)를 함께 확인하세요. 관리형 사용자 저장 경로의 실제 쓰기 검사는 사용자 runtime-host가 실행 직전에 수행합니다.

Agent가 확인한 대상·준비도·검사 상태가 누락되면 미검증 근거를 합성 결과에 보존합니다. 요청 대상을 응답에 보충해 정상으로 만들지 않습니다. 다른 대상·null 검사 목록·모순된 성공은 `diagnostic-response-invalid`로 안내하며, 원래 설정 실패의 코드/지원 ID는 유지합니다.

관리형은 업데이트 서비스가 보호 설정과 credential을 검사하며 portable은 Agent 없이 점검합니다. `read-only-doctor-v1`을 지원하지 않는 구형 서비스는 갱신 안내로 처리합니다. 첫 승인만 있는 경우 관리자의 명시적 승격이 필요하며, 빈 배포 목록·401 인증 실패·403 권한 거부·서명 실패와 구분합니다. [명령·오류별 조치](docs/reference/guide-03-launcher-usage.md)

그 외 자료는 [참고 문서 모음](docs/reference/README.md)에 있습니다. 현재 가이드 9개와 색인은 `docs/reference/`, 검증 이력·중복 입문/구 운영 자료는 그 아래 `archive/`에 보존합니다. 아카이브로 옮긴 검증 증거를 폐기한 것은 아닙니다. 과거 서버 절차를 신규 설치 지침으로 사용하지 않습니다.

## 현재 구현

2026-09-29에는 합성 demo 대신 **실제 ma0t10_dt UE5.3 Windows Development 패키지**로 ZIP/외부 JSON 접수·서명 게시·GUI 설치/실행·런처 종료 후 UE 수명 유지·정상 종료를 확인했습니다. 328개 Manifest 파일 해시가 일치했습니다. 실제 UE 버전 업데이트/복구·Linux/RHEL·회사 서비스 계정은 별도 미검증이며, 설치 경로에 쓰는 CustomLogs는 추가 보완이 필요합니다. [실제 UE 시험·명령·제한](docs/reference/archive/validation/real-ue-package-validation.md)

현재 보완 소스는 **Windows/WSL 각 510개 통과, Release 경고·오류 0**입니다. 변경 없는 소스로 고정한 `8ce5060`의 Windows/Linux publish와 Linux HTTP 요청 서명+Agent·HTTPS/Bearer+nginx E2E도 통과했습니다. 관리형·portable의 정확한 선택/설치/runtime 표시, 최초 연결 실패 재시도와 복원 후 상태 재확인, 제목 글자 배율·작은 화면·포커스를 보완했습니다. Portable은 Agent 없이 `로컬 모드`로 동작하고, 문제 해결은 설치된 손상 대상만 복구하며 미설치 버전을 자동 설치하거나 앱을 실행하지 않습니다.

새 고정 후보의 portable v1→v2 설치/실행·파일 해시·v1 보존·창 종료 후 자식 수명·실행 중 변경 버튼 차단을 확인했습니다. 개발자 손상 복구, 정상 백업 생성 후 복원 취소/적용, 변경 preview 거부와 재확인, 서버 재연결 후 정상 설치의 점검 전용 문제 해결도 통과했습니다. 같은 후보의 **관리형 v1 최초 설치/실행·3파일 해시·GUI 종료/재실행 중 동일 자식 수명·정상 종료 후 실행 가능 복귀**도 확인했습니다. 관리형 v2 업데이트/실행·3파일 해시·v1 보존·생존 중 개발자 변경 버튼 차단·정상 종료도 확인했습니다. 관리형 개발자 손상 복구와 정상 상태에서 추가 repair로 만든 백업20260928145009의 3파일 해시도 통과했습니다. 해당 백업의 복원 취소/적용·v1 보존·완료 제목과 최신 상태 복귀도 확인했습니다. 관리형 변경 preview 거부/새 확인 요구와 서비스 재연결 후 정상 설치의 점검 전용 문제 해결도 통과했습니다. 일반 화면 손상 복구 등 남은 오류 조합과 전체 DPI 수용은 남았습니다. 미설치 재연결 첫 클릭 복귀·설치/실행0건은 작업 중 게시본의 별도 사전 시험입니다. 서로 다른 게시본의 성공을 합산하지 않습니다. [게시본별 실제 증거와 남은 조건](docs/reference/archive/validation/ui-acceptance-finalization.md)

기존 GUI 검증 이력에서는 일반 GUI 설치·실행·실행 중 버튼 차단·창 종료/재실행·자식 정상 종료, 개발자 선택/취소·v2 파일 복구·같은 설치의 정상 backup rollback을 실제로 확인했습니다. [GUI 실행 이력](docs/reference/archive/validation/managed-gui-safety-validation.md), [runtime 장애 이력](docs/reference/archive/validation/runtime-safety-completion-validation.md)

**UI-01은 100%(1920×1080의 현재 검증 범위 한정), UI-02·03은 75% 유지**합니다. 2026-09-29 사용자 합의로 이번 화면 수용을 1920×1080으로 한정했습니다. 고정 후보 `8ce5060`의 관리형·portable 일반/개발자 화면을 OS 배율100%·앱 글자100%·고대비 끔에서 실제 확인한 근거를 사용합니다. 남은 기능/오류·개발자 실행 확인(UI-02), 접근성·내레이터 음성(UI-03), 회사 UE/RHEL·서비스 계정·정식 운영 인수는 별도입니다.

> **추가 테스트 필요:** 1366×768·1280×720 등 다른 해상도. 1920×1080을 포함한 OS 배율125/150%, 큰 글자·고대비 최악 조건도 아직 미검증이며 후속으로 남깁니다. 이번 100%는 모든 화면 환경의 지원 보장이 아닙니다.

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

GUI의 general/developer는 표시 정책이지 다운로드 권한이 아닙니다. 기존 HTTPS/Bearer와 명시적인 schema 3 사내 HTTP/요청 서명을 지원하며, 둘 다 Metadata 서명·해시·권한 검증을 유지합니다. HTTP/Bearer나 무인증으로 자동 후퇴하지 않습니다. nginx 뒤 API는 loopback에만 바인딩하고 공개 정적 경로와 혼합하지 않습니다.

일반 GUI는 자동 점검만 하며 설치는 사용자 클릭 후 수행합니다. 무인 서비스 자동 업데이트와 구분합니다. 관리형 런처 자체 갱신은 MSI/RPM, 게임 콘텐츠 갱신은 Agent 책임입니다. Portable은 현재 계정의 로컬 엔진과 runtime 기록을 사용하며 Agent 연결을 요구하지 않습니다. 두 모드 모두 실행 중·Pending·Unknown 상태에서는 설치 변경을 차단합니다.

## 실행 중 변경과 구형 클라이언트

프로그램이 실행 중이면 정상 종료 후 런처에서 다시 확인하세요. GUI를 닫아도 프로그램은 종료되지 않습니다. 추적 불명 상태는 관리자 점검과 명시적 정지 확인이 필요하며 PID 파일 삭제로 우회하지 않습니다. 런처와 Agent를 함께 갱신하세요. 구형 IPC v1의 조회는 유지하지만 실행 추적 capability 없는 변경 요청은 거부합니다. 기존 직접 EXE 바로가기는 관리자가 이전하고, 새 바로가기는 정확한 버전을 선택한 런처를 호출합니다. [상태별 명령](docs/reference/runtime-safety.md)

서명·권한·해시·실행 수명 안전성을 유지합니다. 사내 HTTP 요청 서명은 암호화가 아니며, Windows LocalService·회사 RHEL/UE와 실제 코드서명 인증서 검증은 별도입니다. [회사 운영 승인 조건](PROJECT_GOALS.md)을 모두 통과하기 전 정식 운영 완료로 보지 않습니다.

## UE 사용자 데이터 분리 — 선택 기능

추가 보강: 실행 티켓의 확정 릴리스·설치 경로와 데이터 계획을 대조하고, 사용자 host의 credential 경로도 보호합니다. Linux 실제 데이터 폴더는 sticky bit가 있어도 공용 쓰기를 거부하며, UserDir와 로그 폴더 양쪽에서 쓰기·flush를 확인합니다. [이번 후속 결과·직접 조작 순서](docs/reference/archive/validation/runtime-data-acceptance-completion.md)

schema 3의 `runtimeData`를 명시적으로 활성화하면 런처가 실행 사용자·정확한 릴리스별 `-UserDir`와 실행별 `-abslog`를 준비합니다. 기존 설정은 기본 비활성화이며 자동 이전하지 않습니다. 관리형 운영 값은 GUI가 아니라 Agent 설정에서 결정합니다.

- 기본 루트: Windows 실행 사용자의 LocalAppData, Linux 실행 사용자의 XDG 데이터 폴더.
- 버전별 세이브·설정은 공유하지 않습니다. 새 버전에 이전 설정이 자동 복사되는 기능은 없습니다.
- 프로그램 repair/backup 복원은 외부 사용자 데이터를 되돌리거나 정리하지 않습니다.
- **앱 내부가 다른 경로에 쓰면 UE 인수로 강제 이동할 수 없습니다.** 기존 ma0t10_dt의 DTCore CustomLogs는 설치 폴더 쓰기가 남아 있으며, 이번에는 UE·DTCore를 수정하지 않았습니다.

[정확한 설정과 권한 조건](docs/reference/guide-03-launcher-usage.md), [이번 구현·검증·GUI 대기 기록](docs/reference/archive/validation/real-ue-data-safety-validation.md)을 확인하세요. 이번 후보의 실제 UE 업데이트·복구·복원은 Windows 입력 접근 거부로 아직 미실행입니다. CLI 시험을 GUI 수용으로 대체하지 않았습니다.

## 처음 준비할 것

1. Linux 서버에 DistributionServer·nginx·systemd 설정. 신규 사내 HTTP는 request-signature-v1을 명시합니다.
2. 서버 서명 개인키와 PC별 IP/배포 권한 등록. 공개키만 PC에 배포.
3. PC별 개인키를 보호 저장하고 공개키만 서버에 등록합니다. 기존 HTTPS/Bearer 환경은 기존 토큰 절차를 유지합니다.
4. PC에 런처·Agent 설치, sample-config로 보호된 운영 설정을 생성하고 doctor로 확인합니다.
5. 런처 옆 설정에서 general/developer 화면 선택.

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

## 성능 설정과 현재 결과

- 다운로드·해시 기본 동시 처리 수는 각각 2개입니다. 설정으로 순차 처리(1개)로 되돌릴 수 있습니다.
- 서명된 배포의 새 버전 설치는 최근 3개 설치 후보에서 동일 파일을 재사용합니다. repair는 기존 다운로드 복구를 유지합니다.
- 서버 정책은 매 요청 다시 읽고 토큰도 매 요청 검사합니다. Catalog 서명 응답을 캐시하지 않습니다.
- 접수 worker는 기본 1개, 선택적으로 2개입니다. 작업별 잠금·활성 임시 폴더 보호·진행/디스크 예상량을 제공합니다.

Windows 재현 시험에서 작은 파일 최초 설치는 중앙값 10.96초→5.98초, 다음 버전 설치는 11.01초→3.29초였고 콘텐츠 전송량은 90% 줄었습니다. 서버 30개 연결의 혼합 API p95는 833.44ms→81.23ms, 측정 요청 실패는 32→0이었습니다. **10개 연결의 혼합 p95는 5.43ms→24.66ms로 악화되어 PERF-03은 부분 완료**입니다. 회사 성능 보장이나 운영 배포 승인이 아닙니다. [설정·재현·한계](docs/reference/archive/validation/performance-validation.md)

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

Linux 클라이언트/Agent는 `-r linux-x64`로 생성합니다. 네이티브 의존성이 있으므로 출력 폴더 전체를 배치합니다. 설치·실행은 [클라이언트 가이드](docs/reference/guide-03-launcher-usage.md)를 따릅니다.

## 검증 범위와 제약

2026-10-03 진단 후속: Windows/WSL 각587개, Release 경고·오류0, 게시 HTTP 요청 서명·HTTPS/Bearer·구형 Agent·지원 ZIP 검증을 확인했습니다. GUI의 상세 실제 통과는 [게시본별 표](docs/reference/archive/validation/readiness-validation.md)에 있습니다. 사용자 요청으로 마우스·GUI 자동 검증은 잠시 중단했으며 새 후보 전체 GUI 수용을 이전 후보 성공과 합산하지 않습니다. USER-02·UI-02는75%를 유지합니다.

2026-09-12 [기존 실행 기록](docs/reference/archive/validation/distribution-validation.md): Windows 210/210, WSL Ubuntu 210/210, Release 경고·오류 0. 테스트 프로그램으로 HTTPS 배포와 Windows GUI/Agent·Linux CLI 설치·실행을 확인했습니다.

2026-09-28 성능 작업: Windows/Linux 각각 전체 293개 테스트 통과, Python 측정 도구 계약 테스트 16개 통과. Release build/publish 및 실제 Windows 일반·개발자 GUI/Agent, Linux CLI/Agent 실행을 확인했습니다. Linux nginx HTTPS E2E의 작은 파일은 통과했지만 WSL1 nginx의 1MiB 응답 중단이 관측되어 큰 파일 GUI 검증은 격리된 Windows HTTPS 프록시로 분리했습니다. 실제 회사 RHEL·UE 패키지·IP/CA·설치본 수명주기는 별도 검증해야 합니다. 코드서명 없는 개발 산출물을 운영용 서명 제품으로 배포하지 않습니다.

- latest는 같은 환경/채널/OS에서 해당 PC에 허용된 승격 이력의 마지막 판입니다. 승인만으로 추천이 바뀌지 않고, 첫 버전도 promote가 필요합니다. 개발자는 허용된 미승격판을 exact로 선택할 수 있습니다. [승격·기존 서버 이전](docs/reference/distribution-workflow.md)
- 신규 프로젝트 게시가 PC 권한을 자동 부여하지 않습니다.
- cleanup은 임시 작업 폴더 대상이며 공개 버전·참조 원본은 삭제하지 않습니다.
- `Agent migrate --apply`의 동일 설치 공유는 현재 안전상 차단합니다. dry-run만 지원하며 소유권 이전은 별도 계획이 필요합니다. 기존 설치의 복사는 명시적 import-install입니다. UE 사용자 데이터는 실제 저장 경로에 맞춰 별도 보존합니다.
- 회사 백엔드 API는 인터페이스만 있고 현재는 파일 정책 구현입니다.
- 기설치 앱 원격 삭제·실행 금지는 범위 밖입니다.
- Agent 설치만으로 정기 업데이트가 시작되지는 않습니다. 현재 managed service-run은 한 회차 실행이며 주기 운영은 추가 설계가 필요합니다.

운영 전 우선 보완: MSI 내부 EXE 서명 순서, 동명 프로세스 식별, Linux credential 소유권. [보완 목록](IMPROVEMENTS.md)의 OPS-08/09·SEC-03을 확인하세요. 기존 테스트 통과만으로 이 항목들이 해결됐다고 판단하지 않습니다.
