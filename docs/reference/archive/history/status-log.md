# 현황 이력 — 루트 정본에서 이동한 검증 기록

2026-10-12 문서 구조 정리 1단계에서 README·PROJECT_GOALS·IMPROVEMENTS의 날짜·후보별 현황 문단을 기준 커밋 `3abf964` 원문 그대로 옮겼습니다. 링크 경로만 이 파일 위치에 맞게 바꿨습니다. 문장 안의 “현재/최신/이번”은 **당시 기준**이며 현재 상태가 아닙니다. 현재 상태는 [루트 README](../../../../README.md)와 [자동 생성 진행표](../validation/local-completion-progress.md)를 확인하세요.

## README.md에서 이동

### 원래 위치: 직전 후보와 공통 사용 안내

문서 점검: **2026-10-06**, 작업 기준: `codex/client-acceptance-closure`. 일반 `UeDtLauncher.exe`와 개발자 `UeDtLauncher.Developer.exe`는 별도 빌드이며 설정으로 GUI 종류를 바꾸지 못합니다. 복원 preview·정리 재시도의 정확한 작업 결속, 복구 적용 후 조회/취소 상태, 정리의 부분 실패·실제 잔여 수, 최소 화면 설정 생성과 개발자 설정 재읽기를 보완했습니다. 최종 제품 `75ea591`에서 Windows **789개 통과**, WSL **788개 통과·Windows 전용 1개 명시적 제외**, 양 에디션/Agent/서버 Release publish와 HTTP 요청 서명·Linux HTTPS/Bearer E2E를 확인했습니다. **최종 네 조합 전체 GUI 수용은 미완료**이며 과거 후보의 설치/복원 통과를 합산하지 않습니다. [현재 검증과 남은 사례](../validation/client-acceptance-closure-validation.md), [이전 후보 이력](../validation/client-usability-validation.md)을 구분하세요.

이번 고정 게시본의 실제 GUI 수용은 **18/51 사례 통과·33사례 미완료**입니다. 네 조합의 초기 설정/재연결·최초v1 설치/실행·창 종료 후 수명, 관리형 일반의v2 취소/Range 재개/명시 실행을 확인했습니다. 관리형 일반의 복구 후 조회 전용 재시도·정상 백업 준비·복원 취소/preview 거부/정상 복원·복원 후 조회 재시도도 확인했습니다. 나머지v2·복원·정리/인증 오류 전수가 남아 USER-02/UI-02는75%를 유지합니다. 이 사례 비율은 제품 완성률이 아닙니다.

### 원래 위치: 현재 구현

2026-09-29에는 합성 demo 대신 **실제 ma0t10_dt UE5.3 Windows Development 패키지**로 ZIP/외부 JSON 접수·서명 게시·GUI 설치/실행·런처 종료 후 UE 수명 유지·정상 종료를 확인했습니다. 328개 Manifest 파일 해시가 일치했습니다. 실제 UE 버전 업데이트/복구·Linux/RHEL·회사 서비스 계정은 별도 미검증이며, 설치 경로에 쓰는 CustomLogs는 추가 보완이 필요합니다. [실제 UE 시험·명령·제한](../validation/real-ue-package-validation.md)

현재 보완 소스는 **Windows/WSL 각 510개 통과, Release 경고·오류 0**입니다. 변경 없는 소스로 고정한 `8ce5060`의 Windows/Linux publish와 Linux HTTP 요청 서명+Agent·HTTPS/Bearer+nginx E2E도 통과했습니다. 관리형·portable의 정확한 선택/설치/runtime 표시, 최초 연결 실패 재시도와 복원 후 상태 재확인, 제목 글자 배율·작은 화면·포커스를 보완했습니다. Portable은 Agent 없이 `로컬 모드`로 동작하고, 문제 해결은 설치된 손상 대상만 복구하며 미설치 버전을 자동 설치하거나 앱을 실행하지 않습니다.

새 고정 후보의 portable v1→v2 설치/실행·파일 해시·v1 보존·창 종료 후 자식 수명·실행 중 변경 버튼 차단을 확인했습니다. 개발자 손상 복구, 정상 백업 생성 후 복원 취소/적용, 변경 preview 거부와 재확인, 서버 재연결 후 정상 설치의 점검 전용 문제 해결도 통과했습니다. 같은 후보의 **관리형 v1 최초 설치/실행·3파일 해시·GUI 종료/재실행 중 동일 자식 수명·정상 종료 후 실행 가능 복귀**도 확인했습니다. 관리형 v2 업데이트/실행·3파일 해시·v1 보존·생존 중 개발자 변경 버튼 차단·정상 종료도 확인했습니다. 관리형 개발자 손상 복구와 정상 상태에서 추가 repair로 만든 백업20260928145009의 3파일 해시도 통과했습니다. 해당 백업의 복원 취소/적용·v1 보존·완료 제목과 최신 상태 복귀도 확인했습니다. 관리형 변경 preview 거부/새 확인 요구와 서비스 재연결 후 정상 설치의 점검 전용 문제 해결도 통과했습니다. 일반 화면 손상 복구 등 남은 오류 조합과 전체 DPI 수용은 남았습니다. 미설치 재연결 첫 클릭 복귀·설치/실행0건은 작업 중 게시본의 별도 사전 시험입니다. 서로 다른 게시본의 성공을 합산하지 않습니다. [게시본별 실제 증거와 남은 조건](../validation/ui-acceptance-finalization.md)

