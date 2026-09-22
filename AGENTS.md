# 작업 지침

점검: 2026-09-22 / 구현 기준 2cd28c8. 저장소 전체에 적용합니다.

## 문서 관리 계약

- 루트 정본은 README.md(현재), AGENTS.md(규칙), IMPROVEMENTS.md(보완), PROJECT_GOALS.md(목표·미정) 4개입니다.
- 나머지 상세 가이드·검증 기록은 docs/reference/, 과거 자료는 archive/에 두고 색인에 연결합니다.
- 기능·설정·보안·CLI·UI 변경 시 관련 정본과 상세 가이드를 함께 갱신합니다. 같은 설정 전문을 중복 관리하지 않습니다.
- 구현됨/과거 테스트됨/이번 검증됨/회사 미검증을 구분하고 날짜·플랫폼·입력을 기록합니다.
- 목표는 사용자 확정 전까지 미정입니다. 제안을 확정 기능이나 완료 기능으로 바꾸지 않습니다.
- 문서를 이동하면 상대 링크와 스크립트 내 참조도 확인합니다.

## 코드 지도

| 위치 | 책임 |
|---|---|
| src/UeDtLauncher/Program.cs | GUI/CLI 명령 |
| src/UeDtLauncher/Gui/ | programmatic Avalonia View·ViewModel·시각 토큰 |
| src/UeDtLauncher.Core/ | 공용 assembly, Distribution 메타데이터·경로·이미지 |
| src/UeDtLauncher/*.cs | 엔진·보안·transaction·IPC 소스 일부. Core csproj가 링크 컴파일 |
| src/UeDtLauncher.Agent/ | Windows Service/Linux systemd Agent |
| src/UeDtLauncher.DistributionServer/ | SQLite 접수·승인·서명 게시·인증 API |
| src/UeDtLauncher.Tests/, tools/test-distribution-e2e.sh | 자동화·실제 프로세스 E2E |
| installer/windows/, packaging/linux/, .github/workflows/ | 설치본·nginx·서비스·CI |

## 보존할 경계

- ZIP + 외부 release.json을 비공개 snapshot으로 검사하고 관리자 승인 후 게시합니다. 업로드 프로그램을 서버에서 실행하지 않습니다.
- .uploading/한 파일만 도착한 상태를 게시하지 않습니다. 동일 릴리스 식별자 덮어쓰기를 허용하지 않습니다.
- 실제 IP/CIDR + PC별 토큰을 목록·Manifest·이미지·파일·Range 모두에 적용합니다. 공개 정적 경로 우회를 만들지 않습니다.
- HTTPS·서명·해시·경로 안전 검사를 완화하지 않습니다. 개인키·토큰·Authorization을 로그나 Git에 넣지 않습니다.
- GUI 프로필은 화면 정책입니다. Agent의 보호된 운영 설정·credential·서버 권한과 분리합니다. 정확한 선택을 다른 버전으로 몰래 대체하지 않습니다.
- 설치·상태·잠금·PID는 프로젝트/환경/채널/버전/OS별 격리입니다. 기존 설치·사용자 데이터는 승인 없이 삭제하지 않습니다.
- 일반 GUI는 자동 점검만 합니다. 설치는 사용자 동작, rollback은 확인 후 실행합니다. 무인 서비스와 구분합니다.
- IPC v1 단일 응답과 streaming 클라이언트 호환성을 보존합니다.

## UI 규칙

- programmatic View와 LauncherVisualTokens·ViewModel을 사용합니다.
- 일반 화면은 한 버튼·친화적 오류, Agent 대신 업데이트 서비스로 표기합니다. 기술 예외·내부 경로·비밀정보를 기본 화면에 표시하지 않습니다.
- 개발자 명령을 보존하되 서버 권한을 확대하지 않습니다. 배포 서버 모드 GUI는 현재 OS용 릴리스를 선택합니다.
- 이미지 누락·손상·과대 파일은 브랜드 fallback으로 처리합니다. 키보드·focus·스크린리더·DPI를 확인합니다.
- GUI 설정 탐색은 explicit --config → 실행 파일 옆 → 관리 설정입니다. 관리형 운영 값은 Agent 설정을 사용합니다.
- single-file 네이티브 라이브러리 포함 옵션과 XAML의 &amp; escaping을 유지합니다.

## 작업·검증·커밋

1. git status와 지침을 확인하고 사용자 변경을 보존합니다. 브랜치·remote를 임의로 교체하지 않습니다.
2. 의미 단위 구현 후 관련 테스트를 실행합니다. --no-restore 전 restore가 필요합니다.
3. 기능 변경은 publish된 GUI/Agent/CLI/서버로 실행 검증합니다. Unreal Editor 프로젝트가 아닙니다.
4. Windows/WSL 결과를 회사 RHEL/실제 UE 결과로 보고하지 않습니다.
5. 관련 파일만 git add로 선별 stage하고 staged diff 확인 후 부분별 커밋합니다. git add -A는 사용하지 않습니다.
6. 최종 git diff --check, 링크, 커밋 범위, worktree를 확인합니다. push/PR은 요청 범위에 따릅니다.

기본 빌드·테스트 명령은 README를 따릅니다. 문서 전용 수정은 소스·명령·링크 대조와 diff 검사로 검증 가능하며 GUI 실행·전체 테스트를 수행한 것처럼 보고하지 않습니다.

publish/bin/obj, 테스트 logs·DB·인증서·토큰, 패키지·설치 데이터는 커밋하지 않습니다. 새 테스트 없이 과거 검증 날짜·결과를 덮어쓰지 않습니다.
