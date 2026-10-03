# 운영 안전성 후속 검증

2026-10-03, 기준 d756af2, codex/operations-hardening-closure. 실제 GUI/키보드 조작은 중단 상태다. 코드·단위·게시 프로세스·회사 인수를 구분한다.

## 1. Fixture와 결과 계약

새 비공개 root만 claim하고 실제 OS owner, Linux0700/Windows current-user·SYSTEM·Administrators ACL, 모든 상위 링크와 config/DB/key/policy containment, config 및 server 실행 파일 SHA-256을 검증한다. 이름은 권한 근거로 사용하지 않는다. 민감한 provisioning stdout은 저장하지 않는다.

실행 전 incomplete summary 생성·단계별 atomic checkpoint·timeout/실패/미실행·source/diff/binary hash를 추가했다. 외부 공개 요약에는 경로/원시 예외/비밀을 넣지 않는다.

Windows fixture 계약3개 및 d756af2의 기존 cohort-06 CLI/console Agent/서버로 실제 headless runner 통과. 제품 소스 수정 전 도구 검증이며 새 제품 후보의 회귀 결과로 합산하지 않는다. Windows 권한 생성은 새 root에 native icacls, 검사는 Win32 ACL API를 사용한다.

## 묶음별 기록 — 최신 결과와 단계별 이력 구분

## 8. 측정/순번 병목 (합성 로컬 범위)

측정 도구의 HTTP/Bearer 가정을 요청 서명·명시 승격으로 바꾸고 Catalog 서명/binding/sequence와 Range 실제 바이트를 검증했다. source/diff/binary hash·부분 실패·null 자원 지표를 보존한다. Python3.10에서도 bounded streaming SHA-256을 사용한다. 미측정을0으로 보충하지 않는다.

Windows original d756af2와 최적화 직전 final-02, 후보 final-03에 작은/큰/혼합 데이터·준비1/측정3·1/10/30을 적용했다. 90% 바이트 재사용/요청 실패0 유지. 최적화 직전→후보 API p95는7.71/26.20/109.92→8.53/11.47/30.24ms. d756af2 대비 모든 관측 중앙값은10% 이내지만, 직전 후보의1연결은10.6% 악화해 엄격한 추가 비교 gate는 미달이다. PERF-03은75%를 유지하고 유리한 부하 평균으로 덮지 않는다.

.NET counter 프로파일에서 sequence wait의 관측 p95 최대116.62ms, 실제 sequence DB 구간 p95 최대4.39ms였다(프로파일 자체는 타이밍에 영향을 줌). 한 인증 서버/루트의 수명 안에서64개 순번을 durable high-water commit 후 발급하는 후보로 대기를 줄였다. 응답·권한은 캐시하지 않으며 재시작 시 남은 번호는 건너뛴다. 정상 순번 예약은 복원 때 임의 순번 증가를 허용하는 우회 기능이 아니다. concurrent/저장 실패/commit 후 실패/재시작·취소/overflow·잘못된 DB형식 회귀와 actual published server 강제 종료→이전 challenge 거부·high-water 초과 발급을 확인했다.

Windows worker1/2(각 준비1/측정3, 동시10클라이언트) 접수 중앙값13207/6068ms, Catalog p95 175/181ms, Range p95 160/159ms, HTTP 실패0. worker2는 선택 기능이고 기본1을 유지한다. 계측 일부 시간은 겹치며 native SQLite wait나 회사 SLA가 아니다.

Ubuntu WSL1의80MiB large 구성도 같은 프로토콜·준비1/측정3으로 비교했다. API p95 7.18/29.25/90.41→7.08/10.21/26.40ms, 실패0·90% 재사용 유지. WSL psutil은 새 비공개 시험 폴더에 배포판 package를 추출해 사용했고 OS에 설치하지 않았다. Windows/WSL 시간은 서로 합산하지 않으며 request-signature와 HTTPS/Bearer 수치도 섞지 않는다. [Windows 정제 비교](operations-performance-comparison.json).

## 9. 최종 게시·설치본·CI와 인수 준비

고정 코드 기준8af82c8의 Windows final-04와 같은193개 product-source를 사후 hash 대조한 WSL copy를 확인했다. Windows/WSL 각각664개 회귀, Release warning/error0, 최신 게시 headless3개·readiness8개, WSL private nginx HTTP 서명/HTTPS Bearer를 통과했다. WSL copy에는 Git metadata가 없으므로 sourceDirty/hash 공백을 false/0으로 채우지 않으며 사후 대조를 build attestation으로 확대하지 않는다. [정제 실행 증거](operations-closure-evidence.json).

양 OS의 저장 경계36개 프로세스 강제 종료·native 후손·broker/재시작·health500/timeout/연결 실패·읽기 전용 inspect/dry-run·migration apply 거부·정확한 service 대상A/defaultB 불변을 확인했다. 이 수명 시험은 final-02/동일 Core의 게시본 이력이며 모든 변경 상태/진입점의 전수 또는 정전 내구성 주장은 하지 않는다. 최종 변경은 서버 sequence/검증 경계이고 최신 게시 headless/인증 회귀를 다시 수행했다.

Windows 개발 MSI 생성→비설치 CAB 추출→GUI/Agent SHA 대조와 공식 preflight/failure 계약11개를 통과했다. Ubuntu RPM은 실행별 고유 디렉터리·정확한 package·data-only newc 경로/링크/중복/크기 제한·payload SHA·config0640/noreplace·credential root:uedt0750·state/log uedt0750을 검사했다. 중복 build-id 경고는 _build_id_links none으로 해소했다. 실제 install/scriptlet·서비스/계정 생성·회사 인증서는 수행하지 않았다. NuGet vulnerability 조회는 전 프로젝트0이었다.

Stable workflow는 동일 commit functional gate와 signer 조건을 모두 요구하며 RPM key DB/GPG home은 실행별로 격리하고 정확한 fingerprint/파일만 서명한다. fast 계약·readiness/headless/restart 증거를 CI에 연결했지만 원격 실행은 미검증이다. 보관은 allowlist 요약 JSON/7일이며 키/DB/원시 log/CSV는 업로드하지 않는다.

회사 인수 도구를 실제 개발 MSI/RPM에 대해 실행해 **미실행** 계획을 생성했다. install→service/권한→정확한 연결→upgrade→repair→승인 후 재부팅→uninstall/데이터 보존 순서이며 현재 호스트에서 실행하지 않는다. 계획 생성은 lifecycle 통과 증거가 아니다.

### 남은 검증

- 실제 GUI 취소/오류/복원 수용과 내레이터, 실제 UE 사용자 데이터/회사 RHEL/망·서비스 계정, 실제 서명 인증서와 guest 설치 수명주기·원격 CI.
- Windows1연결 추가 성능 수용과 nginx 대용량, 모든 변경/취소/재개/정리/복원 장애 조합. 열린 writer/실제 업로드 계정 인수.
- 복원 시 과거 signing identity, 원본 완전 유실과 회사 RPO/RTO, cache 데이터의 명시 수명 정책. discard는 live 기록 슬롯 회복이며 다른 owner 캐시를 삭제하지 않는다.

완료6/부분19/대기3(열린22)를 유지한다. 새 GUI/회사 인수·OPS-08 실제 인증서/OPS-06 원격 CI 상한을 자동 상향하지 않았다. push/PR은 수행하지 않았다.

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
