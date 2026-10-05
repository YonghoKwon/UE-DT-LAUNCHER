# 클라이언트 권한·유지보수·GUI 수용 후속

2026-10-05 / 기준dea2d18 / codex/client-usability-hardening.

## 관리형 경계

관리형 GUI는 화면 설정에서 선택만 투영한다. 운영 설정 loader·설치 Manifest 사전 읽기를 제거하고 Catalog/진단/실행을 Agent에 위임한다. 관리형 로그는 사용자별·화면설정별 경로이며 저장 공간 요약은 서비스 관리 안내다. GUI는 서비스 전용 설정으로 자동 fallback하지 않는다. 바로가기 프로필에는 관리형 모드·정확한 릴리스만 저장하고 기존 사용자 파일을 덮어쓰지 않는다. IPC v1의 선택적 ClientPresentation 정보는 릴리스·설치 식별자와 결속한다.

관련 회귀81개, 관리형/진단 추가 회귀47개 통과. 게시 stage-a4 CLI/console Agent의 HTTP 요청 서명·준비도·취소/재개·설치/실행/repair와 선택-only 최소 관리형 설정의 doctor/정확한 실행/감독 종료가 통과했다. 제품 소스 hash는b8d18516d6685347bfa5ebb6c7e4d0da055218b19120b830c2a97e98de84cdf5이며 publish 당시dea2d18/제품 변경 존재를 기록했다. 이 소스는 최종 GUI 후보가 아니다.

새 계정·Windows 서비스 설치·회사 ACL 인수는 수행하지 않았다. 기존714개를 포함한 최종 양 OS 전체 회귀와 새 고정 게시본의 네 GUI 조합 수용은 다음 단계다. 과거 후보의 성공을 새 후보의 실제 클릭으로 합산하지 않는다.

## 완료 후 재확인

복구/복원 후 상태 확인이 필요한 상태에서 실제 주/상태/재시도 버튼과F6는 조회만 수행한다. 네 조합의 실제 버튼 이벤트·headless 키 입력을 포함한 회귀와 적용/검사 실패 경계가 통과했다. 관리형 복원은 적용 성공을InstallationCommitted로 보존하고 조회 불가/응답 불명 시 재확인을 요구한다. 오래된 재개 힌트와 변경 버튼을 차단한다.

Windows 전체741개(714+27), 실패/skip0. stage-b 게시 CLI의 최소 관리형 설정·실제IPC 정상 백업 복원·선택 결속·전체 해시·완료 플래그가 통과했다. 첫 cli-b는 도구가Selection의 계산된releaseId 추가 필드까지 동등 비교해 실패했으며 필수5필드 비교로 수정한 새cli-b2는 통과했다. 첫 실패는 제품의 복원 실패로 기록하지 않는다. 이 후보는 최종 GUI 수용 전 단계이며 같은 계정 console Agent 결과는 서비스 계정 ACL 인수가 아니다.

## Portable 유지보수

임시 파일/백업 정리는 정확한 선택의 설치 잠금·runtime 재검사·미완료 journal 검사를 거친다. maintenance는 runtime 초기화 기록을 생성하지 않는다. staging 정리는 resume cache를 보존하고 backup은 기존 보관 개수를 따른다. 링크를 거부하며 UI 로그는 UI 스레드에서 집계한다. 설치 폴더는 존재하는 선택 경로만 열고 생성하지 않는다. 관련43개(신규9 포함)와 stage-c 양 에디션 게시 CLI의build-info 시작 확인을 통과했다. 실제 GUI 정리 조작은 최종 후보에서 별도로 수행한다.

## 시험 결속

Catalog 오류 제어는 실제 신규 작업ID를 한 번 고정하고 릴리스5필드·command·Manifest SHA-256·Completed를 모두 대조한다. 발화 기록과 프록시 세션을 보존하며 reset은 파일 오류와 미발생 Catalog 오류를 함께 해제한다. 계산된releaseId 추가 필드는 필수 선택 필드와 혼동하지 않는다. 합성 marker는 runtimeAttemptId와 결속하고 watchdog 종료 사유를 루프 종료 시 확정한다. 새 합성 실행 파일의 독립 수명 시험은 제한시간 경계를requested로 오인하지 않음을 확인했으며 GUI/감독 수용으로 합산하지 않는다.