기존 GUI 검증 이력에서는 일반 GUI 설치·실행·실행 중 버튼 차단·창 종료/재실행·자식 정상 종료, 개발자 선택/취소·v2 파일 복구·같은 설치의 정상 backup rollback을 실제로 확인했습니다. [GUI 실행 이력](../validation/managed-gui-safety-validation.md), [runtime 장애 이력](../validation/runtime-safety-completion-validation.md)

### 원래 위치: 검증 범위와 제약

2026-10-03 진단 후속: Windows/WSL 각587개, Release 경고·오류0, 게시 HTTP 요청 서명·HTTPS/Bearer·구형 Agent·지원 ZIP 검증을 확인했습니다. GUI의 상세 실제 통과는 [게시본별 표](../validation/readiness-validation.md)에 있습니다. 사용자 요청으로 마우스·GUI 자동 검증은 잠시 중단했으며 새 후보 전체 GUI 수용을 이전 후보 성공과 합산하지 않습니다. USER-02·UI-02는75%를 유지합니다.

2026-09-12 [기존 실행 기록](../validation/distribution-validation.md): Windows 210/210, WSL Ubuntu 210/210, Release 경고·오류 0. 테스트 프로그램으로 HTTPS 배포와 Windows GUI/Agent·Linux CLI 설치·실행을 확인했습니다.

2026-09-28 성능 작업: Windows/Linux 각각 전체 293개 테스트 통과, Python 측정 도구 계약 테스트 16개 통과. Release build/publish 및 실제 Windows 일반·개발자 GUI/Agent, Linux CLI/Agent 실행을 확인했습니다. Linux nginx HTTPS E2E의 작은 파일은 통과했지만 WSL1 nginx의 1MiB 응답 중단이 관측되어 큰 파일 GUI 검증은 격리된 Windows HTTPS 프록시로 분리했습니다. 실제 회사 RHEL·UE 패키지·IP/CA·설치본 수명주기는 별도 검증해야 합니다. 코드서명 없는 개발 산출물을 운영용 서명 제품으로 배포하지 않습니다.

## PROJECT_GOALS.md에서 이동

### 원래 위치: 직전 기준점

갱신: 2026-10-06 / 작업 기준 `codex/client-acceptance-closure`. **최상위 목표는 확정**됐습니다. 재시도의 정확한 선택/작업 결속·적용 완료 후 조회·수동 정리 부분 실패·관리형 화면 설정 생성과 개발자 설정 재읽기를 보완했습니다. 최종 제품75ea591의 Windows789개/WSL788개 통과(Windows 전용1개 제외), 양 OS 게시 인증/IPC·Linux HTTPS를 확인했습니다. 네 새 fixture의 실제 초기 설정·재연결 검증은 진행했고 전체 설치/취소/복원 수용과 회사 서비스 계정 ACL은 남아 있습니다. 회사 SLA·출시 인수 완료가 아닙니다. [현재 후보별 기록](../validation/client-acceptance-closure-validation.md).

### 원래 위치: 현재 위치

최신 후보는 커밋 snapshot75ea591이며 Windows/Linux 제품 입력 hash가 같다. 합성 앱과 시험 harness 출처를 별도로 기록하고, 설정/credential·인증 방식·서버 권한·runtime/transaction 보호를 유지한다. 수동 정리 실패를 성공으로 숨기지 않으며 설정 생성은 운영 설정 대신 최소 표시 선택만 제공한다. USER-02/UI-02는 최종 네 조합 전체 수용 전75%, USER-03/UI-03도75%를 유지한다. 현재 집계는완료6·부분19·대기3/열린22개이며 다음 과거 후보의 기록과 합산하지 않는다.

이번 GUI는 네 조합 초기 설정/재연결·각GUI 최초v1 설치/수명과 관리형 일반v2 취소/Range 재개/명시 실행까지18/51사례를 통과했다. 나머지33사례와 회사 인수를 유지하며 이 숫자를 제품 개발 공수·완성률로 해석하지 않는다. 관리형 일반의 복구 후 네 조회 경로·정상 백업·복원 취소/변경preview 거부/정상 복원/조회 재시도도 실제 확인했다. GUI 적용은 실행 직전 확인에 따라 수행한다.

### 원래 위치: 이전 단계 이력 (아래의 후보·테스트 수는 당시 근거)

최신 경계는 **사용자 세션 클라이언트는 선택/표시·Agent는 보호 설정/credential/Manifest/상태 검사**다. 같은 계정 console Agent 검증을 실제 서비스 계정 일반 사용자 ACL 인수로 확대하지 않는다. final-win2에서 네 조합 취소/재개와 일부 복원, final-win3에서 관리형 개발자 직접 복원 취소/preview 거부/정상 적용을 확인했다. 서로 다른 후보의 부분 통과를 최종 전체 수용으로 합산하지 않아 USER-02/UI-02는75%를 유지한다. USER-03은 네 조합 실제 취소/재개와 로컬 장애 회귀 근거로75%이며 회사 계정·전체 장애 인수는 별도다. 다른 SEC/OPS/PERF·회사 Gate는 자동 상향하지 않는다.

