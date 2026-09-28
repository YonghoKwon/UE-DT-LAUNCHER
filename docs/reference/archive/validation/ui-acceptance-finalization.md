# UI 수용 최종화 실행 기록

기준: 8ed01cc / codex/ui-acceptance-finalization / 2026-09-28.

## 1. 관리형·portable 시험 기반

- GUI fixture를 managed/portable로 분리했다. Portable은 Agent를 시작하지 않고 --storage portable 및 전용 portable-private 경로를 사용한다. v1만 승인하고 v2는 승인 대기로 둔다.
- 게시본을 fixture 안에 고정하고 실행 파일·허용된 서버 runtime sidecar 해시를 준비/재개/제어에서 검사한다. 기존 fixture나 다른 게시본으로 덮어쓰지 않는다.
- 준비·재개는 같은 readiness와 OS harness 잠금을 사용한다. 401/403/빈 목록/신뢰키 불일치/서버 단절은 시험 root 및 harness 소유 프로세스만 변경한다.
- Python 11개, 구문/diff 검사 통과. 첫 portable 준비는 e_sqlite3.dll 누락으로 실패했다. 서버 sidecar allowlist 복사·hash 검사를 추가한 portable-preflight-02에서 서버 등록·승인 대기·portable credential 준비 성공.
- 실제 publish 일반 GUI에서 Agent 없이 로컬 모드·미설치·최신 v1 표시, apps 폴더 없음 확인. 화면 진단: 1920×1080, 작업영역1920×1032, RenderScaling1, client1120×740, 글자100%, 고대비false.
- 이 준비 게시본(candidate1)은 부분 구현 중 소스로 만든 preflight이며 최종 수용 게시본 성공으로 간주하지 않는다. 설치 버튼은 아직 누르지 않았다.

## 2. 모드별 상태·문제 해결

- 내부 operation context/result로 정확한 릴리스·모드·runtime을 묶었다. Portable은 로컬 check/engine/runtime만 사용하며 실행 후 Running/Pending/Unknown을 Ready로 덮지 않는다.
- 미설치/새 버전 문제 해결은 설치 안내로 끝나며 이미 설치된 대상의 손상만 repair한다. 복구 제안/preview도 배포 모드를 유지한다. 개발자 확인창에는 고정한 실제 버전을 표시한다.
- 설정 파싱/null 구조 오류를 안전한 문구/지원 ID로 표시하고 설정 수정 후 다시 확인을 지원한다. context/정책/null 설정 관련 15개 통과.
- 통합 소스 Windows 전체 500개 통과, Release 경고/오류0. candidate1 실제 portable 일반 GUI에서 Agent 없이 Catalog/설치 상태 조회·로컬 모드/미설치 확인. 적용 흐름은 최종 후보에서 별도 검증한다.

## 3. 글자·포커스·작은 화면

- Expander/Tab 제목에 공통 글자 배율을 적용했다. 열린 dialog 고대비 전환에서 focus3px·primary 색/글자 대비를 즉시 유지한다.
- headless에서 200%/854×400 오류 상태의 중앙영역0 DIP와640×360의9 DIP를 재현했다. 상태 제목까지 제한 스크롤로 옮기고 주 버튼을 고정해 해결했다. 작업 상세를 Tab/PageDown으로 읽을 수 있다.
- 개발자 Shift+Tab이 템플릿 헤더를 건너뛰던 문제를 Local 탐색으로 보완했다. 프레임워크 실제 peer identity/NameProperty 이벤트와 byte tick 억제를 검사한다.
- 관련 headless72개 통과(신규44개). 통합 Windows500개 및 Release 경고/오류0. 실제 Windows portable-preflight-02에서 글자200%, 확대된 도움말 제목/작업 버튼/본문과 Tab 주 버튼 focus를 확인했다. 사용자 글자 설정은 hash guard를 거쳐 원본으로 복원했다.
- 위 실제 확인은1920×1080/100%의 사전 확인이며 최종18개 OS 조합·OS 고대비·내레이터 음성 증거가 아니다.

## 4. 고정 게시본 ab37fcb의 실제 portable 시험

소스 `ab37fcbb68d5fb045d913e77861ead624d1da673`, 소스 변경 없음, fixture `accept-portable-01`이다. 이 게시본은 아래 시험 후 추가 상태 결함이 확인되어 **최종 수용 후보에서 제외**했다. 통과 결과는 해당 게시본의 유효한 이력으로 보존하되 새 후보의 통과 수에 합산하지 않는다. [정제된 구조화 결과](ui-acceptance-finalization-evidence.json)

| 실행 파일 | SHA-256 |
|---|---|
| GUI/CLI | d104fcb28491e90397def9133e99433d809ec7d1161f9626f3c55c7e15eb0801 |
| 배포 서버 | 46886ec6dd582934ca3b85777bec2c371afa8d904ec6c9ae7102868b054cde3d |
| 합성 앱 | 9c29acfbb5bfa31eee462c53257707cea08ef16a7a24fbde7a121f4c20ca5bfb |

