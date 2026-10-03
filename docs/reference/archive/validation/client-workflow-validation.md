# 클라이언트 작업 수명·GUI 수용 검증

2026-10-03, 기준 `7ead8bd`, 브랜치 `codex/client-workflow-completion`.

## 작업 수명 수정

실제 작업 시작 시 취소 버튼 표시, 종료 상태 해제 후 화면 재구성, 취소 요청 중 중복 입력 방지, 공통 재개 조건 및 작업 세대별 진행 이벤트를 적용했다. 정상 Running은 변경을 차단하면서 오류/지원 ID를 생성하지 않는다. 내부 설정 읽기 경계는 테스트에서만 대체하며 운영 경로는 기존 보호 설정을 사용한다.

Windows 전체695개(기존667+신규28) 통과. 신규 headless 시험은 양 에디션/모드의 성공·실패·취소·초기 취소·commit 후 실행 생략, 즉시 재개, 늦은 진행 이벤트와 개발자 확인 취소/Escape/닫기를 검사한다. 같은 소스의 candidate-01 일반/개발자·Agent·서버를 publish했다. 게시 CLI/console Agent HTTP 요청 서명 시험에서 설치/실행·repair·취소/재개·준비도·폐기 키 거부가 통과했다.

제품 소스 hash: `10cced9cff068b12fab0e3c0227f7016eff2f1053c050291593da4ebbc8adc70`.
General SHA-256: `c4aef4bba261eb6f6300f15f4c58599a139aad64dd5ac27829706a93b070cf62`.
Developer SHA-256: `e7a6b69db66edaa9ce691c368b0f40a20132afa8ad126644746b99915d086211`.

## 추가 결함과 후보 구분

candidate-02에서 관리형/portable의 실제 취소·재개와 Range 시작점을 확인했다. 초기 candidate-01은 GUI 재개 요청의 선택 필드를 IPC 검사가 거부했고, 이 불일치를 수정했다. candidate-02의 빈 Catalog에서는 이전 카드가 남아, 빈 목록 종료 시 재구성과 이전 설치/재개 표시 제거를 추가했다. 두 에디션의 빈 목록→권한 복귀 headless 회귀와 최종 final-04의 실제 관리형 빈 목록/설치0건이 통과했다.

final-04 제품 소스 hash: `2745ad210adb3992afb5b0849581455ceb8d69776a2aa943b0271c97217a87fe`. Windows/WSL 전체698개 통과. 실제 빈 화면 권한 복귀에서 Catalog만 다시 읽는 경로가 남아 설치 상태까지 읽는 조회로 수정했고, 최종 수용 후보를 final-05로 교체했다. 이전 후보의 부분 성공을 최종 전체 수용으로 합산하지 않는다.

USER-02/UI-02는75%, USER-03은50%, UI-03은75%를 유지한다. 최대화 캡처1440×852와 진단1440×900/작업영역1440×852/scale1을 대조했다. 사용자에게 차이를 안내했고 **이번에는1440×900 기능·화면 검증으로 기록**하도록 합의했다. 이전1920×1080 UI-01 수용과 별도이며 음성·회사 인수 통과를 뜻하지 않는다.

## 최종 제품 소스와 게시본

- 고정 게시본: `final-05`, 제품 소스 SHA-256 `64e958ac46731516c6435b879b8ee0eaff512f74c60d03da8bf8c48a41db9d53`.
- Windows publish 시HEAD59b99d3에 제품 변경이 있었으며 해당 변경은7cec991로 커밋했다. Linux publish는7cec991/제품 소스 clean이며 같은 제품 소스 hash다. 서로 다른 출처 상태를 clean으로 보충하지 않는다.
- Windows General `30521b41f51f21caf9e26f450041510f6aaad5acdfab199a00dd604c29da0dd4`.
- Windows Developer `c56648f25616bded17d7387635e5237e8b3ac09ad78419352b2a4ecb93479964`.
- Windows Agent `5cd3a7222e01825630363bab24fb5c1f726c4a22a916a1973a8d5d129a1055db`.
- Windows Server `6cde1faf99b136f8491d8331e2d51ce3e8abc4d78d809921804e9ac658fc4974`.
- Windows/WSL 각각698개 통과. 도구14개·프록시1개 통과. 최종 Linux publish·HTTPS/Bearer nginx E2E 통과. nginx의 기본 로그 경로 부재 alert가 있었지만 고유 시험 prefix의 설정 검사와 실제 인증/Range/폐기/2버전/repair 시험은 통과했다. 시스템 서비스 설치나 회사 프록시 검증은 아니다.
- Windows 최종 게시 HTTP 요청 서명·준비도·작업/취소·재개 E2E 첫 실행은 승인 단계IOException으로 실패했다. 새 고유 root 재시도는 통과했으며 첫 실패를 없었던 것으로 처리하지 않는다.