완료 후 취소 버튼 잔존과 작업 세대/재개 표시 결함을 수정했다. 실제 시험에서 추가 발견한 관리형 재개 선택 거부·빈 Catalog 이전 카드 잔존·권한 복귀 후 설치 상태 누락도 수정했다. 최종 제품 소스의 Windows/WSL698개 회귀와 관리형 일반의 v1 설치/실행·GUI 종료 후 자식 유지·v2 취소/Range 재개·v1 보존·재개 후 자동 실행0건을 확인했다. 네 조합의 잔여 오류/복원 수용은 진행 중이다. 사용자 합의에 따라 이번 화면/기능 증거는1440×900·100%로 기록하며 회사 Gate와 USER/UI 완료 조건을 상향하지 않는다.

698개 양 OS 회귀·최종 게시 CLI/Agent HTTP 요청 서명·취소/재개 시험과 위 실제 관리형 부분 수용을 확인했다. MSI/RPM 비설치 검증은 이전 에디션 이력이며 실제 설치 인수는 별도다. 전체 기능 수용은 아직 남아 USER-02/UI-02·음성 보류의 UI-03은75%를 유지한다. [현재 기록](../validation/client-workflow-validation.md)

성능 후보는 durable 순번 예약으로10/30연결 지연을 줄였고90% 콘텐츠 재사용을 유지했다. 직전1연결의 악화·통제된 재현 및 proxy/회사 환경 인수는 남으므로 PERF-03 전체 수용이나 회사 운영 승인을 선언하지 않는다.

Core 물리 이동·취소/재개·온라인 실행·자격 수명·통제된 backup/restore·확인형 retention·조회 전용 예약을 구현하고, 취소 ACK/commit·owner cache 예산·삭제 ledger·staged fence/독립 서명·HTTP 한도·정확한 선택을 보강했다. 양 OS 회귀/게시 프로그램은 GUI·회사·정전 수용을 대체하지 않는다. [최신 증거](../validation/operations-closure-validation.md)

빠른 개선 묶음은 진단 근거 보존과 모순 검증, 중복 조치 제거, 게시 준비도 검증의 Windows/Linux CI 연결까지 로컬 검증했다. 양 OS601개·준비도 각8개와 정제 요약/실패 기록을 확인했다. GUI 중단 중이므로 USER-02/UI-02는75%, 원격 CI 미확인으로 OPS-06은50%를 유지한다. [비대화형 결과](../validation/quick-wins-validation.md)

이번 후속은 Windows/WSL 각587개와 게시 E2E를 통과했다. 사용자 요청으로 마우스·GUI 자동 검증을 잠시 중단했고 마지막 표시 보강 이후 새 후보의 전체 수용은 보류다. USER-02·UI-02는75%로 유지하며 재개 후 같은 후보에서 남은 사례를 확인한다. 이전 후보의 성공으로 회사 G1~G6 또는 새 후보100%를 선언하지 않는다.

OPS-03의 명시적 승격과 기존 기준선 이전을 구현·로컬 수용했다. 후보a8080ea의 양 OS 각568개와 게시 CLI/API·HTTP/HTTPS·티켓/실행 불변·실제 일반 대기 화면을 확인했다. 이는 회사 Gate 승인 또는 기존 실제 UE 데이터 수용 완료가 아니다. [승격 결과](../validation/release-promotion-validation.md)

후속 `cf99ba1`은 데이터 계획의 정확한 릴리스 결속과 host 권한/로그 쓰기를 보강했고 Windows/WSL 각552개·게시 프로세스 및 인증 호환을 확인했다. 실제 UE GUI 자동 입력이 실패하면 사용자가 직접 조작하고 결과를 관측하는 방식으로 합의했다. 새 fixture 적용/SaveGame 보존은 사용자 조작 결과 전까지 미실행이며 USER-01은50% 유지다. [후속 수용 기록](../validation/runtime-data-acceptance-completion.md)

2026-10-02 런처만 수정하여 schema3 opt-in 사용자/버전별 UE 데이터 경로와 유지보수 보호를 추가했습니다. 후보 `64c4ad0`의 Windows/WSL 각542개, 게시 runtime-host 합성 v1/v2, HTTP 요청 서명·HTTPS/Bearer 회귀를 통과했습니다. 실제 UE 포함 두 릴리스 fixture의 UE 바이너리는 동일하며 시험 텍스트 파일만 다릅니다. GUI 입력 접근 거부로 실제 UE 업데이트/복원/데이터 수용은 미실행입니다. USER-01은50%, OPS-01은50% 유지이며 회사 Gate 완료가 아닙니다. [구현·검증·남은 조건](../validation/real-ue-data-safety-validation.md)

2026-09-29 실제 ma0t10_dt Windows Development 패키지의 로컬 접수→승인→GUI 설치/실행→런처 종료 후 수명 유지→정상 종료 증거를 확보했습니다. OPS-01은50%(로컬 증거)이며 G3는 여전히 미완료입니다. 실제 UE 업데이트/복구·Shipping/Linux·회사 TLS/CA/권한/서비스 계정과 데이터 보존은 이 결과로 대체하지 않습니다. [실제 UE 결과](../validation/real-ue-package-validation.md)