Agent는 실행하지 않았다. GUI 자체 진단은 화면1920×1080, 작업영역1920×1032, RenderScaling1, 일반 client1120×740 / 개발자1280×740 DIP, 앱 글자100%, 고대비false이다. OS 설정 화면의 원래 값과 종료 후 원복은 아직 확인되지 않았다.

| 사례 | 실제 조작·관측 | 결과와 한계 |
|---|---|---|
| v1 최초 설치/실행 | 실행 직전 승인 후 일반 주 버튼 클릭 | 3파일 Manifest 해시 정상, v1 marker·Running. 이후 자연 제한시간 종료. v1의 GUI 종료 중 수명 증거로 사용하지 않음 |
| 새 v2 공개/안내 | 기존 서버 CLI 승인 후 F6 조회 | 기존1.0.0 / 최신2.0.0 / 업데이트 후 실행 표시 |
| v2 설치/실행 | 새 승인 후 일반 주 버튼 클릭 | 3파일 해시 정상, v2 marker·Running, v1 snapshot 불변 |
| 창 종료/재실행 | v2 생존 확인 직후 GUI만 닫고 재실행 | 동일 자식/실행 시도 생존·Running 유지. 이후 정확한 합성 시도의 자연 종료 신호 후 Quiescent |
| 실행 중 개발자 차단 | exact v2 선택, 유지보수 펼침 | 실행/업데이트·repair·rollback 비활성, 보호 파일 snapshot 불변. 모든 변경 진입점에 강제 요청한 전수 시험은 아님 |
| 서버 단절/재연결 | 시험 서버 중단 후 F6, 재시작 | 친화적 서버 오류·지원 ID 관측, 인증 우회 없음 |
| 일반 문제 해결 repair | v2 version.txt 손상 후 승인. 재관측 시 사용자가 이미 조작 완료 | GUI ‘파일 복구 완료’, 3파일 해시 정상, 앱 추가 실행 없음. **사용자 조작 협업 관측**이며 에이전트 클릭 성공으로 기록하지 않음 |
| 정상 백업 생성 | 정상 파일을 확인한 뒤 새 승인으로 개발자 repair 클릭 | backup `20260928141004`의 3파일 전체 해시 정상. 손상 repair의 직전 백업과 구분 |
| portable 복원 취소 | v2만 재손상, exact backup 확인창에서 Escape | 설치·Manifest·backup·journal 포함 보호 snapshot 불변, 상태 확인으로 포커스 복귀 |
| portable 정상 복원 | 새 실행 직전 승인 후 같은 v2 backup 확인/적용 | ‘백업 복원 완료’, 설치3파일 정상, v1 snapshot 불변, 두 버전 Quiescent·새 실행 marker 없음 |

화면 증거는 해당 대화의 native GUI screenshot/UIA 관측에 연결된다. 일반 ‘로컬 모드·기존/최신 버전·Running’, 개발자 exact v2/차단 버튼, 복구 완료, backup ID 확인창, 취소 후 포커스, 복원 완료 화면을 확인했다. 저장하지 않은 PNG 경로나 이미지 파일은 만들었다고 기록하지 않는다. raw 로그·키·credential·시험 설치 파일은 Git에 넣지 않았다.

## 5. 자동화·추가 결함과 재검증 경계

| 묶음 | 확인 결과 | 범위 |
|---|---|---|
| ab37 전체 회귀 | Windows/WSL 각500 통과, skip0, Release 경고/오류0 | 해당 게시본 |
| Linux 실제 호환 | HTTP 요청 서명+Agent, HTTPS/Bearer+nginx E2E 통과 | 합성 파일. 회사 RHEL/UE·WSL nginx 대용량 한계 해결의 증거 아님 |
| fixture 도구 | Python13개·구문 검사 통과 | 긴 한글 이름/notes·허용된 보조 프로젝트 생성 추가. 공식 제품 우회 옵션 없음 |
| 재연결 결함 | 최초 Catalog 실패 후 중첩 문제 해결에서 이전 오류가 남아 첫 재시도가 중단될 수 있음 | Catalog 조회 성공 여부를 명시하고 성공 시 오류 상태 제거하도록 보완 |
| 복원 표시 결함 | portable 복원 후 예전 설치/Working 상태가 남을 수 있음 | 같은 모드/선택으로 읽기 전용 재확인. 재확인 실패는 복원 적용 실패와 구분하고 Check 재시도로 안내 |
| 보완 소스 회귀 | Windows/WSL 각510 통과, skip0, Release 경고/오류0 | 위 추가 결함 회귀 포함. 새 최종 후보 GUI 수용과는 별도 |
| 보완 게시 실행 | recovery-candidate-01 Windows GUI/Agent/서버/합성 앱 publish 성공 | 작업 중 게시본. 새 후보 실제 복원 적용 재수용은 아직 미완료 |
| 최초 연결 실패 재시도 | recovery-preflight-01을 서버 정지 상태에서 시작해 친화적 Catalog 실패·지원 ID 관측. 서버 재시작 후 ‘문제 해결’ 한 번 클릭 | 설치 필요/최신v1/설치 후 실행으로 복귀, 이전 오류/지원 상태 해제. 설치 폴더 없음·실행 marker0. 조회 전용 시험이며 GUI 설치 대체 증거 아님 |

