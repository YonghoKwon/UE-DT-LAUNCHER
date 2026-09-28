# 관리형 GUI 안전성 검증

2026-09-28 / 기준 28c4019 / codex/managed-gui-safety-validation.

## 1. 시험 환경

- `tools/SyntheticGuiApp`는 shell/네트워크/사용자 데이터 접근 없이 marker와 자기 자식 수명만 사용하는 테스트 전용 WinExe이다. 공식 설치본에 포함하지 않는다.
- `test-intranet-auth.py --prepare-gui <exe>`는 서명 HTTP 서버에 두 버전을 승인하고 console Agent·일반/개발자 설정을 준비하되 클라이언트에 설치하지 않는다.
- `gui-fixture-control.py`는 해당 격리 root의 상태/hash 관측·데이터 파일 손상·정상 자식 종료·자기가 띄운 Agent 제어만 제공한다. GUI 버튼 호출을 대체하지 않는다.
- `publish/gui-safety/prep-smoke`에서 준비 성공, 부모 선종료/자식 유지/정상 종료, client/apps 미생성 확인. 개발 도구 Release publish 경고/오류 0. 준비 후 테스트 서버·Agent 종료.

## 2. 상태 수정과 현재 검증

- 실행 후 Agent runtime-inspect로 Running/Pending/Unknown/누락을 반영하고 기본 버튼을 비활성화한다. 설치 결과의 구조화 버전 정보도 반영한다.
- 문제 해결의 typed runtime 오류를 유지하고 검사 전/후 runtime을 확인한다. 실제 repair 실패 + backup 존재일 때만 rollback을 제안하며 runtime 차단에서는 제안하지 않는다.
- Windows/WSL 전체 각 393개 통과. 최종 버전 정보 갱신 보강 후 관련 8개 재통과, Windows publish 경고/오류 0.
- 실제 일반 GUI의 업데이트 서비스 정상·미설치·설치 후 실행 버튼을 관측했다. 일반 프로필은 현행 정책으로 최신 v2를 선택하므로, 실제 시험은 일반 v2 첫 설치 → 개발자 v1 정확 선택 순서로 수행한다. 정책을 임의 변경하지 않는다.
- 이 단계에서는 설치 버튼 직전 Computer Use 확인을 요청하고 대기했다. 이후 사용자 승인과 실제 결과는 3절을 따른다.

## 3. 일반 GUI 설치·실행 실제 통과

- 사용자 실행 직전 승인 후 `publish/gui-safety/actual`의 실제 설치 후 실행 버튼을 눌렀다. 첫 자동화 입력의 geometry 오류는 재관측 후 screenshot 좌표 입력으로 재시도했으며 설치는 한 번 수행됐다.
- 일반 최신판 2.0.0: 미설치 → 서명된 파일 다운로드/설치 → 합성 앱 실행. 설치 버전 2.0.0과 실행 중 안내, 기본 버튼 disabled를 접근성 트리와 화면에서 확인했다.
- 자식 marker `a239213077ab4b92bd4219252d43c6a1`, runtime attempt `07ffd84b293742f0832a6568f507c488`. GUI가 앱을 시작했으며 CLI 사전 실행으로 대체하지 않았다.
- 부모 선종료·자식 유지 중 GUI를 닫고 다시 열었다. 동일 attempt의 Running과 자식 생존을 확인했다.
- 시험 제어 신호로 자식을 정상 종료했다(프로세스 kill 없음). ended marker와 Quiescent(0) 확인 후 GUI 상태 새로고침에서 최신 상태·실행 버튼 활성 복원을 확인했다.
- 설치 Manifest의 모든 파일 SHA-256과 실제 설치 파일이 일치했으며 version.txt는 2.0.0이었다.
- 이번 추가 작업은 실행 검증·문서만이며 기존 전체 자동화 각 393개를 새로 실행한 것으로 표기하지 않는다.
- 검증 후 GUI·격리 서버·console Agent를 정상 정리했다. 제품 소스 변경 없이 검증 기록과 관리 문서만 갱신했다.

## 남은 직접 조작

1. 일반 첫 설치/실행 및 즉시 실행 중 안내: **통과**.
2. GUI가 시작한 앱의 자식 유지 중 GUI 종료·재실행, 자연 종료 확인: **통과**.
3. 실행 중 update/repair/rollback 차단과 보호 파일 hash 불변.
4. 정상 파일의 repair로 실제 backup 생성 → 테스트 version.txt 손상 → rollback 취소/확인·바이트 복원.
5. 개발자 다른 버전 설치·실행 확인 취소/승인, 일반 문제 해결의 단절/재연결·실행 중 차단.

portable GUI의 버전별 rollback 경로는 별도 보완이 남는다. 회사 UE/RHEL·서비스 계정은 이번 범위 밖이다. OPS-09 75%, OPS-08 50%와 기존 개선 집계를 유지한다.
