# 로컬 기능·운영·GUI 수용 마무리

## 고정 게시본과 실제 실행

제품 후보1a7740f, 입력 hash67f441e18d16e0843e4f1050680f2cdc97b7d15254641703ce2016a1a0e58532의 Windows/Linux General·Developer·Agent·server 게시가 경고/오류0으로 통과했다. Windows849개, WSL848개+Windows delete-sharing 전용1개 제외. 양 OS 게시 HTTP 서명·readiness·schedule·자격·관리형 최소 화면 설정/IPC 복원·온라인 IP/exact누락/만료 거부는 설치/runtime 변경0건으로 통과했다. 양 OS offline backup/통제 restore/retention도 통과했다. 새 실행에서 살아 있는 기존 자식을 유지하는 단절/폐기 전수는 아직 별도다.

Windows HTTPS/Bearer runtime broker와 양 OS native runtime family가 통과했다. WSL1 broker는 첫 socket 준비15초 timeout 후 종료까지 timeout, 실제 readiness30초로 보강한 재시험도 timeout이었다. 종료는 보유한 Popen만 사용했고 새 추정 PID/이름 kill은 수행하지 않았다. 이번 WSL broker는 환경 제약/미통과이며 HTTP Agent E2E 성공으로 대체하지 않는다.

Windows fixture 준비2건은 preparation-status 원자 교체에서 WinError5로 실패했다. 소유자/ACL이 기대값과 일치함을 확인했고 ACL 변경 없이 해당 교체만 최대5회(총300ms)의 제한 재시도를 추가했다. 영구 거부는 기존 파일을 보존하고 실패한다. 제품 명령 자동 재실행/비원자 write fallback은 없다. 새 root의 준비는 통과했다.

## 새 GUI 실행에 결속한 근거

UiDisplay는 창별 session ID/생성 시각/실제 process ID를 기록한다. 도구의 GUI 시작 기록은 fixture·제품 hash·profile·새 run ID·시각에 결속된다. schema2 수용 기록은 현재 run의 화면만 사용하며 최초 설치/수명에는 GUI 설치=true·정확한 runtime attempt·requested 종료가 필수다. 예전 schema1은 이력이고 새 통과를 추가하지 않는다. 도구 계약13개와 fixture23개가 통과했고 중간 게시 GUI 시작/접근성 트리/정상 닫기를 직접 확인했다.

전체 Windows 회귀 첫 실행은849개 중847개 통과,2개는 내부 조회 함수의 새 선택 파라미터를 reflection 시험이 생략하여 실패했다. 명시적으로 false를 전달하도록 시험을 수정했고 해당 기존12개가 통과했다. 제품 결함이 아닌 시험 호출 정합성 수정이며 최종 전체 회귀에서 다시 집계한다.

## 인증·저장·자동 점검 회귀

HTTP 요청 서명33개와 HTTPS-origin Bearer 서버 회귀36개는 opt-in 다운로드 제한을 켠 HEAD/Range/이미지/404/416/권한 변경/폐기/만료/시계 역행, handler 실패·클라이언트 연결 취소 후 슬롯 반환을 실제 Kestrel HTTP로 확인했다. Bearer 단위 서버는 loopback HTTP transport에 HTTPS 공개 origin을 사용하며 실제 TLS 증거는 별도 게시 HTTPS E2E로 구분한다.

복원 활성화 다섯 내부 저장 경계의 예외 시험에서 fence·원본 파일을 보존했다. 재시도는 최신 자격 폐기와 sequence1201을 반영했다. 이는 프로세스 강제 종료/정전 내구성 시험으로 확대하지 않는다. 자동 점검13개는 단절만 최대2회, 인증/권한/검증 오류1회, 취소 시 이전 관측 보존·lease 반환·설치/state 생성0건을 확인했다.

