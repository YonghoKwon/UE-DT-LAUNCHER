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

### 작업 조정·취소·재개 (Windows 로컬)

Operation ID/실제 OS owner·session/정확한 릴리스/Manifest SHA-256 기록을 추가했다. Agent는 최대16연결, 변경 gate1개 유지. 단절은 취소가 아니다. 취소 요청과 완료를 구분하고 commit 이후에는 설치 완료·미실행 launch 생략 정책을 적용한다. 런처의 기존 runtime 보호를 우회하지 않는다.

새 운영 명령은 `operation status|cancel|resume|discard --id ...`이며 portable은 `--config ...`를 지정한다. 개인키·token·challenge는 기록하지 않는다. OS 작업 잠금은 종료 시 해제되지만 미완료 기록은 Interrupted로 남는다. 지속 캐시는 명시적 `performance.resumeCacheBytes` 예산과 검증된 Manifest/정확한 릴리스에만 활성화된다. 다른 Manifest/손상 캐시/예산 부족은 자동으로 정상 재사용하지 않는다.

Windows unit13개(소유권/세션/중복/저장 실패/중단 상태/commit 이후 취소/캐시 검증·독립 복사·손상·예산/다른 Manifest) 통과. 게시 proof-04의 console Agent·서버·CLI로 다운로드 단계에서 상태 조회/취소 → 작업 종료 및 설치/Manifest/backup/journal 불변 → fresh auth resume 완료 → discard를 통과했다. 첫 동시 named pipe 시험은 서버 identity의 CreateNewInstance 권한 누락으로 실패했고 ACL을 해당 Agent identity로만 보강했다. CLI control JSON 출력 결함도 수정 후 새 게시본에서 재시험했다.

GUI 취소 코드는 연결했지만 실제 클릭은 미검증이다. WSL 및 적용/복구 실패·부분 Range 전수 장애 표는 최종 통합 때 별도 기록한다. 진행률은 수용 전까지 보수적으로 유지한다.

Core 물리 이동, 취소/재개, 인증 수명, 유지보수, 백업/정리, 자동 점검, 최종 재측정 결과는 실제 검증 후 이 문서에 누적한다.