## final-05 실제 GUI 진행표

[정제된 결과 JSON](client-workflow-evidence.json). 원시 키·정책·DB·사용자 경로·로그는 포함하지 않는다.

네 독립 fixture는 미설치로 준비했고 최초 설치를 CLI로 대신하지 않았다. 입력 주체는 에이전트 마우스, 조건은1440×900/OS100%/앱100%/고대비 끔이다. 적용 직전 승인과 정확한 릴리스를 확인한다.

| 사례 | 관리형 일반 | 관리형 개발자 | Portable 일반 | Portable 개발자 |
|---|---|---|---|---|
| 빈 목록→권한 복귀·미설치 재연결/조회 불변 | 통과 | 통과 | 통과 | 통과 |
| 추천 미지정→관리자 승격→조회 | 통과 | exact 유지 | 통과 | exact 유지 |
| v1 최초 설치·3파일 해시·정확한 실행 | 통과 | 통과 | 통과 | 미실행 |
| GUI 닫기 직후 자식 유지·자연 종료 | 통과 | 통과 | 통과 | 미실행 |
| 개발자 실행 확인 취소·파일/marker 불변 | 해당 없음 | v1 통과 | 해당 없음 | v1 통과 |
| v2 수신 중 취소·즉시 재개 표시 | 통과 | 통과 | 통과 | 미실행 |
| Range 재개·4파일 해시·자동 실행0건·v1 불변 | 통과 | 미실행 | 미실행 | 미실행 |
| v2 명시 실행·실행 중 보호 | 미실행 | 미실행 | 미실행 | 미실행 |
| 손상 복구→정상 추가backup→복원 취소/적용 | 미실행 | 미실행 | 미실행 | 미실행 |
| preview 변경 거부·오류별 재시도 | 미실행 | 미실행 | 미실행 | 미실행 |

관리형 일반v2 취소 직후 프록시 수신량9,404,416바이트, 재개 Range 시작9,371,648을 관측했다. 경계 버퍼 차이를 합산하지 않고 실제Range 및최종4파일 해시를 검증했다. 취소 완료에 즉시 재개 버튼, 재개 완료에 취소/재개 버튼 제거와 ‘다운로드 재개 및 검증 완료’를 확인했다. v2 marker0건이며runtime Quiescent다.

관리형 개발자v2는10,649,600바이트, portable 일반v2는9,404,416바이트의 제한 전송 중 취소했으며 작업자 종료(activeFiles=0)·즉시 재개 표시·v1 inventory 불변을 확인했다. 재개 적용과 최종 해시 판정은 아직 별도 대기다. Portable 개발자의v1 실행 확인 취소도 미설치 inventory 불변·marker0건을 확인했다.

유효한 창 종료 수명 증거의 합성 marker: 관리형 일반`b223a359d57a4ad79af66f8ff5af4112`, 관리형 개발자`88c54a8b7cb04b4d8fa5e99189828a8c`, portable 일반`dbbe7ef71a8d44a1b8df1a0524715733`. 모두 창을 닫은 뒤ended=false/Running을 관측하고 해당 marker만 자연 종료시켰다. 이전 첫 marker의 제한시간 종료는 수명 시험으로 계산하지 않는다.

화면 원본은 비공개 시험 산출물에 `accept-mg-v2-resume-complete`, `accept-md-v1-running`, `accept-pg-v1-running` 이름으로 보관한다. 제품 hash와 시험 도구 커밋efed1b3을 분리한다. 원시 로그·키·DB·시험 설치 데이터는 Git에서 제외한다.