stage4 General/Agent/server Release publish 및 게시 HTTP 서명·준비도·자격 수명·조회형 schedule·selection-only 관리형 CLI·실제 IPC 복원이 통과했다. 같은 새 fixture에서 게시 offline backup/통제 복원/retention도 통과했다. 중간 게시본이며 최종 GUI51개와 합산하지 않는다.

## 승인되지 않은 정리 자료 보존

RetentionPlan schema2는 파일뿐 아니라 빈 디렉터리의 OS 신원도 승인 목록에 포함한다. 승인 파일 삭제 후 새 파일/빈 폴더가 발견되면 재귀 삭제하지 않고 quarantine·DeleteIntent를 보존한다. schema1 적용은 거부하며 새 계획 또는 기존 중단 journal의 수동 점검이 필요하다. 디렉터리 탐색도 깊이128/개수500000으로 제한한다.

Windows retention/backup 관련22개가 통과했다. 실제 삭제 경계에서 새 파일·빈 디렉터리를 넣은2개는 재시도에서도 자료와 journal을 보존했고 완료 audit을 만들지 않았다. stage3 서버 Release publish는 경고/오류0이었다. 새 독립 fixture의 게시 HTTP 서명 E2E와 offline backup/통제 복원/retention CLI 시험이 통과했다. 이전 fixture의 다른 서버 cohort를 사용한 첫 시도는 시작 전 거부했으며 정상 제품 시험으로 계산하지 않는다.

## 설치 완료 이후 오류와 정리 결과

GUI 내부 결과에 후속 단계/원래 오류를 전달한다. commit 뒤 Launch/Integration의 비취소 오류는 완료된 설치 기록으로 남고 Check-only 재시도로 연결된다. 취소/오래된 resume를 제거하고 원래 지원 ID를 보존하며 실행 상태를 추정하지 않는다. 정리 성공은 이전 오류/지원 정보를 지우고 설치 상태를 재조회한다. 조회 실패는 정리 완료 사실을 보존한다.

Windows 관련69개와 정리 UI2개가 통과했다. 실제 portable backend가 HTTP manifest/파일을 받아 설치한 뒤 Launch/Integration callback에서 오류를 발생시킨2개는 설치 파일/Manifest와 Completed operation을 보존했다. 네 조합 실제 retry 버튼 회귀는 추가 변경/복원0건과 지원 ID 보존을 확인했다. 중간 General Release publish 및 게시 CLI build-info/최소 화면 설정 생성이 통과했다. 관리형 실제 launch와 최종 GUI 수용은 뒤의 고정 후보에서 확인한다.

2026-10-06 / 기준 b26e148 / codex/local-completion-sprint. 기존75ea591 GUI18/51은 이력이며 새 제품 후보의 수용에 합산하지 않는다.

## 재시도·선택 갱신

Troubleshoot의 최초 설정/Catalog I/O 전에 자신의 retry를 결속하고, Resume 준비 실패는 Check만 재시도하며 유효 resume를 보존한다. 실제 catalog-refresh는 선택과 설치 상태를 함께 조회한다. 요청 중 선택 변경은 거부하며 추천 버전/프로젝트 변경 직후 이전 설치 상태·runtime 설정을 무효화한다.

Windows 신규 headless11개와 관련 기존135개가 통과했다. 실제 버튼 이벤트에서 이전 변경 작업 호출0건, 설정 실패 뒤 resume 보존, A→B/추천v1→v2 Check 대기 중 이전 상태/경로 null을 확인했다. 중간 General Release publish 경고/오류0, CLI build-info 및 게시 GUI의 설정 없음 화면 시작/정상 창 종료를 확인했다. 이 중간 시작 확인은 최종51개 기능 수용이 아니다.

기준선 b26e148 snapshot의 General/Developer/Agent/server/synthetic 게시와 Windows HTTP 서명 large 프로필 준비1+측정3회를 수행했다. 입력 SHA-256 eda71c82c6bd1254b0bd81813752715ab00140feea1d347bfd8667c43281f124. 최종 후보 비교는 후속으로 기록한다.
