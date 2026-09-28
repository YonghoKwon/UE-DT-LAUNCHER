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

## 남은 수용

최종 동일 소스 게시본의 관리형/portable 적용, 오류 조합, 실제18개 OS 조합과 최악 조건을 별도로 확인한다. OS 변경/원복은 사용자 협업, 설치/복원은 실행 직전 확인을 유지한다. 내레이터 실제 음성은 사용자 선택으로 보류하며 UI-03은75%를 유지한다.

## 3. 글자·포커스·작은 화면

- Expander/Tab 제목에 공통 글자 배율을 적용했다. 열린 dialog 고대비 전환에서 focus3px·primary 색/글자 대비를 즉시 유지한다.
- headless에서 200%/854×400 오류 상태의 중앙영역0 DIP와640×360의9 DIP를 재현했다. 상태 제목까지 제한 스크롤로 옮기고 주 버튼을 고정해 해결했다. 작업 상세를 Tab/PageDown으로 읽을 수 있다.
- 개발자 Shift+Tab이 템플릿 헤더를 건너뛰던 문제를 Local 탐색으로 보완했다. 프레임워크 실제 peer identity/NameProperty 이벤트와 byte tick 억제를 검사한다.
- 관련 headless72개 통과(신규44개). 통합 Windows500개 및 Release 경고/오류0. 실제 Windows portable-preflight-02에서 글자200%, 확대된 도움말 제목/작업 버튼/본문과 Tab 주 버튼 focus를 확인했다. 사용자 글자 설정은 hash guard를 거쳐 원본으로 복원했다.
- 위 실제 확인은1920×1080/100%의 사전 확인이며 최종18개 OS 조합·OS 고대비·내레이터 음성 증거가 아니다.