포스코DX 일반 라이트/개발자 다크·100~200% 글자/고대비를 유지하며, 관리형·portable의 정확한 선택/설치/runtime 표시와 문제 해결 정책을 통일했습니다. 보완 소스의 양 OS 각510개와 고정 후보 `8ce5060`의 portable v1→v2 설치/실행·해시·v1 보존·수명·변경 버튼 차단·개발자 복구·정상 복원/취소·preview 거부·정상 설치 문제 해결을 확인했습니다. 같은 후보의 관리형 v1 최초 설치/실행·3파일 해시·GUI 종료/재실행 중 동일 자식 생존·정상 종료 후 실행 가능 복귀도 통과했습니다. 관리형 v2 업데이트/실행·파일 해시·v1 보존·생존 중 개발자 변경 버튼 차단·정상 종료도 확인했습니다. 관리형 손상 복구·정상 백업 생성·복원 취소/적용·변경 preview 거부·서비스 재연결 점검도 통과했습니다. 일반 화면 손상 복구 등 남은 오류/개발자 확인 조합과 전체 화면 수용은 남습니다. 과거 게시본과 작업 중 게시본의 사전 시험은 별도 이력으로 보존합니다. [이번 증거와 제약](../validation/ui-acceptance-finalization.md)

## IMPROVEMENTS.md에서 이동

### 원래 위치: 직전 작업 이력

점검: 2026-10-06 / `codex/client-acceptance-closure`. 재시도 선택/작업 결속·복구 commit 뒤 조회/취소 상태·정리 부분 실패·최소 화면 설정 생성·개발자 설정 재읽기를 수정했습니다. 최종 제품75ea591의 Windows789개 통과/WSL788개 통과·Windows 전용1개 제외, 양 OS 고정 publish·HTTP/HTTPS·최소 관리형 설정/IPC가 통과했습니다. 네 새 GUI fixture의 초기 설정·연결 오류 조회 재시도는 직접 확인했으며 관리형 일반의 v1 설치/수명과 v2 취소를 진행했습니다. **최종 네 조합 전체 수용은 미완료**입니다. [현재 근거](../validation/client-acceptance-closure-validation.md). USER-02/UI-02/USER-03은75% 유지하며 완료6·부분19·대기3·열린22개입니다.

### 원래 위치: 먼저 확인할 진행 현황

최신 구현·회귀·직접 입력은 위 기록과 현재 수용 체크리스트가 기준입니다. 이하 usability/final-05/각753·698개, 이전 GUI 중단 문장은 **당시 이력**이며 현재 후보 통과나 현재 중단을 의미하지 않습니다. 제품 수정 후 최종75ea591의 네 root를 새로 만들었으며 이전 후보의 부분 성공을 합쳐100%로 표시하지 않습니다.

이전 workflow 후속 이력: 작업 시작/종료·취소 요청·재개 조건·세대별 진행 이벤트와 정상 Running 안내를 수정했다. 실제 GUI에서 발견한 관리형 resume 선택 거부, 빈 목록의 이전 카드 잔존, 권한 복귀 후 설치 상태 누락도 수정했다. 당시 Windows/WSL각698개, 시험 도구14+프록시1개와 게시 HTTP 요청 서명·취소/재개가 통과했다. `final-05` 관리형 일반의 추천 대기/빈 목록/재연결 무변경·설치/수명·취소/Range·hash·v1 보존 근거는 archive에 보존한다. 당시 USER-03의50%와1440×900 화면 조건은 현재 값이 아니며 위 최신75%/1920×1080 기록을 따른다.

이전 에디션 작업 이력: 별도 General/Developer 빌드·MSI 두 종·base/developer RPM, 공통 CLI/Agent/runtime-host, GUI 취소 완료/재개 결속을 구현했다. 당시 Windows/WSL각667개·게시 headless/readiness·비설치 payload 검증 통과. 현재 작업 수명/GUI 수용은 위 최신 기록을 따르고 [에디션 검증](../validation/client-editions-validation.md)은 당시 이력으로 보존한다.

OPS-07 closure: selected target과 준비도/설치검사 결속을 보강했다. default1/exact2 실제 게시 점검 및 inventory 불변 확인. 실제 OS 예약/서비스 계정은 후속이다.

SEC-01/02 closure: HTTP429·slot 반환·window 회복·만료endpoint·expiry audit 동시/실패 원자성을 보강했다. Windows48개와 게시 경계 통과, 회사 proxy/계정·범위 밖 인수는 유지한다.

OPS-05 closure: stage/live 공통 차단·전체 예약, 삭제 ledger 연결, 필수 그래프 양방향 대조와 latest policy 검증을 보강했다. Windows16개 및 활성화한 게시 서버의 인증된 실제 설치 확인. 회사 RPO/RTO/완전 유실/계정 인수는 유지한다.

OPS-04 closure: 후보별 durable 상태·directory identity·잔여 파일 삭제·private quarantine·schema6 삭제 이력을 구현했다. Windows16개/게시 통과. 실제 계정·전체 경합/후속 복원 연결 검증 전 전체 수용 완료로 올리지 않는다.

closure 캐시/기록 수명: requester partition·쓰기 예약·copy temporary 정리·discard archive/슬롯 회복을 구현했다. Windows18개 및 게시 headless 확인. USER-03 전체 수용/GUI·회사 계정 조건은 후속으로 유지한다.

closure 취소 보완: 등록 ACK·취소 보존·commit 결과·unstarted launch abort·Backend token을 구현하고 관련18개/게시 headless를 확인했다. 실제 GUI 취소와 회사 계정 조건은 유지한다. 후속 cohort로 최종 token/결과 흐름을 재검증한다.

