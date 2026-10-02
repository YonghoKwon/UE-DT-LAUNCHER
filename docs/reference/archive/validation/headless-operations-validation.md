# Headless 후속 운영 검증

2026-10-03, 기준 fe10488, 브랜치 codex/headless-operations. 실제 GUI/입력 조작은 수행하지 않는다. 회사 운영 인수와 분리한다.

## 1. 측정 기반

- Windows single-file/self-contained Release CLI·console Agent·서버 게시 후 합성 HTTP 요청 서명 E2E 통과.
- 기존 HTTP/Bearer 측정 가정을 제거하고 schema3/device key/명시적 promote로 전환했다. HTTPS/Bearer는 독립 검증하며 서로 다른 프로토콜의 과거 수치를 비교 기준으로 합산하지 않는다.
- 준비 1회, 측정 3회, 1/10/30 연결, Catalog+3바이트 Range, 클라이언트 서명/응답 검증 시간 포함. OS 캐시는 비우지 않았다.
- 첫 비단일 파일 게시 시험은 fixture가 CLI만 복사해 시작 실패했다. 이후 새 single-file 묶음으로 재시험했다. 첫 large 도구 실행은 Markdown 집계 형식 결함으로 종료했으며, 수정 후 새 fixture client-large-02 전체 실행이 통과했다.
- Windows 80MiB/10파일 시험: 최초 83,886,080바이트, 변경 없음 0, 다음 버전 8,388,608, force repair 83,886,080바이트. 다음 버전 90% 절감 유지. 실제 UE 크기/성능 보장이 아니다.
- psutil 20ms 샘플의 출처·누락 사유를 기록한다. 측정 불가를 0으로 보충하지 않는다. 시작/challenge 실패도 실패 그룹으로 기록한다. 계약 테스트 3개 통과.
- raw 로그·키·DB·패키지는 ignored publish/headless 아래에만 보존한다. 이 문서는 정제된 근거이며 PERF-03/SEC-04를 완료로 올리지 않는다.

## 2. Core 물리 이동

28개의 링크 컴파일 파일을 Core 폴더로 그대로 이동했다. namespace/assembly/외부 계약은 변경하지 않았다. Windows Release 기존601개 전체 통과, GUI/Agent/서버 single-file 게시 성공, 실제 게시 readiness8개 통과. 실제 GUI는 실행/조작하지 않았다. 화면 조정 책임 분리는 이후 동작 변경 단위에서 다룬다.

## 3. 이후 기록

### 조회 전용 자동 점검

scheduled-check 한 회차 CLI·기본 disabled 설정·명시 interval·중복 skip·네트워크 제한 재시도·비밀 없는 결과를 추가했다. 이 명령은 self-update/ServiceRunner/engine apply/launch를 호출하지 않는다. Windows task XML/Linux timer는 비활성 placeholder 템플릿이며 이번 호스트에는 등록하지 않았다.

Windows3개 disabled/주기 검증/중복·설치0건 unit, 게시 scheduled/proof-01 opt-in 조회/설치 상태 검사 전후 보호 inventory 불변·disabled 실행 거부 통과. 실제 OS 스케줄/서비스 계정 인수는 별도이다. WSL 중간 후보633개 및 게시 runtime-host 합성 데이터 smoke 통과(최종 cohort 검증과 분리).

### 確認형 정리

retention inspect/plan/apply: 공개판·모든 승격·pending/active 보호, 명시 선택 failed/rejected 또는 참조 없는 processing 자식만 허용. plan ID/fingerprint·참조·전체 hash 재검사, protected quarantine/journal로 중단 복구, audit/history 보존. legacy cleanup --apply는 새 plan/confirm을 안내하며 거부한다.

Windows unit5개, 게시 retention-candidate/proof-05 stale plan 거부·새 plan 임시 정리 통과. 완료 journal을 내용이 같은 새 폴더에 재사용하던 문제는 unique plan ID/재생성 거부로 수정 후 재시험했다. 회사 기간/공개판 삭제 정책은 범위 밖이다.

### 오프라인 백업·통제된 복원

