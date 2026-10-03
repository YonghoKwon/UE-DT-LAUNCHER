# 운영 안전성 후속 검증

2026-10-03, 기준 d756af2, codex/operations-hardening-closure. 실제 GUI/키보드 조작은 중단 상태다. 코드·단위·게시 프로세스·회사 인수를 구분한다.

## 1. Fixture와 결과 계약

새 비공개 root만 claim하고 실제 OS owner, Linux0700/Windows current-user·SYSTEM·Administrators ACL, 모든 상위 링크와 config/DB/key/policy containment, config 및 server 실행 파일 SHA-256을 검증한다. 이름은 권한 근거로 사용하지 않는다. 민감한 provisioning stdout은 저장하지 않는다.

실행 전 incomplete summary 생성·단계별 atomic checkpoint·timeout/실패/미실행·source/diff/binary hash를 추가했다. 외부 공개 요약에는 경로/원시 예외/비밀을 넣지 않는다.

Windows fixture 계약3개 및 d756af2의 기존 cohort-06 CLI/console Agent/서버로 실제 headless runner 통과. 제품 소스 수정 전 도구 검증이며 새 제품 후보의 회귀 결과로 합산하지 않는다. Windows 권한 생성은 새 root에 native icacls, 검사는 Win32 ACL API를 사용한다.

## 이후 묶음

## 2. 취소와 실행 경계

작업 등록 ACK capability와 공통 coordinator로 등록 전 취소 의도를 보존한다. 서버가 취소를 승인하고 workers/복구가 끝나야 terminal을 반환한다. 설치 commit은 transaction 경계에서 확정하고 이후 상태 조회 실패를 설치 취소로 표시하지 않는다. Backend 인터페이스에 token/결과 종류를 넣어 실행·문제 해결 repair에서 같은 경로를 사용한다.

실행은 host 시작 전까지 취소를 검사하고 원래 requester/attempt·ticket에 결속된 unstarted abort만 허용한다. host 생성 후에는 사용자 취소로 중단을 주장하지 않는다. 설치 후 실행 생략/상태 재확인 필요를 분리한다.

관련18개 Windows 자동화와 cancellation-01 게시 CLI/Agent/서버 headless 통과. 실제 GUI는 조작하지 않았다. 추가 token 전달/취소 안내 보완은 같은 소스의 최종 게시 cohort에서 재검증한다.

취소/실행·캐시·정리/복원·인증/한도·점검·성능·설치본 검증을 실제 완료 후 누적한다. 이전635개 및 과거 GUI 성공은 당시 이력으로 보존한다.
