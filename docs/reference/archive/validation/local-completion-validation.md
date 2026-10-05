# 로컬 기능·운영·GUI 수용 마무리

2026-10-06 / 기준 b26e148 / codex/local-completion-sprint. 기존75ea591 GUI18/51은 이력이며 새 제품 후보의 수용에 합산하지 않는다.

## 재시도·선택 갱신

Troubleshoot의 최초 설정/Catalog I/O 전에 자신의 retry를 결속하고, Resume 준비 실패는 Check만 재시도하며 유효 resume를 보존한다. 실제 catalog-refresh는 선택과 설치 상태를 함께 조회한다. 요청 중 선택 변경은 거부하며 추천 버전/프로젝트 변경 직후 이전 설치 상태·runtime 설정을 무효화한다.

Windows 신규 headless11개와 관련 기존135개가 통과했다. 실제 버튼 이벤트에서 이전 변경 작업 호출0건, 설정 실패 뒤 resume 보존, A→B/추천v1→v2 Check 대기 중 이전 상태/경로 null을 확인했다. 중간 General Release publish 경고/오류0, CLI build-info 및 게시 GUI의 설정 없음 화면 시작/정상 창 종료를 확인했다. 이 중간 시작 확인은 최종51개 기능 수용이 아니다.

기준선 b26e148 snapshot의 General/Developer/Agent/server/synthetic 게시와 Windows HTTP 서명 large 프로필 준비1+측정3회를 수행했다. 입력 SHA-256 eda71c82c6bd1254b0bd81813752715ab00140feea1d347bfd8667c43281f124. 최종 후보 비교는 후속으로 기록한다.
