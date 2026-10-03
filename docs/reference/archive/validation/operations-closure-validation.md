# 운영 안전성 후속 검증

2026-10-03, 기준 d756af2, codex/operations-hardening-closure. 실제 GUI/키보드 조작은 중단 상태다. 코드·단위·게시 프로세스·회사 인수를 구분한다.

## 1. Fixture와 결과 계약

새 비공개 root만 claim하고 실제 OS owner, Linux0700/Windows current-user·SYSTEM·Administrators ACL, 모든 상위 링크와 config/DB/key/policy containment, config 및 server 실행 파일 SHA-256을 검증한다. 이름은 권한 근거로 사용하지 않는다. 민감한 provisioning stdout은 저장하지 않는다.

실행 전 incomplete summary 생성·단계별 atomic checkpoint·timeout/실패/미실행·source/diff/binary hash를 추가했다. 외부 공개 요약에는 경로/원시 예외/비밀을 넣지 않는다.

Windows fixture 계약3개 및 d756af2의 기존 cohort-06 CLI/console Agent/서버로 실제 headless runner 통과. 제품 소스 수정 전 도구 검증이며 새 제품 후보의 회귀 결과로 합산하지 않는다. Windows 권한 생성은 새 root에 native icacls, 검사는 Win32 ACL API를 사용한다.

## 이후 묶음

취소/실행·캐시·정리/복원·인증/한도·점검·성능·설치본 검증을 실제 완료 후 누적한다. 이전635개 및 과거 GUI 성공은 당시 이력으로 보존한다.