## 6. 남은 수용과 판정

새 고정 후보는 `8ce5060563c6294e55fbbcf086682a84ccef1c98`(제품 상태 수정 `fa87eaa`)이며 sourceDirty=false이다. Windows/Linux publish 및 새 Linux HTTP 요청 서명+Agent, HTTPS/Bearer+nginx E2E를 다시 통과했다. nginx 기본 로그 경로 경고는 있었으나 해당 합성 E2E는 통과했고 대용량/회사 환경 보장으로 확대하지 않는다. 새 `accept-portable-02`/`accept-managed-02`는 v1 공개·v2 승인 대기·미설치로 준비됐다. 관리형 readiness에서 runtime-supervision-v1/rollback-preview-v1을 확인했다. 새 portable 일반 GUI에서 별도 실행 직전 승인을 받고 v1을 설치·실행했다. 3파일 해시 정상, Running/주 버튼 차단, GUI 종료 후 동일 자식 생존과 다시 연 GUI의 Running 인식을 확인했다. 정확한 합성 시도의 자연 종료 신호 후 Quiescent와 F6의 최신 상태/실행 가능 복귀를 확인했다. 관리형 fixture는 미설치를 유지하며, portable의 v2는 이후 서버 CLI로 공개했고 F6 후 기존1.0.0/최신2.0.0/업데이트 후 실행 안내를 확인했다. 새 실행 직전 승인 후 같은 fixture에서 v2 업데이트 후 실행을 클릭했다. v2 marker/Running과 Manifest3파일 해시 정상, v1-before-v2 전체 snapshot 불변을 확인했다. 살아 있는 v2를 exact로 선택한 개발자 화면에서 실행/업데이트/복구/복원 버튼 비활성 및 v2-running snapshot 불변을 확인한 뒤 정확한 합성 시도에 정상 종료 신호를 보냈다. Quiescent와 F6 후 최신 상태·변경 버튼 재활성화를 확인했다. 이후 다음 복구 시험 준비로 v2의 version.txt만 손상시켰고 변경1/누락0을 확인했으며 v1은 불변이다. 별도 승인 후 개발자 검증/복구를 실제 클릭했고 파일 복구 완료/최신 상태와 3파일 해시 정상, v1 snapshot 불변, 추가 marker 없음/Quiescent를 확인했다. 손상 직전 파일이 백업될 수 있으므로 이 repair의 backup을 정상 복원 근거로 사용하지 않는다. **새 후보의 일반 문제 해결·정상 백업 복원과 관리형 GUI 적용은 아직 미실행**이다.

| 새 후보 실행 파일 | SHA-256 |
|---|---|
| Windows GUI/CLI | 2a809f4c6729aff6b60ab4e17c2380d196c4a18ad5069787e2bee9164670a4ab |
| Windows 서버 | 75d9553cc872220fa9aabf9c705b8a3c3536072ad8eb3bb3e976d984f8d0019f |
| Windows 합성 앱 | 997be6f2252b6250c0267c6f29a1086eb0c06998da7b73b85a73d8d265836972 |
| Linux CLI | 87a3ce297960006656653b9b2683b4306f4041d31c8fa759c59124b0590874b8 |
| Linux Agent | a840434a7662d92b72834c4e24249b8fdc6b3dadfea6e0edf7b3397072ed3ad9 |
| Linux 서버 | d966b016a56c841bd08d193318cd3ad9be1dc6aba5214b7dc9e81939feb403c8 |

| 항목 | 남은 실제 증거 | 현재 판정 |
|---|---|---|
| UI-01 | 새 최종 동일 게시본의 3해상도×3OS배율×2프로필 전18개, 1280×720/150%·글자200%·앱/OS 고대비·긴 오류/빈 목록/대화창 | 75% 유지 |
| UI-02 | 새 후보 양 모드 설치/업데이트·개발자 확인 취소/실행·문제 해결·정상 복원/취소·변경 preview 거부, 오류 구분/미설치 조회 재시도, 작은 환경 적용 | 75% 유지 |
| UI-03 | 새 후보 키보드 전체 흐름·OS 고대비 전환. 내레이터 실제 청취는 사용자 선택으로 후속 보류 | 75% 유지, 음성 자동화 대체 금지 |
| UI-04 | 트레이·완료 알림은 범위 제외 | 0% 유지 |

관리형 `accept-managed-01`과 새 `accept-managed-02`의 준비 성공은 실제 GUI 적용 통과가 아니다. Portable 정상 복원 이력은 추가 결함 보완 전 게시본이며 재시험이 필요하다. OS 변경/원복은 사용자 협업, 설치/복구/복원은 실행 직전 확인을 유지한다. 회사 UE·RHEL·설치 서비스 계정·인증서·정식 운영 인수 및 다른 OPS/SEC/PERF 진행률은 이번 결과로 변경하지 않는다.