후속 closure 시작: fixture 실제 소유/경로/게시본 확인과 비밀 stdout 비보관, 실패 checkpoint를 추가했다. Windows 계약3개와 기존 게시본의 headless 실행 통과. 항목 전체 진행률은 새 결함 수정·후속 게시 검증 후 판단한다. [현재 기록](../validation/operations-closure-validation.md)

이하635개 등은 이전 단계 이력이다. 당시 headless runner·broker/health/native family·Windows36개 종료 경계, 개발 MSI 추출·Ubuntu RPM을 확인했다. 현재664개와 추가 수용 조건은 위 요약·체크포인트를 따른다. 과거 RPM 중복 build-id 경고는 이번에 해소했으며 원격CI/실설치/회사 인증서는 여전히 미검증이다.

PERF-03 최신 cohort-06 재측정은1/10/30 p95가18.16/97.01/365.33ms, 실패0이나 비교 기준 개선 미달이다. 새 signer pool은 제거했고 계측만 남겼다. 75%와 시간 미달을 유지한다. [정제 비교](../validation/headless-performance-results.json). 90% 콘텐츠 절감 성공을 API 지연 완료로 합산하지 않는다.

Headless 주요 묶음은 구현·양 OS 회귀/게시 시험을 진행했다. 완료6·부분19·대기3, 열린22개다. 새 취소 GUI·장애 전수·회사 인수가 남아 신규 USER/SEC/OPS/DEV 항목은50%로 보수적으로 유지한다. PERF-03 10연결 개선 미확인, UI 중단/음성/원격CI·실제 인증서 상한도 유지한다. 부분 진행 수가 늘어난 것을 회사 출시 완료율로 해석하지 않는다.

2026-10-03 headless 후속 진행: 현재 계약으로 측정 도구를 정상화하고 fe10488 Windows 게시 HTTP 서명 E2E·80MiB 최초/변경 없음/다음 버전/repair·1/10/30 연결을 수행했다. 90% 콘텐츠 재사용 유지. 최종 후보 비교 전 PERF-03/SEC-04 진행률은 유지한다. [현재 검증](../validation/headless-operations-validation.md). GUI 수용은 중단 중이다.

2026-10-03 빠른 개선: `4a37061` 문서 정합성, `f215e62` 진단 근거 보존/모순 검증·중복 안내 수정, `9788de1` 준비도 CI 연결을 완료했다. Windows/WSL 각601개, 게시 CLI/Agent 준비도 각8개·요약 계약3개·fixture 계약13개 통과. 세부 체크포인트 완료이며 GUI 중단/원격 CI 조건으로 USER-02·UI-02는75%, OPS-06은50% 유지. [정제 결과와 조건](../validation/quick-wins-validation.md)

2026-10-03 최초 연결 진단 후속: USER-02는0%→75%. 최신 소스d665d67 Windows/WSL 각587개와 실제 게시 E2E 통과, 이전 후보의 양 모드/두 프로필 설치·실행·수명·복구·복원·오류/설정 재시도를 직접 확인했다. 일반 오류의 다음 조치를 보강한 새 후보는 양 모드403 화면을 확인했다. 사용자 요청으로 마우스·GUI 자동 검증은 잠시 중단했으며, 새 후보 전체 수용 전까지 USER-02·UI-02는75%다. [게시본별 증거](../validation/readiness-validation.md)

2026-10-02 OPS-03 완료(로컬 범위): 승인/승격 분리, revision·감사·schema4 기준선 이전, 현재 권한별 추천·구형 요청 제한, Core/Agent/GUI explicit policy 처리를 검증했다. 후보a8080ea Windows/WSL 각568개·게시본 이력/이전/실행 티켓·Running 불변·HTTP/HTTPS 통과, 실제 일반 GUI의 지정 대기/주 버튼 비활성화 관측. [승격 검증](../validation/release-promotion-validation.md)

2026-10-02 후속 `cf99ba1`: 데이터 릴리스/설치 결속·명시적 root 선택·host credential 중첩·Linux 권한·로그 write/flush 보강, Windows/WSL 각552개와 게시 프로세스/HTTP·HTTPS 회귀 통과. 자동 GUI 입력은0x80070005로 거부되어 사용자 직접 조작 협업 대기다. USER-01은 실제 데이터 보존 결과 전까지50%를 유지한다. [후속 기록](../validation/runtime-data-acceptance-completion.md)

## 2단계(README·PROJECT_GOALS 재구성)에서 이동

2026-10-12 2단계 재구성에서 기준 커밋 `df34fc1`의 날짜·후보별 문단을 원문 그대로 옮겼습니다. 같은 문단의 현재 정책은 루트 정본이나 현행 가이드에 요약해 남겼습니다.

### README.md — 원래 위치별

#### 최신 상태 — 로컬 보완 스프린트

2026-10-06 / `codex/local-completion-sprint`, 제품 후보 `1a7740f`. 재시도 준비 실패·Catalog 선택 갱신, 설치 commit 뒤 실행/바로가기 오류, 정리 후 상태 재조회, 승인되지 않은 정리 자료 보존을 수정했습니다. Windows849개, WSL848개+Windows 전용1개 제외 및 양 OS General/Developer/Agent/server Release 게시가 통과했습니다. [현재 검증](../validation/local-completion-validation.md).

