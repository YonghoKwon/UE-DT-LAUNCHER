# UI 수용 최종화 실행 기록

기준: 8ed01cc / codex/ui-acceptance-finalization / 2026-09-28.

## 1. 관리형·portable 시험 기반

- GUI fixture를 managed/portable로 분리했다. Portable은 Agent를 시작하지 않고 --storage portable 및 전용 portable-private 경로를 사용한다. v1만 승인하고 v2는 승인 대기로 둔다.
- 게시본을 fixture 안에 고정하고 실행 파일·허용된 서버 runtime sidecar 해시를 준비/재개/제어에서 검사한다. 기존 fixture나 다른 게시본으로 덮어쓰지 않는다.
- 준비·재개는 같은 readiness와 OS harness 잠금을 사용한다. 401/403/빈 목록/신뢰키 불일치/서버 단절은 시험 root 및 harness 소유 프로세스만 변경한다.
- Python 11개, 구문/diff 검사 통과. 첫 portable 준비는 e_sqlite3.dll 누락으로 실패했다. 서버 sidecar allowlist 복사·hash 검사를 추가한 portable-preflight-02에서 서버 등록·승인 대기·portable credential 준비 성공.
- 실제 publish 일반 GUI에서 Agent 없이 로컬 모드·미설치·최신 v1 표시, apps 폴더 없음 확인. 화면 진단: 1920×1080, 작업영역1920×1032, RenderScaling1, client1120×740, 글자100%, 고대비false.
- 이 준비 게시본(candidate1)은 부분 구현 중 소스로 만든 preflight이며 최종 수용 게시본 성공으로 간주하지 않는다. 설치 버튼은 아직 누르지 않았다.

## 남은 수용

최종 동일 소스 게시본의 관리형/portable 적용, 오류 조합, 실제18개 OS 조합과 최악 조건을 별도로 확인한다. OS 변경/원복은 사용자 협업, 설치/복원은 실행 직전 확인을 유지한다. 내레이터 실제 음성은 사용자 선택으로 보류하며 UI-03은75%를 유지한다.
