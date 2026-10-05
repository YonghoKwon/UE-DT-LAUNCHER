# 로컬 기능·운영·GUI 수용 마무리

## 설치 완료 이후 오류와 정리 결과

GUI 내부 결과에 후속 단계/원래 오류를 전달한다. commit 뒤 Launch/Integration의 비취소 오류는 완료된 설치 기록으로 남고 Check-only 재시도로 연결된다. 취소/오래된 resume를 제거하고 원래 지원 ID를 보존하며 실행 상태를 추정하지 않는다. 정리 성공은 이전 오류/지원 정보를 지우고 설치 상태를 재조회한다. 조회 실패는 정리 완료 사실을 보존한다.

Windows 관련69개와 정리 UI2개가 통과했다. 실제 portable backend가 HTTP manifest/파일을 받아 설치한 뒤 Launch/Integration callback에서 오류를 발생시킨2개는 설치 파일/Manifest와 Completed operation을 보존했다. 네 조합 실제 retry 버튼 회귀는 추가 변경/복원0건과 지원 ID 보존을 확인했다. 중간 General Release publish 및 게시 CLI build-info/최소 화면 설정 생성이 통과했다. 관리형 실제 launch와 최종 GUI 수용은 뒤의 고정 후보에서 확인한다.

2026-10-06 / 기준 b26e148 / codex/local-completion-sprint. 기존75ea591 GUI18/51은 이력이며 새 제품 후보의 수용에 합산하지 않는다.

## 재시도·선택 갱신

Troubleshoot의 최초 설정/Catalog I/O 전에 자신의 retry를 결속하고, Resume 준비 실패는 Check만 재시도하며 유효 resume를 보존한다. 실제 catalog-refresh는 선택과 설치 상태를 함께 조회한다. 요청 중 선택 변경은 거부하며 추천 버전/프로젝트 변경 직후 이전 설치 상태·runtime 설정을 무효화한다.

Windows 신규 headless11개와 관련 기존135개가 통과했다. 실제 버튼 이벤트에서 이전 변경 작업 호출0건, 설정 실패 뒤 resume 보존, A→B/추천v1→v2 Check 대기 중 이전 상태/경로 null을 확인했다. 중간 General Release publish 경고/오류0, CLI build-info 및 게시 GUI의 설정 없음 화면 시작/정상 창 종료를 확인했다. 이 중간 시작 확인은 최종51개 기능 수용이 아니다.

기준선 b26e148 snapshot의 General/Developer/Agent/server/synthetic 게시와 Windows HTTP 서명 large 프로필 준비1+측정3회를 수행했다. 입력 SHA-256 eda71c82c6bd1254b0bd81813752715ab00140feea1d347bfd8667c43281f124. 최종 후보 비교는 후속으로 기록한다.