인증 제한·복원 저장 경계·조회형 예약 장애 회귀와 양 OS 게시 HTTP/운영 CLI를 확인했습니다. 네 새 GUI 조합의 v1 설치·실행·수명과 관리형 일반의 취소/재개/실행 보호/복원 핵심 흐름을 확인했습니다. **최종51개 중8개 전부 통과·43개 전수 미완료**입니다. USER-02/UI-02는75%, 전체 대장은 완료6·부분19·대기3/열린22개를 유지합니다. [전체 진행표·남은 조건](../validation/local-completion-progress.md). 실환경·내레이터·원격CI·인증서 인수를 자동 상향하지 않습니다. 아래75ea591/789개 기록은 직전 후보 이력입니다.

#### 프로젝트 목표와 처음 읽을 안내

Catalog 순번은64개를 먼저 durable high-water로 예약하고 요청마다 서로 다른 번호를 발급합니다. 재시작 시 미사용 번호는 건너뛰며, 서명 응답이나 권한 허용 결과는 캐시하지 않습니다. Windows 재현 시험의10/30연결 p95는26.20/109.92→11.47/30.24ms로 개선됐지만 직전 후보의1연결 비교는10.6% 악화했습니다. PERF-03은75%를 유지합니다. 회사 SLA나 GUI 성능 보장은 아닙니다. [비교 조건](../validation/operations-closure-validation.md)

이 headless 묶음 당시에는 마우스·키보드·실제 GUI 검증을 중단했고, 현재 에디션 수용에서는 격리된 합성 앱 GUI 검증을 재개했습니다. MSI/RPM은 정확한 실행별 package와 payload hash를 확인하며, 현재 호스트에 설치/서비스/계정을 생성하지 않았습니다. 회사 인수용 미실행 계획은 `tools/prepare-company-acceptance.py`로 생성합니다. [설치본과 인수 준비](../../commercial-deployment.md)

#### UE 사용자 데이터 분리 — 선택 기능

추가 보강: 실행 티켓의 확정 릴리스·설치 경로와 데이터 계획을 대조하고, 사용자 host의 credential 경로도 보호합니다. Linux 실제 데이터 폴더는 sticky bit가 있어도 공용 쓰기를 거부하며, UserDir와 로그 폴더 양쪽에서 쓰기·flush를 확인합니다. [이번 후속 결과·직접 조작 순서](../validation/runtime-data-acceptance-completion.md)

[정확한 설정과 권한 조건](../../guide-03-launcher-usage.md), [이번 구현·검증·GUI 대기 기록](../validation/real-ue-data-safety-validation.md)을 확인하세요. 이번 후보의 실제 UE 업데이트·복구·복원은 Windows 입력 접근 거부로 아직 미실행입니다. CLI 시험을 GUI 수용으로 대체하지 않았습니다.

#### 성능 설정과 현재 결과

Windows 재현 시험에서 작은 파일 최초 설치는 중앙값 10.96초→5.98초, 다음 버전 설치는 11.01초→3.29초였고 콘텐츠 전송량은 90% 줄었습니다. 서버 30개 연결의 혼합 API p95는 833.44ms→81.23ms, 측정 요청 실패는 32→0이었습니다. **10개 연결의 혼합 p95는 5.43ms→24.66ms로 악화되어 PERF-03은 부분 완료**입니다. 회사 성능 보장이나 운영 배포 승인이 아닙니다. [설정·재현·한계](../validation/performance-validation.md)

### PROJECT_GOALS.md — 원래 위치별

#### 최신 위치 — 로컬 구현과 회사 인수 분리

2026-10-06 / `codex/local-completion-sprint`, 제품 후보1a7740f. Windows849개/WSL848개+Windows 전용1개 제외와 양 OS 동일 입력 snapshot 게시가 통과했습니다. 재시도/선택·설치 commit 후 오류·조회·정리 보존·인증/운영 장애 검증을 보완했습니다. [현재 검증](../validation/local-completion-validation.md).

네 새 합성 GUI 조합의 최초 설치와 실행 수명을 직접 확인했습니다. 현재8/51 전부 통과이며43개 전수, WSL1 broker 준비/종료 timeout, 회사 계정/UE/RHEL/인증서/원격CI·운영 정책은 남아 있습니다. [전체 진행표](../validation/local-completion-progress.md). 완료6·부분19·대기3/열린22개와 회사 승인 미완료를 유지하며, UI01의 합의된1920×1080 범위를 다른 DPI·음성 수용으로 확대하지 않습니다. 아래75ea591 기록은 직전 후보 이력입니다.

#### 회사 운영 승인 조건

G1~G6의 증거가 확보된 뒤 전사 확대를 승인합니다. 게시본 `ab37fcb`의 Windows/WSL 자동화 각 500개와 Linux HTTP/HTTPS E2E 통과나 합성 성능 개선만으로 대신하지 않습니다. 관리자 웹 화면·회사 API·추가 알림 등은 필요성/범위가 확정되기 전 필수 완료 조건으로 임의 추가하지 않습니다.

#### 개발·도입 진행 순서

2차 구현: MSI payload 선서명 gate와 runtime-host/설치 변경 조정기를 추가했다. OPS-08은 실제 인증서 미검증으로 50%, OPS-09는 합성 실행 검증 75%이며 후속 저장 경계 강제 종료·health 실패·서비스 대상 구분은 합성 환경에서 검증했다. 이전 관리형 GUI 설치/실행·창 종료 수명, 개발자 선택/취소·v2 복구/rollback과 이번 portable 실행 중 버튼 차단은 각각의 게시본 이력이다. 모든 변경 진입점·실행 상태 조합의 실제 전수 시험, 새 최종 GUI 수용 및 회사 UE/계정 검증을 대체하지 않는다. [runtime 후속 검증](../validation/runtime-safety-completion-validation.md)

