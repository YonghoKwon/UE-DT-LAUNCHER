# 새 GUI 실제 수용 검증 후속

2026-09-28 / 기준 f6d5174 / codex/gui-validation-completion.

## 1. 재현 도구

- 동일 소스에서 Windows GUI/Agent/서버/합성 앱 publish. 바이너리별 SHA-256과 기준 HEAD를 fixture.json에 기록하고 제어 시 재검증한다.
- --prepare-gui --defer-v2는 v1만 승인하고 v2 job을 승인 대기로 보존한다. approve-v2는 기존 서버 승인 CLI를 호출한다.
- 지정 attempt 자연 종료, 설치/정상 백업 전체 hash 확인, preview fingerprint 변경 도구와 경로/해시 단위 검사 추가.
- 최초 readiness 시험은 CLI status를 JSON으로 오해해 실패했다. 실제 IPC status/capability 응답 검사로 수정했다.
- intake-proof-02: published Agent 준비/이미지/doctor, v2 승인 대기→수동 승인, 합성 부모 선종료/자식 유지/자연 종료와 미설치 보존 통과. 도구 단위 1개 통과. GUI 설치 버튼 시험은 아직 하지 않았다.

## 2. 상태·복원 대상

- IPC v1의 선택적 PreviousInstallation은 인증된 같은 track의 bounded 설치 기록만 표시한다. 선택 버전의 기존 필드 의미는 유지하며 늦은 응답은 현재 선택과 대조한다.
- portable rollback도 정확한 버전 경로와 preview fingerprint를 기존 설치 lease에서 확인한다. 취소 전에는 작업 시작 표시를 변경하지 않는다. Catalog typed 오류·지원 ID와 재시도 버튼을 보존한다.
- 과대 metadata의 InvalidDataException 누락을 회귀에서 발견해 수정. 신규 이전 설치/기존 오류·복원 관련 14개 통과, 기존 전체 428개 통과(신규 테스트 추가 전). Windows publish GUI 시작·서비스 단절 안내, 실제 console Agent broker/preview/변조 거부/복원 PASS (`uedt-broker-proof-i6cjafyk`). GUI 복원 적용 증거는 아직 아니다.

## 남은 수용

실제 GUI 적용 직전 승인과 사용자 협업 OS 변경이 필요하다. 실제 내레이터 음성은 사용자 선택으로 후속 보류한다. 단계별 결과를 추가하며 코드·자동화·GUI·실제 OS 결과를 서로 대신하지 않는다.
