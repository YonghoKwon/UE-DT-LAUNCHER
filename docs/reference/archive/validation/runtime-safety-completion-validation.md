> 검증 이력 보관: 실제 실행 날짜·환경·결과를 보존합니다. 아카이브 이동은 증거 폐기를 뜻하지 않습니다. 현재 진행률은 [개선 대장](../../../../IMPROVEMENTS.md), 현재 사용 절차는 [문서 색인](../../README.md)을 따릅니다.

# 실행·설치 안전성 후속 검증

2026-09-28 / 기준 8eda693 / codex/runtime-safety-completion. 회사 운영 승인과 별도.

## 1. 실행 기록과 요청 검증

- 필수/중복/상태별 필드 검증, Unknown 차단, 복구 요청 선검증, inspect/dry-run 무생성.
- Windows 전체 357개 통과. nullable 경고 수정 뒤 runtime 관련 27개 재통과, publish 경고/오류 0.
- publish CLI `tools/test-runtime-validation.py` 통과: 임시 root에서 inspect/dry-run 후 config 외 파일 없음, state 없는 기록 Unknown, 잘못된 service 선택 거부 후 원본 불변.
- 증거: `uedt-runtime-validation-p1wvkw7z`. 원시 fixture는 Git 제외.

## 2. 서비스 상태와 저장 경계

- schema 2 단일 service snapshot, startup/health 선행 barrier, 이전·대상 설치 순서 잠금, 정확한 Agent project 설정 전달.
- legacy active/failure 파일은 자동 삭제/통합하지 않는다. 명시적 확인에서만 새 snapshot을 기록한다.
- runtime write/flush/replace/ACK 경계와 health/선택 최종 저장 실패 회귀 추가. runtime 관련 53개 통과.
- Windows publish portable service 실제 health 500·서비스 CLI 강제 종료·payload 자연 종료·barrier 보존·명시적 정지 복구 통과: `uedt-service-proof-zppwk584`.
- Windows publish console Agent peer/중복/실행 중 변경/재시작/host 장애 통과: `uedt-broker-proof-xve6wsfi`.
- 서비스 fixture는 loopback schema 1 unsigned 합성 앱이다. 운영 인증 검증은 별도 signed HTTP/HTTPS E2E로 수행한다.

## 3. 이전 차단과 import

- shared-install migration apply는 대상 생성 전 거부. Agent CLI도 예상 거부를 crash report로 저장하지 않는다.
- import는 source lease 이후 metadata/파일 목록 읽기, staging hash/size 재검증, non-overwrite 공개를 유지한다.
- Windows/WSL 전체 383개 통과. WSL 최초 실패는 새 dry-run 시험이 root 소유 /tmp를 자기 소유로 가정한 fixture 문제였으며 자기 소유 부모로 수정 후 통과했다. 권한 규칙을 완화하지 않았다.
- Windows publish CLI/Agent의 dry-run·apply 무변경 거부·정지 확인 후 import·중복 대상 거부 실제 통과: `uedt-migration-proof-590h_xau`.

## 4. 경합·프로세스 장애·실제 실행

- 이벤트 barrier로 Pending 저장 대 설치/rollback, 서비스 선택 대 launch/중복 복구를 직렬화하는 회귀 추가.
- 중단 transaction journal이 남은 설치의 직접 실행도 복구 전 차단한다.
- 테스트 전용 RuntimeFaultHarness를 설치본과 분리했다. 운영 CLI/환경변수에는 장애 주입·검사 우회 옵션이 없다.
- write/flush/replace/ACK 경계 36곳에서 소유한 harness 프로세스 강제 종료 후 실제 publish CLI로 관측: Windows `uedt-runtime-crash-avp0zflh`, WSL `/tmp/uedt-runtime-crash-8xx89rbf` 통과. 이 harness는 상태 저장 시험이며 native payload 수명 시험과 구분한다.
- health 500/timeout/연결 종료, service CLI 강제 종료, payload 자연 종료, 명시적 복구: Windows `uedt-service-proof-1uhyzd_q`, Linux `/tmp/uedt-service-proof-5c8aanh1` 통과.
- Agent의 요청 A/default B 구분: Windows `uedt-service-target-28bbxfcf`, Linux `/tmp/uedt-service-target-exi6txio` 통과. B 파일·상태 생성 없음.
- 최신 Windows broker에서 실행 중 update/repair/rollback 불변 및 재시작 시험: `uedt-broker-proof-fe_8oxh3`. Linux broker `/tmp/uedt-broker-proof-_0jve4d2`는 rollback 추가 이전 실행이다.
- native 후손 수명 시험: Windows `uedt-runtime-proof-f7p61fgb`, Linux `/tmp/uedt-runtime-proof-oj716xpb` 통과.
- publish 일반 GUI의 실제 정상 상태 화면은 관측했다. 설치/실행/rollback 버튼 조작은 Computer Use의 action-time 확인 요청 응답 전이므로 아직 수행하지 않았다. 이를 CLI E2E 통과로 대체하지 않는다.

