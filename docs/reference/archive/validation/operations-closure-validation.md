# 운영 안전성 후속 검증

2026-10-03, 기준 d756af2, codex/operations-hardening-closure. 실제 GUI/키보드 조작은 중단 상태다. 코드·단위·게시 프로세스·회사 인수를 구분한다.

## 1. Fixture와 결과 계약

새 비공개 root만 claim하고 실제 OS owner, Linux0700/Windows current-user·SYSTEM·Administrators ACL, 모든 상위 링크와 config/DB/key/policy containment, config 및 server 실행 파일 SHA-256을 검증한다. 이름은 권한 근거로 사용하지 않는다. 민감한 provisioning stdout은 저장하지 않는다.

실행 전 incomplete summary 생성·단계별 atomic checkpoint·timeout/실패/미실행·source/diff/binary hash를 추가했다. 외부 공개 요약에는 경로/원시 예외/비밀을 넣지 않는다.

Windows fixture 계약3개 및 d756af2의 기존 cohort-06 CLI/console Agent/서버로 실제 headless runner 통과. 제품 소스 수정 전 도구 검증이며 새 제품 후보의 회귀 결과로 합산하지 않는다. Windows 권한 생성은 새 root에 native icacls, 검사는 Win32 ACL API를 사용한다.

## 이후 묶음

## 복원 활성화의 독립 서명 검사

원본/복사본이 동일하게 손상된 경우에도 공개되지 않도록 복원 릴리스의 Manifest 서명·서명 ID·릴리스 tuple·파일 전체 hash/inventory를 별도로 검사한다. 현재 signing identity로 확인할 수 없는 과거 키는 자동 신뢰하지 않고 staged 상태로 남긴다. 회전된 과거 공개키의 운영 인수는 별도다.

Windows 관련14개(정상/동일 손상 서명 사례 포함)와 final-02 게시 CLI·console Agent·서버의 backup/restore→인증된 설치 통과. 제품 변경 뒤 새 cohort로 재검증했고 이전 후보 결과를 대체하지 않았다. 전체 Windows/WSL 회귀는 각각660개 통과했다.

## 7. 선택/준비도 점검

Doctor의 authenticated selected release를 optional 응답에 보존·target 검증하고 schedule 설치 검사에 동일 선택을 전달한다. defaultB와 exactA를 혼합하지 않으며 preparation/inspection으로 checked/action-required/verification-pending을 결정한다. protected config default1과 schedule exact2를 실제 게시 fixture로 대조했고 설치 inventory는 불변이다. Windows 관련21개 및 boundaries-01 publish 통과. GUI actual input은 중단 상태다.

## 6. 인증/한도 게시 경계

Request limit에 내부 monotonic clock, actual Kestrel 파일 dispatch barrier seam을 추가했다(제품 설정/환경변수로 활성화 불가). held download N/N+1의429/Retry-After·정상 완료 슬롯 반환·폐기 후401, window 회복, endpoint 만료/clock rollback·expiry audit rollback/동시1회 latch를 확인했다.

관련48개 Windows 회귀 및 boundaries-01 publish headless 만료/폐기/복원/점검 통과. 모든 회사 경로·계정/부하·HEAD/image 권한 전수는 최종 matrix/회사 인수와 구분한다.

## 5. 복원 정합성

target 전체 stage를 private directory/exclusive lease로 예약하고 shared live lease는 staged fence를 검사한다. 모든 store/serve/watch/쓰기 진입점이 보호된다. source/target 잠금은 canonical path 순서다. 복사 후 hash, current DB integrity, jobs.source/snapshot·active_work·release의 양방향 파일 목록/hash, latest policy를 검증한다. 의도된 삭제 ledger는 source 부재와 함께 인정하고 이전 backup으로 삭제 자료를 다시 공개하지 않는다. incomplete retention은 backup을 거부한다.

Windows16개 정리/복원 회귀 및 restore-01 실제 게시 흐름 통과. 도구는 staged serve 거부와 private signing key 내용 부재를 assertion으로 확인하며, 새로운 허용 키로 활성화된 서버의 Catalog/Manifest/파일 설치를 인증·서명 검증해 확인했다. 회사 복구/완전 유실은 별도다.

## 4. 정리 상태/삭제 이력

move intent/quarantined/delete intent/deleted를 후보별로 원자 기록한다. OS directory identity에 결속하고 이동 후에는 source를 재수집하지 않는다. 부분 삭제는 승인 목록의 잔여 파일만 hash 검사하고 진행한다. quarantine parent는 신규 생성 시 비공개 owner/ACL/mode, 기존 영역은 검증한다. 완료한 삭제는 schema6 maintenance_deletions에 유지한다.

Windows16개 정리/승격 회귀와 retention-01 게시 headless 통과. 삭제/파일별 삭제 경계 종료 후 같은 경로·내용의 새 source를 보존했고 이동 전 재생성은 거부했다. schema5→6은 서버 정지·pre-v6 SQLite backup 후 전환하며 구 서버 동시 사용을 금지한다. 실제 업로드 계정과 열린 writer의 회사 인수는 별도다.

## 3. 재개 캐시/기록 수명

Cache schema2는 trusted IPC requester 또는 portable 현재 owner/session별 partition에 저장한다. Agent 서비스 계정으로 사용자 캐시를 결속하지 않는다. 이전 cache는 자동 채택/삭제하지 않는다. 설치별 content quota에 partial/verified/new를 예약하며, 소유가 확인된 copy temporary만 정리한다. 사용 불가/예산 부족은 네트워크 처리로 전환한다.

명시 discard는 비활성 기록을 archive로 옮겨 live4096 슬롯을 회복한다. archived ID 재사용·활성 discard는 거부하고 OS lock inode/감사 이력을 유지한다. Windows 관련18개, resume-01 게시 CLI/Agent/서버 흐름 통과.

## 2. 취소와 실행 경계

작업 등록 ACK capability와 공통 coordinator로 등록 전 취소 의도를 보존한다. 서버가 취소를 승인하고 workers/복구가 끝나야 terminal을 반환한다. 설치 commit은 transaction 경계에서 확정하고 이후 상태 조회 실패를 설치 취소로 표시하지 않는다. Backend 인터페이스에 token/결과 종류를 넣어 실행·문제 해결 repair에서 같은 경로를 사용한다.

실행은 host 시작 전까지 취소를 검사하고 원래 requester/attempt·ticket에 결속된 unstarted abort만 허용한다. host 생성 후에는 사용자 취소로 중단을 주장하지 않는다. 설치 후 실행 생략/상태 재확인 필요를 분리한다.

관련18개 Windows 자동화와 cancellation-01 게시 CLI/Agent/서버 headless 통과. 실제 GUI는 조작하지 않았다. 추가 token 전달/취소 안내 보완은 같은 소스의 최종 게시 cohort에서 재검증한다.

취소/실행·캐시·정리/복원·인증/한도·점검·성능·설치본 검증을 실제 완료 후 누적한다. 이전635개 및 과거 GUI 성공은 당시 이력으로 보존한다.
