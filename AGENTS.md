# 작업 지침

점검: 2026-09-28 / 작업 기준 codex/ui-acceptance-finalization. 저장소 전체에 적용합니다.

## 문서 관리 계약

- 루트 정본은 README.md(현재), AGENTS.md(규칙), IMPROVEMENTS.md(보완), PROJECT_GOALS.md(확정 목표·승인 조건·미정 정책) 4개입니다.
- 현재 상세 가이드 9개와 색인은 docs/reference/에 둡니다. 실행 증거는 docs/reference/archive/validation/, 통합된 중복 안내는 archive/guides/, 초기 자료는 기존 archive/에 보존합니다. archive/README.md에서 현재 대체 문서와 이력을 구분합니다.
- 검증 이력의 아카이브 이동은 증거 폐기를 뜻하지 않습니다. 날짜·환경·과거 미완료 문장을 임의로 최신화하지 말고, 현재 상태는 루트 정본에 반영합니다.
- 기능·설정·보안·CLI·UI 변경 시 관련 정본과 상세 가이드를 함께 갱신합니다. 같은 설정 전문을 중복 관리하지 않습니다.
- 구현됨/과거 테스트됨/이번 검증됨/회사 미검증을 구분하고 날짜·플랫폼·입력을 기록합니다.
- 최상위 목표는 사용자 확정 사항인 **회사에서 안정적으로 활용 가능한 Unreal Engine DT 배포 시스템**입니다. 세부 범위·회사 SLA·운영 정책은 PROJECT_GOALS의 미정 항목을 따릅니다. 제안이나 목표를 구현/운영 완료로 바꾸지 않습니다.
- 문서를 이동하면 상대 링크와 스크립트 내 참조도 확인합니다.
- IMPROVEMENTS는 완료 이력도 보존하는 진행 대장입니다. 항목별 진행률은 문서의 증거 기반 0/25/50/75/100% 단계로 표시하고, 상태·체크포인트·근거·남은 조건·집계를 함께 갱신합니다. 기존 기능이 있다는 이유로 추가 보완에 점수를 주거나 항목 평균을 제품 완성률로 사용하지 않습니다.

## 사용자 안내와 도식 관리

- 상세 기능 지도·명령 순서는 `docs/reference/feature-workflow.md`에서 관리합니다. A=최초 준비, B=새 버전 배포, C=사용자 실행, D=문제 해결 구분을 유지합니다.
- 단계마다 담당자·실행 위치·입력/명령·정상 결과를 적습니다. Windows PowerShell과 Linux 터미널, 예시값과 실제 회사값, 최초 설정과 반복 작업을 구분합니다.
- 도식은 실제 코드 기준으로 작성하며 수동 승인·사용자 클릭·자동 검사를 분리합니다. 없는 관리자 웹 화면·자동 승인·자동 UE 패키징을 그리지 않습니다.
- 관리형은 Agent가 설치하고 GUI/CLI가 사용자 세션에서 실행합니다. 실행 버튼을 오프라인 즉시 실행 보장으로 설명하지 않습니다.
- Mermaid 흐름과 명령 표는 함께 갱신합니다. 그림을 볼 수 없는 환경에서도 표만으로 순서를 따라갈 수 있게 합니다.
- 문서 검증에서는 링크·명령 옵션·노드/연결을 확인합니다. 회사 서버에 안내 명령을 실제 실행한 것으로 보고하지 않습니다.

## 코드 지도

