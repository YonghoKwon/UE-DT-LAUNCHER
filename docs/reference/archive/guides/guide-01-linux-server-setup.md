> 중복 입문 가이드 보관: 신규 운영 절차로 사용하지 마세요. 현재 [전체 명령 순서](../../feature-workflow.md)와 [서버 운영](../../distribution-workflow.md)에 통합했습니다. 아래는 당시 기록입니다.

# Linux 배포 서버 설정

> 참고 가이드 / 문서 점검 2026-09-22 / 구현 기준 2cd28c8. 현재 기능은 [README](../../../../README.md), 미완료 항목은 [보완 목록](../../../../IMPROVEMENTS.md)을 따릅니다.
현재 운영 기준은 [ZIP·외부 JSON 통합 배포](../../distribution-workflow.md)입니다.
기존 공개 /projects 및 /catalogs 정적 파일 규칙, Basic Auth, project-ip-allowlist 생성 방식은 새 배포 서비스의 인증 정책을 대신하지 않습니다.

- nginx는 HTTPS 종단과 실제 클라이언트 IP 전달을 담당합니다.
- DistributionServer는 서명된 목록과 모든 파일 요청에 PC 토큰 + IP/CIDR + 프로젝트/트랙 권한을 검사합니다.
- incoming, processing, archive와 DB는 정적 웹 루트에 놓지 않습니다.
- 배포 서버 프로세스는 localhost에만 바인딩하고 외부에는 nginx 443 포트만 제공합니다.
- 프록시 뒤에 추가 프록시가 있는 경우 별도 신뢰 IP 설계가 필요합니다. 기본은 사내 PC가 nginx에 직접 연결하는 구성입니다.

검증된 정책만 운영 적용하고, 적용 전에 nginx -t 및 허용/거부 PC의 직접 URL 다운로드 검사를 수행하세요.
