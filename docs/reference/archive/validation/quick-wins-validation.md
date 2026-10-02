# 빠른 개선 — 비대화형 검증

2026-10-03 KST. 기준9a87b52 → codex/launcher-quick-wins. 실제 GUI·마우스·키보드를 조작하지 않았다. 기존 열린 fixture/회사 설정·Unreal 소스는 변경하지 않았다.

## 완료한 묶음

| 단계 | 결과 |
|---|---|
| 문서 정합성4a37061 | 최신 요약/항목표/체크포인트와 게시본별 이력 일치. 보류 GUI 체크리스트를 현재 가이드 한 곳으로 통합 |
| 진단 수정f215e62 | 요청 target으로 누락 응답을 보충하지 않음. transport→상위 merge→표시까지 deferred 근거 유지. null/대상 불일치/성공 모순은 정제 오류. 원래 실패 코드/지원 ID 보존 |
| CI 연결9788de1 | Windows/Linux publish 준비도 검증, 독립 미승격 시나리오, 유일한 임시 root/IPC, 실패 단계·허용필드 요약·7일 보관 |

외부 필수 필드·설정 schema·IPC v1·CLI 종료 코드는 유지한다. 진단 창의 예외 처리에서 조치 문장이 반복되는 경로를 제거했다.

## 검증 결과

| 검증 | 결과 |
|---|---|
| 관련 진단/실제 IPC/headless UI 회귀 | 33개 통과 |
| 전체 Windows 회귀 | 601 통과·실패0·skip0 |
| 전체 WSL Ubuntu 회귀 | 601 통과·실패0·skip0 |
| Windows Release solution build | 경고0·오류0 |
| Windows/WSL CLI·Agent·서버 publish | 성공·출력에 경고/오류 없음 |
| Windows 동일 CI readiness 명령 | 8개 통과 |
| WSL 동일 CI readiness 명령 | 8개 통과 |
| 요약 허용필드·민감값·불완전 성공 계약 | Python3개 통과 |
| 기존 GUI fixture 도구 계약 | Python13개 통과·실제 GUI 조작 없음 |
| 실패 경로 | 누락 실행 파일의 실행은 실패·준비 단계failed1/나머지not-run7. 원시 경로/예외/로그 미포함 |
| workflow YAML | 로컬 파싱 성공. 원격 Actions 실행은 미검증 |

실제 named pipe/Unix socket 시험은 구형 status 응답에 read-only-doctor-v1이 없을 때 doctor 호출0건을 확인했다. capability가 있지만 Target이 누락된 응답은 transport와 상위 merge 후에도 Target=null·verification-pending이었다. 정상/설정 오류·누락/알 수 없는 상태·null 목록/항목·모순 검사 및 조치 중복을 검증했다.

게시 프로세스 readiness는 오프라인 양 모드 client inventory 불변, 미승격 대기, exact 승인판, 미허용 프로젝트, 2버전 설치/실행/repair, 관리형 진단, 폐기 키 거부를 확인했다. 이는 합성 CLI 시험이며 실제 GUI/UE/RHEL/회사 계정 검증이 아니다.

제품 소스는f215e62이다. Windows 게시본은 그 코드가 작업 중이던4a37061에서 빌드했으며 이후 제품 소스 변경 없이 커밋했다. 최종 정제 요약의 source는f215e62로 대조했다. WSL mirror는 /tmp/uedt-data-safety.4H0Vg4이며 같은 제품 코드의601개·publish·synthetic runtime-host smoke를 확인했다. 시험 도구9788de1과 제품 출처를 구분한다.

Windows SHA-256:

| 제품 | SHA-256 |
|---|---|
| GUI/CLI | 9cbdbbde6cdd12cff7e0a044abca309f3ab0588f38ba667b0e64fa10defee8be |
| Agent | ef8801f5549b93eaab888dd3763e8c59f5469b8ad3ff66ec5389c47949c33c0f |
| 서버 | 81431f967747b486e5d7beaf7e400ad52d50cf9db95501dfb06804a2dd1da7e2 |

처음 새 회귀1건은 collection 참조를 record 전체와 비교한 테스트 기대값 문제로 실패했다. 준비도/대상/검사 값별 비교로 수정한 뒤 전체 회귀를 통과했다. 첫 WSL 요약에 잘못 전달한 source-sha는 채택하지 않고 실제f215e62로 다시 실행·대조했다. 실패/수정 이력을 성공으로 덮지 않는다.

## CI 보관과 남은 조건

run-readiness-regression은 private root를 temp에 만들고 원시 stdout/stderr·키·설정은 그 안에 둔다. 업로드는 publish/readiness-summary/summary.json 한 파일만, 소스40hex·OS·고정된 검사명/상태/개수만 새 객체에 담아7일 보관한다. private 자료/예외 전문을 정제 요약에 복사하지 않는다. 공유 CI 시간은 성능 수용 기준이 아니다.

USER-02·UI-02는 실제 GUI 중단 때문에75%, OPS-06은 원격 CI 결과가 없어50% 유지한다. 항목수는28개 중 완료6·부분11·대기11, 열린22개다. 이번 세부 작업 완료는 회사 운영 승인·다른 보안/성능/UE 항목 완료를 의미하지 않는다. [정제 결과](quick-wins-evidence.json)
