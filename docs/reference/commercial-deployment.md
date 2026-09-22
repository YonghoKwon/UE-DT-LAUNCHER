# 관리형 배포·패키징 준비

> 참고 가이드 / 문서 점검 2026-09-22 / 구현 기준 2cd28c8. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

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

출력은 `artifacts/windows-installer`이며 기본은 `UNSIGNED-DEV`입니다. `-OfficialBuild`도 표시·빌드 속성을 바꾸는 옵션이며 자체적으로 Authenticode 서명을 하지 않습니다.

Linux RPM:

```bash
./scripts/build-rpm.sh 1.0.0
```

출력은 `artifacts/linux-rpm/rpmbuild/RPMS/x86_64`입니다. `rpmbuild`가 필요하며 spec은 single-file 번들 strip을 막고 관리 설정을 `%config(noreplace)`로 보존합니다. 빌드 스크립트에 포함된 예시 설정은 실제 회사 DistributionServer 설정으로 교체·검증해야 합니다.

`.github/workflows/release.yml`은 `launcher-v*` tag에서 Windows PFX·RPM GPG 비밀값을 검사합니다. 코드에 gate가 있다는 사실과 실제 서명된 설치 결과 검증은 다릅니다. 배포 전 MSI 내부에 설치되는 EXE의 서명까지 검사하세요.

현재 순서는 MSI 생성 후 payload EXE 서명이므로 embedded CAB 내부 EXE에는 그 서명이 반영되지 않습니다. **EXE 서명·검증 → MSI 생성 → MSI 서명·설치 후 검증** 순서로 수정하기 전 운영 배포를 승인하지 마세요. 이는 코드에서 확인한 미해결 항목 OPS-08입니다.

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
UeDtLauncher.Agent migrate --config C:\ue-dt\launcher.config.json --apply
UeDtLauncher import-install --config C:\ue-dt\launcher.config.json --destination-root C:\ue-dt\apps
UeDtLauncher import-install --config C:\ue-dt\launcher.config.json --destination-root C:\ue-dt\apps --apply
```

`migrate`는 기존 portable 설정·상태를 관리형 위치로 이전하는 경로이며 기본은 계획 출력입니다. `import-install`은 버전 설치 루트로 기존 설치를 검사·복사하는 별도 경로이고 원본을 삭제하지 않습니다. 적용 전 계획과 대상 경로를 확인하고 복사 후 새 설정으로 check/update를 수행합니다. UE 저장 데이터는 실제 저장 위치에 맞춰 별도 백업·이전합니다.

## 운영 승인 전 확인

- 회사 IP/방화벽/CA/접근 정책과 실제 RHEL 적용
- 서명된 MSI/RPM 설치·업그레이드·제거 및 설치 payload 서명
- Agent 계정별 credential·IPC·파일 권한
- 실제 UE 의존성·대용량 패키지·재시작·복구·저장 데이터
- 서버 DB/서명키/릴리스 백업·복원과 sequence replay 방어

레거시 `publish-release`/정적 catalog는 기존 설치 호환 경로입니다. 새 통합 서버에 공개 /projects·/catalogs location을 섞지 않습니다. 우선순위·미완료 개선은 [IMPROVEMENTS](../../IMPROVEMENTS.md)에서 관리합니다.
