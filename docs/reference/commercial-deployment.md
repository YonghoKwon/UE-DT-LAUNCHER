# 관리형 배포·패키징 준비

> 참고 가이드 / 문서 점검 2026-09-28 / 구현 기준 codex/launcher-deployment-safety. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

현재 기본 경로는 DistributionServer의 ZIP + 외부 `release.json` 접수·승인 → 서버 권한 확인 → 서명된 메타데이터/파일 다운로드 → 버전별 설치입니다. 서버·토큰 준비는 [통합 운영](distribution-workflow.md)을 따릅니다.

이 문서는 패키징 소스와 현장 점검 절차입니다. MSI machine-wide 설치·실제 RHEL 8 RPM 배포·실제 UE 운영 검증 완료를 뜻하지 않습니다. Windows GUI/Agent·WSL Ubuntu Linux CLI 테스트 결과는 [2026-09-12 기록](distribution-validation.md)에 한정됩니다.

## 책임과 계정

GUI는 사용자 세션에서 앱을 실행하고 Agent는 보호 설정으로 설치·복구·백업 복원을 수행합니다. IPC는 Windows named pipe 또는 Linux Unix socket입니다. DistributionServer에서는 서버 grant가 권한을 결정합니다. 레거시 정적 catalog Agent는 관리 설정에 선언된 프로젝트로 제한됩니다.

Windows MSI의 Agent 계정은 `NT AUTHORITY\LocalService`, Linux unit은 `uedt`입니다. 설정·credential·공개키의 소유권/ACL, 앱 실행 계정의 읽기·실행 권한을 별도로 점검합니다.

## 빌드 진입점

Windows 개발 패키지:

```powershell
.\scripts\build-windows-installer.ps1 -Version 1.0.0
```

기본은 `UNSIGNED-DEV`입니다. 출력은 `artifacts/windows-installer/runs/<실행ID>/release`이며 성공한 `package-result.json`의 정확한 파일만 사용합니다. `-OfficialBuild`는 publish 전 인증서/개인키/EKU/유효기간/도구를 확인하고 EXE 선서명·검증, MSI 생성·서명·검증, CAB payload 비교를 강제합니다. 개발 빌드는 인증서 환경변수만으로 공식 빌드로 승격되지 않습니다.

Linux RPM:

```bash
./scripts/build-rpm.sh 1.0.0
```

출력은 `artifacts/linux-rpm/rpmbuild/RPMS/x86_64`입니다. `rpmbuild`가 필요하며 spec은 single-file 번들 strip을 막고 관리 설정을 `%config(noreplace)`로 보존합니다. 빌드 스크립트에 포함된 예시 설정은 실제 회사 DistributionServer 설정으로 교체·검증해야 합니다.

`.github/workflows/release.yml`은 `launcher-v*` tag에서 Windows PFX·RPM GPG 비밀값을 검사합니다. 코드에 gate가 있다는 사실과 실제 서명된 설치 결과 검증은 다릅니다. 배포 전 MSI 내부에 설치되는 EXE의 서명까지 검사하세요.

현재 순서는 **EXE 서명·검증 → MSI 생성·서명·검증 → 비설치 CAB 추출·EXE hash/signer 확인 → artifact 공개**입니다. 실행별 WiX intermediate를 분리하며 CI는 검증된 정확한 release 폴더만 사용합니다. 실제 개발 MSI 추출은 통과했지만 회사 인증서·설치된 EXE 검증은 미완료이므로 OPS-08은 50%입니다. [상세 기록](deployment-safety-validation.md)

## 현장 적용 순서

1. 격리된 관리자 테스트 PC/실제 RHEL에서 설치·업그레이드·제거를 확인합니다.
2. 보호 설정에 distributionServerUrl, 설치·상태·로그 루트, 공개키, credential 이름을 지정합니다.
3. PC 토큰을 안전하게 전달하고 Agent 계정이 읽을 수 있는 credential 저장소에 등록합니다.
4. Linux credential은 0600이므로 소유자도 확인합니다. Windows는 DPAPI LocalMachine만으로 계정 격리가 보장되지 않으므로 ACL도 확인합니다.
5. 서비스·서버 연결, 허용/거부 프로젝트·버전, 설치·repair·실제 UE 실행을 확인합니다.

```text
UeDtLauncher credential set --name company-distribution
UeDtLauncher doctor --config launcher.config.json --online
UeDtLauncher agent status
UeDtLauncher agent check --project demo
UeDtLauncher agent update --project demo
UeDtLauncher diagnostics export --config launcher.config.json
```

설정은 [클라이언트 레퍼런스](guide-03-launcher-usage.md), GUI는 [UI 가이드](launcher-ui-customization.md)를 사용합니다. Agent 설치만으로 주기 점검이 시작되지 않으며 [서비스 모드](service-mode.md)의 제한을 확인합니다.

## 기존 설치 이전

두 명령은 목적이 다릅니다.

```text
UeDtLauncher.Agent migrate --config C:\ue-dt\launcher.config.json
UeDtLauncher.Agent migrate --config C:\ue-dt\launcher.config.json --dry-run
UeDtLauncher import-install --config C:\ue-dt\launcher.config.json --destination-root C:\ue-dt\apps
UeDtLauncher import-install --config C:\ue-dt\launcher.config.json --destination-root C:\ue-dt\apps --apply
```

`migrate`는 현재 계획 출력만 지원합니다. 기존 InstallDir를 공유하는 apply는 소유권 충돌 위험으로 차단되며 config/state를 생성하지 않습니다. 기존 공유 구성이 있다면 자동 이전 대신 별도 정비 계획이 필요합니다. `import-install`은 정지 확인·잠금 후 버전 설치 루트에 검사·복사하는 별도 경로이며 원본을 삭제하지 않습니다. UE 저장 데이터 이전은 USER-01의 별도 정책 대상입니다. [수동 점검](runtime-safety.md)

## 운영 승인 전 확인

- 회사 IP/방화벽/CA/접근 정책과 실제 RHEL 적용
- 서명된 MSI/RPM 설치·업그레이드·제거 및 설치 payload 서명
- Agent 계정별 credential·IPC·파일 권한
- 실제 UE 의존성·대용량 패키지·재시작·복구·저장 데이터
- 서버 DB/서명키/릴리스 백업·복원과 sequence replay 방어

레거시 `publish-release`/정적 catalog는 기존 설치 호환 경로입니다. 새 통합 서버에 공개 /projects·/catalogs location을 섞지 않습니다. 우선순위·미완료 개선은 [IMPROVEMENTS](../../IMPROVEMENTS.md)에서 관리합니다.
