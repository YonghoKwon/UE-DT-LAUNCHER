# 클라이언트 작업 수명·GUI 수용 검증

2026-10-03, 기준 `7ead8bd`, 브랜치 `codex/client-workflow-completion`.

## 작업 수명 수정

실제 작업 시작 시 취소 버튼 표시, 종료 상태 해제 후 화면 재구성, 취소 요청 중 중복 입력 방지, 공통 재개 조건 및 작업 세대별 진행 이벤트를 적용했다. 정상 Running은 변경을 차단하면서 오류/지원 ID를 생성하지 않는다. 내부 설정 읽기 경계는 테스트에서만 대체하며 운영 경로는 기존 보호 설정을 사용한다.

Windows 전체695개(기존667+신규28) 통과. 신규 headless 시험은 양 에디션/모드의 성공·실패·취소·초기 취소·commit 후 실행 생략, 즉시 재개, 늦은 진행 이벤트와 개발자 확인 취소/Escape/닫기를 검사한다. 같은 소스의 candidate-01 일반/개발자·Agent·서버를 publish했다. 게시 CLI/console Agent HTTP 요청 서명 시험에서 설치/실행·repair·취소/재개·준비도·폐기 키 거부가 통과했다.

제품 소스 hash: `10cced9cff068b12fab0e3c0227f7016eff2f1053c050291593da4ebbc8adc70`.
General SHA-256: `c4aef4bba261eb6f6300f15f4c58599a139aad64dd5ac27829706a93b070cf62`.
Developer SHA-256: `e7a6b69db66edaa9ce691c368b0f40a20132afa8ad126644746b99915d086211`.

新규 실제 GUI 네 조합·제한 전송/Range·WSL 회귀는 다음 단계에서 확인하며, 이전 후보 성공을 이번 전체 수용으로 합산하지 않는다. USER-02/UI-02는75%, USER-03은50%, UI-03은75%를 유지한다.
