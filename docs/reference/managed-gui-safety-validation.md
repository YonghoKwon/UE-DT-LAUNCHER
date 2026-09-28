# 관리형 GUI 안전성 검증

2026-09-28 / 기준 28c4019 / codex/managed-gui-safety-validation.

## 1. 시험 환경

- `tools/SyntheticGuiApp`는 shell/네트워크/사용자 데이터 접근 없이 marker와 자기 자식 수명만 사용하는 테스트 전용 WinExe이다. 공식 설치본에 포함하지 않는다.
- `test-intranet-auth.py --prepare-gui <exe>`는 서명 HTTP 서버에 두 버전을 승인하고 console Agent·일반/개발자 설정을 준비하되 클라이언트에 설치하지 않는다.
- `gui-fixture-control.py`는 해당 격리 root의 상태/hash 관측·데이터 파일 손상·정상 자식 종료·자기가 띄운 Agent 제어만 제공한다. GUI 버튼 호출을 대체하지 않는다.
- `publish/gui-safety/prep-smoke`에서 준비 성공, 부모 선종료/자식 유지/정상 종료, client/apps 미생성 확인. 개발 도구 Release publish 경고/오류 0. 준비 후 테스트 서버·Agent 종료.
