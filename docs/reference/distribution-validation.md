# 통합 배포 검증 기록 — 2026-09-12

> 보존된 실행 기록입니다. 2026-09-22 문서 재분류는 아래 테스트를 다시 수행했다는 의미가 아닙니다.

## 자동화
- Windows 전체 테스트 210/210 통과, Release 솔루션 빌드 경고 0·오류 0.
- WSL Ubuntu .NET 8 전체 테스트 210/210 통과, Release 솔루션 빌드 경고 0·오류 0.
- SQLitePCLRaw 번들 2.1.13 적용 후 배포 서버의 NuGet 취약 패키지 0건. 테스트에서 실제 SQLite 버전이 3.50.2 이상인지 확인.
- 두 파일 도착 순서, 중복 접수, 재시작, 원본 변조 후 private snapshot 보존, unsafe ZIP, 크기/해시 불일치, 승인 중 서명 실패 후 재개, 버전 경로 분리, 기존 설치 dry-run/복사 검증 포함.

## 실제 프로세스
- publish된 Windows 서버 CLI로 ZIP+외부 JSON 접수 → pending → approve → 디렉터리 자동 생성 → 서명 Manifest 생성 확인.
- publish된 Windows Agent로 정확한 버전 선택 설치 확인.
- Windows 개발자 GUI에서 서버 허용 프로젝트 목록 및 설치 버전 확인.
- WSL Ubuntu의 실제 nginx(auth_request)+TLS+배포 API와 publish Linux 클라이언트로 tools/test-distribution-e2e.sh 통과.
- 정상 목록 200, Range 206, 미허용 트랙 및 다른 IP/위조 헤더 403, 폐기 토큰 401 확인.
- 두 Linux 버전 동시 설치·실행권한·실행·손상 파일 repair 확인.
- 동일 Linux HTTPS 서버에 Windows ZIP도 외부 JSON과 함께 접수·승인한 뒤, publish Windows GUI/Agent에서 설치 후 실행 버튼을 눌러 2.0.0 다운로드·설치·실행 및 100% 표시 확인.

## 재현
tools/test-distribution-e2e.sh는 세 실행 파일(배포 서버, Linux 런처, nginx)을 인자로 받습니다. 로그·임시 인증서·서명키·작업 DB·실행 로그는 /tmp/uedt-distribution-e2e.* 아래에 보관합니다. 최신 재현은 /tmp/uedt-distribution-e2e.5Hedlh에서 수행했습니다.
Windows HTTPS 실행 화면은 커밋하지 않는 publish/distribution-final/evidence/windows-https-installed.jpg에 저장했습니다.

## 적용 범위
실제 회사 RHEL 서버 설치, 회사 IP·방화벽·CA 정책 적용 및 실제 Unreal 패키지 검증은 수행하지 않았습니다. 이 기록의 Windows 실행 파일과 Linux 스크립트는 테스트용이며 실제 UE 프로그램 성능이나 필요 라이브러리를 검증하지 않습니다.
회사 백엔드 연동은 IAccessPolicyProvider 확장 지점까지 제공하며 실제 원격 API 구현은 포함하지 않습니다.