관리형 GUI의 실행 후 관측·문제 해결 차단을 추가 보완했다. 이전 관리형 이력과 별개로 새 고정 후보의 관리형 v1 미설치→실행→창 종료/재실행→자식 정상 종료도 실제 합성 앱으로 통과했다. 같은 후보의 portable·관리형 정확한 버전 backup 복원/취소·preview 거부도 확인했다. 남은 기능/오류 조합은 별도이며 다른 해상도 재시험은2026-09-29 합의에 따라 후속으로 분리한다. [현재 후보별 증거](../validation/ui-acceptance-finalization.md)를 기준으로 판단한다.

## 3단계(IMPROVEMENTS 앞부분 정리)에서 이동

2026-10-12 3단계 정리에서 기준 커밋 `bfecbb9`의 IMPROVEMENTS 앞부분 날짜·후보별 문단과, 최신 후보로 갱신되지 않은 요약 행을 원문 그대로 옮겼습니다. 현재 요약 행은 진행표 링크로 대체했습니다.

### 원래 위치: 최신 작업 — 2026-10-06 로컬 스프린트

`codex/local-completion-sprint` 제품 후보1a7740f의 Windows849개/WSL848개+Windows 전용1개 제외, 양 OS 고정 게시를 확인했습니다. 재시도 준비·선택 갱신·commit 뒤 실행/바로가기 실패·정리 결과를 보완하고 retention schema2의 새 자료 보존, 인증 제한/슬롯 반환, 복원 저장 경계/예약 조회 실패 회귀를 추가했습니다. 양 OS 게시 HTTP·관리형 진단/복원·온라인 IP/exact/만료 거부·운영 backup/restore/retention이 통과했습니다. [현재 근거](../validation/local-completion-validation.md).

새 네 GUI 조합은 v1 최초 설치/실행/수명, 관리형 일반 v2 취소·Range 재개·명시 실행과 실행 중 보호를 확인했습니다. 최종51개 전수와 WSL1 HTTPS broker timeout은 미완료입니다. USER-02/UI-02/USER-03/UI-03은75%, OPS-08은50%를 유지합니다. **집계 완료6·부분19·대기3/열린22개는 변하지 않았으며 코드 보완을 항목100%로 바꾸지 않았습니다.** 아래75ea591/789개/18개 GUI 근거는 직전 후보 이력입니다.

### 원래 위치: 먼저 확인할 진행 현황

현재 집계: **완료6·부분19·대기3, 열린22개**를 유지합니다. 실제 합성 GUI 부분 수용은 위 최신 기록을 따르며 회사 UE/RHEL/서비스 계정·실제 인증서·내레이터 음성·원격 CI는 별도 미검증입니다. 대부분의 headless 기능은 구현돼 있지만 열린 항목이 전부 미구현이라는 뜻은 아닙니다.

PERF-03: Windows10/30연결 p95 26.20/109.92→11.47/30.24ms, WSL10/30연결29.25/90.41→10.21/26.40ms를 관측했습니다. 실패0·90% 콘텐츠 절감 유지. Windows 직전1연결은10.6% 악화했고 측정 순서/ambient load는 완전 통제하지 못했으므로75%를 유지합니다. 원래 d756af2 비교와 최적화 직전 비교를 구분하며 원시 키/로그는 보관하지 않습니다.

### 원래 위치: 현재 진행 상태 요약 (표 행)

| 구분 | 확인된 상태 | 아직 남은 조건 |
|---|---|---|
| 새 UI | 포스코DX/에디션 고정·현재 재시도/정리 결과/설정 재읽기 보완. Windows789/WSL788+1 제외 | UI-01은1920×1080 합의 범위 완료. 최종 네 조합 전체 수용·다른 해상도/DPI·음성 별도 |
| GUI 직접 확인 | 최종75ea591 네 조합 초기 설정/에디션·연결 오류 재시도·v1 GUI 설치/수명, 관리형 일반v2 취소/Range 재개/명시 실행 통과 | 18/51 사례 완료,33사례 미완료. 관리형 일반 복구 후 조회·정상 백업 준비·복원 취소/변경 거부/정상 적용/조회 재시도 통과. 나머지v2/실행 보호·복원/정리/오류. 이전 후보를 합산하지 않음 |

## 5단계(상세 가이드 정리)에서 이동

2026-10-12 5단계 정리에서 기준 커밋 `3303e64`의 상세 가이드·색인에 남아 있던 날짜·후보별 현황을 원문 그대로 옮겼습니다. 체크리스트의 확인 기준 열은 가이드에 남기고, 직전 후보 `75ea591` 기준 판정 열만 옮겼습니다.

### docs/reference/README.md — 색인 머리말

