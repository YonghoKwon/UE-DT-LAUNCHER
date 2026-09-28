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
