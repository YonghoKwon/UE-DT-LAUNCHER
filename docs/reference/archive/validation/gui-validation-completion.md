# 새 GUI 실제 수용 검증 후속

2026-09-28 / 기준 f6d5174 / codex/gui-validation-completion.

## 1. 재현 도구

- 동일 소스에서 Windows GUI/Agent/서버/합성 앱 publish. 바이너리별 SHA-256과 기준 HEAD를 fixture.json에 기록하고 제어 시 재검증한다.
- --prepare-gui --defer-v2는 v1만 승인하고 v2 job을 승인 대기로 보존한다. approve-v2는 기존 서버 승인 CLI를 호출한다.
- 지정 attempt 자연 종료, 설치/정상 백업 전체 hash 확인, preview fingerprint 변경 도구와 경로/해시 단위 검사 추가.
- 최초 readiness 시험은 CLI status를 JSON으로 오해해 실패했다. 실제 IPC status/capability 응답 검사로 수정했다.
- intake-proof-02: published Agent 준비/이미지/doctor, v2 승인 대기→수동 승인, 합성 부모 선종료/자식 유지/자연 종료와 미설치 보존 통과. 도구 단위 1개 통과. GUI 설치 버튼 시험은 아직 하지 않았다.

## 남은 수용

실제 GUI 적용 직전 승인과 사용자 협업 OS 변경이 필요하다. 실제 내레이터 음성은 사용자 선택으로 후속 보류한다. 단계별 결과를 추가하며 코드·자동화·GUI·실제 OS 결과를 서로 대신하지 않는다.