최신 작업은2026-10-06 `codex/local-completion-sprint`입니다. [스프린트 검증](../validation/local-completion-validation.md)에 고정 제품1a7740f,849개 전체 회귀, 운영 CLI·현재 GUI 근거와 미완료를 기록합니다. 아래75ea591/closure 기록은 직전 이력입니다. 루트4종 정본과 상세9개를 유지하고 새 실행 근거는 archive/validation 아래에만 보관합니다.
정리: 2026-10-05 / 작업 기준 `codex/client-acceptance-closure`. 상시 관리 정본은 루트 4개이며, 이 폴더에는 **현재 사용하는 상세 가이드 9개와 이 색인**만 둡니다. 최소 화면 설정 생성과 재시도·정리 결과는 현재 설정/GUI/기능 지도에 반영했습니다. [최종 고정 후보의 검증·미실행 사례](../validation/client-acceptance-closure-validation.md)는 과거 후보와 구분합니다.

### docs/reference/README.md — 검증 증거와 과거 자료

최신 에디션 분리: [일반/개발자 빌드·설치본·사용 흐름](../validation/client-editions-validation.md). 두 실행 파일과667개 회귀, 실제 마우스 입력의 환경 제약을 구분합니다.
최신 추가: [운영 안전성·게시 프로그램 검증](../validation/operations-closure-validation.md). 취소/캐시/정리/복원/인증/점검·설치본과 실제 GUI/회사 미검증을 구분합니다. 이전 [UE 데이터 경로](../validation/real-ue-data-safety-validation.md)는 당시 후보의 이력입니다.

### docs/reference/README.md — 관리 규칙

- 이번 UI 변경은 `ab37fcb`의 각500개·portable 실제 적용과 추가 상태 보완 소스의 각510개·미설치 재연결 사전 시험을 확인했습니다. 새 최종 후보 전체 수용은 대기입니다. [최신 증거·미완료 조건](../validation/ui-acceptance-finalization.md)을 따르며 과거 게시본 성공을 합산하지 않습니다.

### docs/reference/guide-03-launcher-usage.md — 현재 GUI 수용 체크리스트 — 직전 후보 판정

2026-10-05 closure 최종 제품75ea591에서 Windows789개/WSL788개 통과·Windows 전용1개 제외, 양 OS publish HTTP 인증/IPC·Linux HTTPS를 확인했습니다. 실제 화면은1920×1080/OS100%/앱100%/고대비끔입니다. 이전 후보의 설치/복원 수용은 이력이며 새 네 root의 사례별 ledger만 최종 전체 수용으로 계산합니다. [현재 근거](../validation/client-acceptance-closure-validation.md), [이전 usability 이력](../validation/client-usability-validation.md)을 구분합니다.

| 사례 | 재개 후 확인 기준 | 현재 최신 후보 전체 판정 |
|---|---|---|
| 초기 설정·연결 | 수정 후 조회만·에디션 고정·서비스/서버 재연결·파일 불변 | 네 조합 직접 통과. Portable Agent 요청0건. 실제 서비스 계정ACL과 별도 |
| 빈 목록·승격 | 이전 카드 제거·추천 대기→promote 후 조회만 | 관리형 일반 통과. 다른 세 조합 남음 |
| 설치·실행·수명 | v1→v2 정확한 실행, v1 보존·GUI 종료 후 자식 유지 | 네 조합v1 GUI 설치/3파일 hash·창 종료 후 자식 생존·정상 요청 종료 통과. 관리형 일반 외v2 남음 |
| 개발자 exact 확인 | 취소 불변·승인 대상만 실행 | 두 개발자 exact1 확인 취소(all 불변)/승인 대상 실행 통과. exact2와 나머지 조건 남음 |
| 취소·재개 | 실제 수신 중 취소·즉시 재개·Range·hash·완료 버튼 정리 | 관리형 일반v2 수신 중 취소/즉시 재개·Range11993088·4파일 hash·v1 보존·자동 실행0건·별도 승인 후v2 명시 실행/정상 종료 통과. 다른 세 조합 남음 |
| 실행 중 보호 | 생존 전후·변경 차단·보호 hash 동일 | 최종 네 조합의 변경/정리 시험 남음 |
| 문제 해결 | 정상은 조회만·손상은 복구·commit 후 조회 오류에서 재적용0회 | 관리형 일반의 실제 손상 복구/조회 오류1회/주·상태·재시도·F6 조회만·작업 기록 hash 불변 통과. 다른 세 조합 남음 |
| 정상 backup/복원 | 정상 추가 repair·전체 backup hash·취소/변경preview 거부/새 확인/적용 | 관리형 일반 시험의 보조 Developer 추가repair로 정상 backup4파일 확인·General 복원 확인 취소 불변. General 변경preview 거부/새 확인/정상 복원/취소 불변도 통과. 다른 세 조합 남음 |
| 복원 후 조회 | file-transition 뒤 실제 Catalog 오류1회·조회만 재시도 | 관리형 일반의 file-transition witness·Catalog 오류1회·조회 재시도/재복원0건·v1 보존 통과. 다른 세 조합 남음 |
| 정리·진단·인증 오류 | partial failure/잔여 수·정리만 재시도·ZIP·401/403/서명/지원ID | Windows held-handle/headless 및 게시 인증 회귀 통과. 실제 GUI 적용 전수 남음 |

현재 단계별 근거와 게시본 소스/파일 해시는 [클라이언트 closure 검증](../validation/client-acceptance-closure-validation.md)에 기록합니다. [클라이언트 작업 흐름 검증](../validation/client-workflow-validation.md)은 이전 이력입니다. 설치/실행·GUI 삭제의 적용은 Computer Use 지침에 따라 실행 직전 확인받으며 GUI 설치를 CLI 선설치로 대체하지 않습니다.