| 위치 | 책임 |
|---|---|
| src/UeDtLauncher/Program.cs | GUI/CLI 명령 |
| src/UeDtLauncher/Gui/ | programmatic Avalonia View·ViewModel·시각 토큰 |
| src/UeDtLauncher.Core/ | 공용 assembly, Distribution 메타데이터·경로·이미지 |
| src/UeDtLauncher/*.cs | 엔진·보안·transaction·IPC 소스 일부. Core csproj가 링크 컴파일 |
| src/UeDtLauncher.Agent/ | Windows Service/Linux systemd Agent |
| src/UeDtLauncher.DistributionServer/ | SQLite 접수·승인·서명 게시·인증 API |
| src/UeDtLauncher.Tests/, tools/test-distribution-e2e.sh | 자동화·실제 프로세스 E2E |
| installer/windows/, packaging/linux/, .github/workflows/ | 설치본·nginx·서비스·CI |

## 보존할 경계

- ZIP + 외부 release.json을 비공개 snapshot으로 검사하고 관리자 승인 후 게시합니다. 업로드 프로그램을 서버에서 실행하지 않습니다.
- .uploading/한 파일만 도착한 상태를 게시하지 않습니다. 동일 릴리스 식별자 덮어쓰기를 허용하지 않습니다.
- 실제 IP/CIDR + PC 인증키(기존 HTTPS/Bearer는 토큰)를 목록·Manifest·이미지·파일·Range 모두에 적용합니다. 공개 정적 경로 우회를 만들지 않습니다.
- 요청 서명은 schema 3·별도 .keycred만 사용합니다. 개인키를 GUI에 공유하거나 임의 URL 서명 IPC를 만들지 않습니다. challenge는 monotonic 만료·재시작 무효화, 살아 있는 nonce는 용량 확보를 위해 제거하지 않습니다.
- Catalog 요청 결속은 실제 성공한 송신 요청과 비교한 후 해석/sequence 저장합니다. nginx 이중 인증과 권한 허용 캐시는 금지합니다. 같은 root의 인증 서버는 하나만 실행합니다.
- 기존 HTTPS/Bearer를 유지합니다. 사용자 승인한 schema 3 사내 HTTP는 요청 서명·재사용 차단·Catalog 요청 결속을 모두 요구하며 Bearer/무인증으로 후퇴하지 않습니다. Metadata 서명·해시·경로 검사를 완화하지 않습니다. 개인키·토큰·Authorization·Signature·challenge를 로그나 Git에 넣지 않습니다.
- GUI 프로필은 화면 정책입니다. Agent의 보호된 운영 설정·credential·서버 권한과 분리합니다. 정확한 선택을 다른 버전으로 몰래 대체하지 않습니다.
- 설치·상태·잠금·PID는 프로젝트/환경/채널/버전/OS별 격리입니다. 기존 설치·사용자 데이터는 승인 없이 삭제하지 않습니다.
- 일반 GUI는 자동 점검만 합니다. 설치는 사용자 동작, rollback은 확인 후 실행합니다. 무인 서비스와 구분합니다.
- IPC v1 framing·조회·streaming 의미를 보존합니다. runtime-supervision-v1 capability 없는 변경 요청은 업그레이드 안내로 거부합니다.
- 실행 시작과 모든 설치 변경은 InstallationMutationLease로 직렬화합니다. Prepare의 복구보다 먼저 검사하고 Running/LaunchPending/Unknown에서는 설치·state·backup·journal을 변경하지 않습니다.
- runtime 기록의 필수 필드·중복·상태별 관계를 검사합니다. 누락 필드를 enum 기본값 Quiescent로 인정하지 않습니다. inspect/dry-run과 거부된 복구 요청은 파일을 생성/변경하지 않습니다.
- 서비스 잠금 → 설치 ID 정렬 잠금 순서를 지킵니다. 단일 service snapshot에 시작 전 barrier를 기록하고 health 성공 기록 전에는 안전 상태로 풀지 않습니다. 기존 active/failure 기록은 명시적 확인 전 자동 통합하지 않습니다.
- 공유 InstallDir의 portable→관리형 migrate apply를 재활성화하지 않습니다. import는 잠금 후 metadata를 다시 읽고 staging을 재검증합니다. 중단 transaction이 있으면 직접 실행하지 않습니다.
- 테스트 전용 fault harness는 설치/공식 publish graph에 넣지 않습니다. 공개 CLI/환경변수로 장애 주입·가짜 종료·검사 우회를 제공하지 않습니다.
- 이름/PID만 보고 종료하거나 host 소멸/timeout을 Quiescent로 바꾸지 않습니다. runtime-host가 표준 후손 종료를 확인해야 하며 추적 불가 플랫폼에서 우회 실행하지 않습니다.
- 관리형 실행 기록은 Agent 소유입니다. 티켓은 private pipe로 전달하고 실제 OS peer/생성 식별자/시도에 결속합니다. GUI에 보호 state 쓰기 권한이나 임의 원격 실행 IPC를 추가하지 않습니다.
- service-run의 자동 kill·버전 handoff·health 실패 자동 rollback을 금지합니다. 수동 정지 확인은 관리자/portable 소유자 권한과 보존 기록을 요구하며 OS 종료 증거로 표시하지 않습니다.
- runtime-host는 악성 동일 사용자 격리 경계가 아닙니다. 외부 실행 broker·수동 EXE는 보장 범위 밖입니다. 테스트 정리는 직접 생성해 보유한 handle만 사용합니다.
- 병렬 작업 실패 시 형제 작업을 취소하고 모두 종료한 뒤 반환합니다. transaction 적용은 직렬로 유지합니다.
- 파일 재사용의 로컬 기록은 힌트이며, 인증된 대상 Manifest와 복사 결과 해시가 신뢰 기준입니다. 원본 수정·hard link를 금지합니다.
- DB schema 변경 전 백업, 작업별 OS 잠금, active_work 보호를 유지합니다. 잠금 파일을 삭제하거나 긴 ZIP I/O를 공용 잠금 안에 넣지 않습니다.

## UI 규칙

- programmatic View와 LauncherVisualTokens·ViewModel을 사용합니다. 현행 레이아웃은 MainWindowEnterprise, 대화창은 MainWindowAccessibility, 피드백은 MainWindowFeedback입니다.
- 일반은 밝은 POSCO DX 업무 화면, 개발자는 같은 브랜드의 다크 화면을 유지합니다. 공식 로고 원본/출처를 보존하며 임의 CI 재가공을 하지 않습니다.
- 글자 배율·앱 고대비는 사용자별 ui-preferences.json에만 저장합니다. UI 스레드에서 비동기 파일 저장을 동기 대기하지 않습니다. OS 고대비 요청을 우선합니다.
- GUI 시험은 새 fixture에 같은 소스의 GUI/Agent/서버/합성 앱을 게시하고 바이너리 hash·HEAD·소스 diff hash를 기록합니다. 기존 fixture와 설치를 덮어쓰지 않습니다.
- 최종 수용은 고정된 같은 게시본 묶음으로 수행합니다. 수정 후에는 영향받는 시험을 새 게시본에서 다시 실행하고 이전 후보 성공을 합산하지 않습니다. Portable fixture는 Agent 없이 별도 credential/설치/state를 사용합니다. 준비·재개·제어 도구에서 게시본 해시와 실제 readiness를 확인합니다.
- 손상 repair 직전 파일은 손상 상태로 백업될 수 있습니다. 정상 rollback 시험은 repair 성공 후 정상 상태에서 다시 repair하여 최신 backup 전체 hash를 확인합니다.
- OS 해상도·배율·고대비는 사용자 협업으로 변경/원복하고 앱 진단의 screen/work area·RenderScaling·DIP·글자 배율로 대조합니다. 사용자 prefs는 원본을 보존하고 중간 사용자 변경이 감지되면 복원 덮어쓰기를 거부합니다.
- PreviousInstallation은 인증된 동일 track의 bounded 설치 기록 표시 힌트입니다. 현재 선택의 IsInstalled/InstalledVersion/HasBackup 의미, 실행 권한·실행 대상·백업 대상을 바꾸지 않습니다.
- 조회 오류의 재시도를 설치/실행으로 바꾸지 않습니다. IPC 연결 대기와 연결 후 작업 제한을 분리하고 외부 취소를 서비스 장애로 바꾸지 않습니다. 작업·릴리스 snapshot이 달라지면 조회하고 rollback은 새 preview/확인을 받습니다. backup ID/fingerprint를 설치 lease 안에서 재검증합니다.
- 관리형·portable의 작업 context/result는 같은 모드와 정확한 선택에 결속합니다. Portable은 실제 버전 경로와 로컬 runtime 상태를 표시하고 Agent 상태로 대체하지 않습니다. 문제 해결은 미설치/새 버전을 설치하지 않으며 정상 설치는 점검만, 설치된 손상 대상만 repair합니다. 복원 후 상태 재확인 실패를 복원 실패나 무조건 Ready로 오인하지 않습니다.
- 관리형 정리에 필요한 Agent 명령이 없으면 보호 디렉터리 직접 쓰기로 우회하지 않습니다. 기존 IPC와 신규 rollback-preview-v1 capability를 구분합니다.
- headless DIP viewport/UIA 이름은 실제 DPI·내레이터 음성 증거가 아닙니다. 실제 GUI 시험은 합성 앱에 한정하고 적용 직전 필요한 확인을 받습니다.
- GUI 조작 주체를 구분합니다. 사용자 조작 후 결과만 관측했다면 에이전트가 버튼을 눌렀다고 기록하지 않습니다. 원래 OS 설정/원복을 확인하지 못했다면 미확인으로 남깁니다. UI-01은 최종 게시본의 실제 18개 OS 조합·최악 조건, UI-02는 양 모드의 기능 수용을 통과해야 100%이며 음성 보류 중 UI-03은 75%입니다.
- 관리형 실행 성공을 Ready로 단정하지 않습니다. runtime 관측의 Running/Pending/Unknown/누락은 차단 상태이며, 문제 해결에서 typed runtime 실패를 일반 예외로 잃거나 rollback 제안으로 바꾸지 않습니다.
- GUI 시험용 앱은 셸 없이 자기 자식·marker만 사용하며 공식 배포물에 포함하지 않습니다. 미설치 GUI 시험을 CLI 선설치로 대체하지 않습니다. 실제 버튼 조작과 단위/CLI 검증을 별도로 기록합니다. 일반 설치/실행·창 종료 통과를 모든 개발자 경로 통과로 확대하지 않습니다. rollback 증거는 실제 정상 backup과 복원 후 hash로 확인하며, 같은 설치의 backup 복원을 다른 버전 경로로의 전환이라고 표현하지 않습니다.
- 일반 화면은 한 버튼·친화적 오류, Agent 대신 업데이트 서비스로 표기합니다. 기술 예외·내부 경로·비밀정보를 기본 화면에 표시하지 않습니다.
- 개발자 명령을 보존하되 서버 권한을 확대하지 않습니다. 배포 서버 모드 GUI는 현재 OS용 릴리스를 선택합니다.
- 이미지 누락·손상·과대 파일은 브랜드 fallback으로 처리합니다. 키보드·focus·스크린리더·DPI를 확인합니다.
- GUI 설정 탐색은 explicit --config → 실행 파일 옆 → 관리 설정입니다. 관리형 운영 값은 Agent 설정을 사용합니다.
- single-file 네이티브 라이브러리 포함 옵션과 XAML의 &amp; escaping을 유지합니다.

## 작업·검증·커밋

- 공식 MSI는 payload EXE 서명/검증 후 생성한다. 내장 CAB EXE hash/signer 검사 전 artifact를 공개하지 않는다. 실행별 WiX intermediate를 사용하고 stale MSI wildcard를 금지한다.

1. git status와 지침을 확인하고 사용자 변경을 보존합니다. 브랜치·remote를 임의로 교체하지 않습니다.
2. 의미 단위 구현 후 관련 테스트를 실행합니다. --no-restore 전 restore가 필요합니다.
3. 기능 변경은 publish된 GUI/Agent/CLI/서버로 실행 검증합니다. Unreal Editor 프로젝트가 아닙니다.
4. Windows/WSL 결과를 회사 RHEL/실제 UE 결과로 보고하지 않습니다.
5. 관련 파일만 git add로 선별 stage하고 staged diff 확인 후 부분별 커밋합니다. git add -A는 사용하지 않습니다.
6. 최종 git diff --check, 링크, 커밋 범위, worktree와 tools/check-documentation.py의 구조/링크 검사, tools/check-improvement-ledger.py의 집계 검사를 확인합니다. push/PR은 요청 범위에 따릅니다.

기본 빌드·테스트 명령은 README를 따릅니다. 문서 전용 수정은 소스·명령·링크 대조와 diff 검사로 검증 가능하며 GUI 실행·전체 테스트를 수행한 것처럼 보고하지 않습니다.

publish/bin/obj, 테스트 logs·DB·인증서·토큰, 패키지·설치 데이터는 커밋하지 않습니다. 새 테스트 없이 과거 검증 날짜·결과를 덮어쓰지 않습니다.

성능 변경은 [재현 절차](docs/reference/archive/validation/performance-validation.md)를 따릅니다. 준비 1회/측정 3회, 같은 publish 형식·데이터·호스트 조건으로 비교합니다. CLI 시작 비용·OS 캐시·측정 프록시 영향과 누락 지표를 구분합니다. 실패한 요청을 지연 통계에서 숨기지 않고, 악화된 시나리오를 유리한 평균으로 덮지 않습니다. 공유 CI의 시간 수치를 성능 합격 gate로 사용하지 않습니다.