## 5. 통합 상태와 남은 조건

| 검증 | 결과 |
|---|---|
| Windows/WSL .NET 전체 | 각 388개 통과, Release build/publish 경고·오류 0 |
| 기존 계약 | Windows packaging 11개, Python benchmark 계약 16개 통과 |
| Windows signed HTTP | `publish/safety-completion/e2e-win` 및 최종 `publish/safety-completion/service-e2e-win`: 설치·두 버전 실행·repair·키 폐기, v1 실행 중 v2 선택/실행 거부, 정상 종료 후 명시적 v2 선택·실행 통과 |
| Linux signed HTTP | `/tmp/uedt-intranet-ph_5lj2s` 및 최종 `/tmp/uedt-intranet-uufkvp33`의 동일 서비스 버전 전환 시험 통과 |
| Linux HTTPS/Bearer | `/tmp/uedt-distribution-e2e.FoUVjP` 통과. nginx 기본 log 경로 경고는 남았고 임시 config 시험은 성공 |
| GUI | 일반/개발자 publish 화면·버전·명령 표시와 개발자 상태 확인 조작. 설치/실행/rollback·실행 후 창 종료 직접 시험은 확인 응답 대기 |
| CI | 두 OS persistence/service/이전/집계 smoke 연결. 원격 실행은 하지 않음 |

재현: `dotnet test src/UeDtLauncher.Tests -c Release`, `python tools/check-improvement-ledger.py`. `.github/workflows/build.yml`에 각 publish 및 실제 시험 명령이 있다. fault harness는 tools에만 있으며 공식 패키지의 project reference/산출물에는 포함하지 않는다.

최종 nullable peer identity 검증도 보강했다. Windows/WSL 전체 388개 재통과 및 재publish CLI에서 null executable을 가진 peer 기록을 Unknown으로 관측했다(`uedt-runtime-validation-5uakzwhm`, `/tmp/uedt-runtime-validation-ydnbnwnv`). Linux 최신 broker는 `/tmp/uedt-broker-proof-9btcoz1b`에서 rollback 포함 차단을 재확인했다. 문서 링크·YAML 문법·28개 진행 대장 집계 검사 통과. GUI 테스트 창·임시 서버/Agent는 종료했으며 확인 후 새 fixture로 직접 조작 시험을 재개할 수 있다.

OPS-09는 75%, OPS-08은 50%를 유지한다. 현재 로컬 자동화/CLI 체크포인트는 통과했지만 GUI 직접 조작 확인과 회사 환경 검증이 남아 **로컬 수용 기준 전체 완료도 아직 선언하지 않는다**. 전체 변경 진입점의 모든 상태 조합을 실제 프로세스로 전수 시험한 것으로 표현하지 않는다. 관리자 권한이 필요한 관리형 복구 성공 경로는 portable 소유자 시험으로 대체하지 않았다.

미포함: 회사 인증서·설치 서비스 계정·실제 UE/RHEL, 전원 장애 내구성 보장, USER-01 데이터 이전, 자동 kill/handoff, SEC-03/04·PERF-03 잔여 해결, push/PR. 테스트 전용 임시 root/키/바이너리/원시 로그는 Git 제외.