shared serve/watch/ingest/publish/auth/승격과 exclusive 유지보수 OS 잠금을 연결했다. 서비스/작업자를 자동 종료하지 않는다. backup plan/create/verify, restore plan/stage/activate를 추가했다. SQLite snapshot/파일 hash/정책/서명 신원을 기록하며 private key는 포함하지 않는다. 새 빈 target만 허용하고 시작 전 staged fence를 저장한다.

활성화는 살아 있는 최신 원본의 모든 공개판·참조 snapshot을 확인하고 최신 authority DB(순번/승격/폐기/만료/audit)와 정책을 가져온다. 불명·누락이면 staged를 유지한다. 원본 완전 유실/RPO·RTO·회사 복구훈련은 보장하지 않는다.

Windows unit5개 및 게시 backup-fixed/proof-02 통과: 백업 verify/변조 거부/서버 생존 중 backup 거부/서명키 분리/staged 차단/백업 이후 폐기 기록 보존. 초기 fixture는 이전 바이너리에 maintenance lock이 없어서 create가 거부됐고, apply에서 기존 root의 잠금 파일을 안전하게 생성하도록 수정 후 재시험했다. Windows idle SQLite pool handle로 DB replace 실패도 수정 후 통과했다. 실제 회사 백업·정전 내구성 시험과 분리한다.

### 자격 수명·요청 경계

DB schema5 이전 전 SQLite snapshot 백업. 기존 token 관리 ID와 null 만료 유지. token-list/개별 revoke, PC key 명시 만료를 추가했다. 발급·폐기·최초 만료 latch와 audit은 같은 transaction이며 시계 역행으로 만료를 풀지 않는다. 명시한 요청/다운로드 한도만 활성화하며 초과 시 큐 없이429다.

관련29개 unit(신규6개 포함) 통과. Windows lifecycle/proof-01 게시 CLI·console Agent·서버에서 정상 설치/실행/repair, 명시 만료 키 거부와 durable latch, 비밀 없는 token-list·개별 폐기, 기존 키 폐기 HTTP 회귀 통과. Windows 서비스 identity·WSL·부하/한도 전수는 최종 단계와 분리한다.

### 작업 조정·취소·재개 (Windows 로컬)

Operation ID/실제 OS owner·session/정확한 릴리스/Manifest SHA-256 기록을 추가했다. Agent는 최대16연결, 변경 gate1개 유지. 단절은 취소가 아니다. 취소 요청과 완료를 구분하고 commit 이후에는 설치 완료·미실행 launch 생략 정책을 적용한다. 런처의 기존 runtime 보호를 우회하지 않는다.

새 운영 명령은 `operation status|cancel|resume|discard --id ...`이며 portable은 `--config ...`를 지정한다. 개인키·token·challenge는 기록하지 않는다. OS 작업 잠금은 종료 시 해제되지만 미완료 기록은 Interrupted로 남는다. 지속 캐시는 명시적 `performance.resumeCacheBytes` 예산과 검증된 Manifest/정확한 릴리스에만 활성화된다. 다른 Manifest/손상 캐시/예산 부족은 자동으로 정상 재사용하지 않는다.

Windows unit13개(소유권/세션/중복/저장 실패/중단 상태/commit 이후 취소/캐시 검증·독립 복사·손상·예산/다른 Manifest) 통과. 게시 proof-04의 console Agent·서버·CLI로 다운로드 단계에서 상태 조회/취소 → 작업 종료 및 설치/Manifest/backup/journal 불변 → fresh auth resume 완료 → discard를 통과했다. 첫 동시 named pipe 시험은 서버 identity의 CreateNewInstance 권한 누락으로 실패했고 ACL을 해당 Agent identity로만 보강했다. CLI control JSON 출력 결함도 수정 후 새 게시본에서 재시험했다.

GUI 취소 코드는 연결했지만 실제 클릭은 미검증이다. WSL 및 적용/복구 실패·부분 Range 전수 장애 표는 최종 통합 때 별도 기록한다. 진행률은 수용 전까지 보수적으로 유지한다.

Core 물리 이동, 취소/재개, 인증 수명, 유지보수, 백업/정리, 자동 점검, 최종 재측정 결과는 실제 검증 후 이 문서에 누적한다.
