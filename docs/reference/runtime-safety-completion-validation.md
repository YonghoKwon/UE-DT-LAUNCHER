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
