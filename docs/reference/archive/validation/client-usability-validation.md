# 클라이언트 권한·유지보수·GUI 수용 후속

2026-10-05 / 기준dea2d18 / codex/client-usability-hardening.

## 관리형 경계

관리형 GUI는 화면 설정에서 선택만 투영한다. 운영 설정 loader·설치 Manifest 사전 읽기를 제거하고 Catalog/진단/실행을 Agent에 위임한다. 관리형 로그는 사용자별·화면설정별 경로이며 저장 공간 요약은 서비스 관리 안내다. GUI는 서비스 전용 설정으로 자동 fallback하지 않는다. 바로가기 프로필에는 관리형 모드·정확한 릴리스만 저장하고 기존 사용자 파일을 덮어쓰지 않는다. IPC v1의 선택적 ClientPresentation 정보는 릴리스·설치 식별자와 결속한다.

관련 회귀81개, 관리형/진단 추가 회귀47개 통과. 게시 stage-a4 CLI/console Agent의 HTTP 요청 서명·준비도·취소/재개·설치/실행/repair와 선택-only 최소 관리형 설정의 doctor/정확한 실행/감독 종료가 통과했다. 제품 소스 hash는b8d18516d6685347bfa5ebb6c7e4d0da055218b19120b830c2a97e98de84cdf5이며 publish 당시dea2d18/제품 변경 존재를 기록했다. 이 소스는 최종 GUI 후보가 아니다.

새 계정·Windows 서비스 설치·회사 ACL 인수는 수행하지 않았다. 기존714개를 포함한 최종 양 OS 전체 회귀와 새 고정 게시본의 네 GUI 조합 수용은 다음 단계다. 과거 후보의 성공을 새 후보의 실제 클릭으로 합산하지 않는다.
