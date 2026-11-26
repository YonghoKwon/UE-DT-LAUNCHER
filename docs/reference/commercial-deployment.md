# 관리형 배포·패키징 준비

> 현재 가이드 / 문서 점검 2026-10-03 / codex/operations-hardening-closure. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

2026-11-27 PROD-10 [로컬] 문서 대조. 현재 표준은 `/dt`의 파일 저장소, 사내 HTTP 요청 서명, SSH `upload`, 수동 승인, `stable`의 명시적 추천 승격입니다. 회사 실환경은 미검증입니다. [서버 배치](distribution-workflow.md) · [PC 등록](intranet-auth.md)

이 문서는 패키징 소스와 현장 점검 절차입니다. MSI machine-wide 설치·실제 RHEL 8 RPM 배포·실제 UE 운영 검증 완료를 뜻하지 않습니다. 초기 통합 결과는 [배포 이력](archive/validation/distribution-validation.md), 최신 합성 앱 실행·복구·rollback 결과는 [관리형 GUI 이력](archive/validation/managed-gui-safety-validation.md)을 따릅니다. 문서 점검이 새 설치 수명주기 시험을 뜻하지 않습니다.

## 책임과 계정

GUI는 사용자 세션에서 앱을 실행하고 Agent는 보호 설정으로 설치·복구·백업 복원을 수행합니다. IPC는 Windows named pipe 또는 Linux Unix socket입니다. DistributionServer에서는 서버 grant가 권한을 결정합니다. 레거시 정적 catalog Agent는 관리 설정에 선언된 프로젝트로 제한됩니다.

Windows MSI의 Agent 계정은 `NT AUTHORITY\LocalService`, Linux unit은 `uedt`입니다. 설정·credential·공개키의 소유권/ACL, 앱 실행 계정의 읽기·실행 권한을 별도로 점검합니다.

## 빌드 진입점

Windows 개발 패키지:

```powershell
.\scripts\build-windows-installer.ps1 -Version 1.0.0
.\scripts\build-windows-installer.ps1 -Version 1.0.0 -Edition Developer
```

기본은 `UNSIGNED-DEV`입니다. 출력은 `artifacts/windows-installer/runs/<실행ID>/release`이며 성공한 `package-result.json`의 정확한 파일만 사용합니다. `-OfficialBuild`는 publish 전 인증서/개인키/EKU/유효기간/도구를 확인하고 EXE 선서명·검증, MSI 생성·서명·검증, CAB payload 비교를 강제합니다. 개발 빌드는 인증서 환경변수만으로 공식 빌드로 승격되지 않습니다.

General MSI는 일반 EXE/Agent, Developer MSI는 일반/개발자 EXE/Agent를 포함합니다. 같은 제품 계열의 동일 버전 다른 설치본을 중복 설치하려 하면 전환 안내로 거부하며, 관리자 정비 후 재설치 또는 새 버전 업그레이드로 전환합니다. 내용/설정/state/사용자 데이터 보존은 실제 VM 인수에서 확인해야 합니다. 에디션은 설정에서 선택하지 않습니다.

Linux RPM:

```bash
./scripts/build-rpm.sh 1.0.0
```

출력은 `artifacts/linux-rpm/run.<고유ID>/artifacts/`이며 스크립트의 `RPM_PATH`와 `package-result.json`으로 이번 실행의 정확한 RPM을 선택합니다. 예전 산출물을 wildcard로 다시 서명하지 않습니다. `rpmbuild`, `rpm2cpio`, Python3.10+가 필요합니다. spec은 번들 strip·중복 build-id 링크를 막고 설정을 `%config(noreplace)`로 보존합니다. data-only CAB/RPM 추출과 GUI·Agent hash/권한 확인까지 성공해야 공개합니다. RPM 원본 검증 결과는 `UNSIGNED-DEV`이며 실제 서명 후 CI가 별도로 검증해야 합니다.

동시에 `ue-dt-launcher-developer` 추가 RPM을 생성하며 정확히 같은 version-release의 `ue-dt-launcher`를 요구합니다. 기본 RPM은 일반/runtime-host/Agent/config/service, 추가 RPM은 개발자 EXE만 소유합니다. 개발자 배포에는 두 RPM을 사용하며 서비스가 두 번 설치되지 않습니다.

`UE_DT_SKIP_DOTNET_PUBLISH=1` 재사용 경로는 거부합니다. 새 버전의 GUI/Agent를 항상 publish하며 예시 설정은 회사 주소/계정/권한으로 provision해야 합니다. 현재 개발 PC/WSL에서 실제 설치나 scriptlet·계정 생성을 실행한 것은 아닙니다.

`.github/workflows/release.yml`은 같은 tag source의 Windows/Linux functional gate와 Windows PFX·RPM GPG 조건을 모두 요구합니다. RPM은 정확한 primary fingerprint·격리된 GPG/RPM key DB로 검사하고 재추출한 payload를 대조합니다. SBOM·checksum은 검증된 이번 package 폴더만 사용합니다. CI 코드 연결은 실제 원격 성공이나 인증서 인수가 아닙니다.

## 회사 인수 계획 생성 — 실행하지 않는 도구

```powershell
python tools/prepare-company-acceptance.py --platform windows --package <정확한-MSI> --next-package <다음-MSI> --output <새-비공개-계획폴더>
```

Linux는 `--platform rhel8 --package <정확한-RPM>`을 사용합니다. 도구는 package SHA-256과 **미실행** 체크리스트만 생성하고 install/서비스/계정/재부팅 명령은 실행하지 않습니다. 다음 package가 없으면 upgrade는 자료 대기입니다. 승인된 격리 VM snapshot과 별도 백업을 준비한 뒤, 담당자가 각 적용 직전에 확인하고 설치→연결/권한→upgrade→repair→재부팅→uninstall·설정/콘텐츠/데이터 보존을 점검하세요. `UNSIGNED-DEV` 계획 생성은 회사 서명 합격이 아닙니다.

현재 순서는 **EXE 서명·검증 → MSI 생성·서명·검증 → 비설치 CAB 추출·EXE hash/signer 확인 → artifact 공개**입니다. 실행별 WiX intermediate를 분리하며 CI는 검증된 정확한 release 폴더만 사용합니다. 실제 개발 MSI 추출은 통과했지만 회사 인증서·설치된 EXE 검증은 미완료이므로 OPS-08은 50%입니다. [상세 기록](archive/validation/deployment-safety-validation.md)

## 현장 적용 순서

1. 격리된 관리자 테스트 PC/실제 RHEL에서 설치·업그레이드·제거를 확인합니다.
2. 보호 설정에 distributionServerUrl, 설치·상태·로그 루트, 공개키, credential 이름을 지정합니다.
3. 사내 HTTP PC는 `enroll prepare` → `client-enroll` → `enroll finish`로 등록합니다. 기존 HTTPS/Bearer 설치를 유지하는 경우에만 토큰을 안전하게 전달하고 Agent 계정의 credential 저장소에 등록합니다.
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
- 파일 권위 저장소·별도 서명키·릴리스 백업/복원과 sequence replay 방어

레거시 `publish-release`/정적 catalog는 기존 설치 호환 경로입니다. 새 통합 서버에 공개 /projects·/catalogs location을 섞지 않습니다. 우선순위·미완료 개선은 [IMPROVEMENTS](../../IMPROVEMENTS.md)에서 관리합니다.

## 장애 대응 런북

사용자는 오류 화면에서 **지원 정보 복사**로 시각·지원 ID·PC 이름·런처 버전을 전달합니다. PC 관리자는 `doctor --online`과 업데이트 서비스 상태를 확인합니다. 서버 관리자는 같은 시각의 서비스·nginx 로그와 업로드 ID를 확인합니다. 전달 자료에는 인증 헤더·키·토큰을 넣지 않습니다. 회사값은 승인된 설정을 사용합니다. **TODO(09/09L/25 결과 반영)**

| 증상 | 담당 / 실행 위치 | 입력·조치 | 복귀 확인 |
|---|---|---|---|
| 업로드가 전송 완료/대기에 머묾 | 개발자 / PowerShell, 서버 관리자 / Bash | `upload status --upload-id ID --wait` → 서버 `list`/`inspect`로 두 최종 파일, `.uploading`, 서비스 계정의 읽기 권한과 `serve` 확인. 불완전 전송은 원인 해결 후 새 ID로 재전송 | 정상 파일 쌍 검사를 거쳐 `pending` |
| 검사 실패 또는 거절 | 개발자 / PowerShell, 운영자 / 서버 콘솔 | `upload status --upload-id ID`, 콘솔 2 상세. ZIP/외부 메타데이터 오류를 수정해 새 ID로 업로드 | 새 ID가 `pending`, 운영자 수동 승인 가능 |
| 다운로드 실패 다수 | 서버·망 담당 / Bash | nginx 오류와 `df`, `usage --config /dt/config/server.json`, 서비스 상태 확인. 원인을 고치고 `nginx -t` 후 적용. 서비스 재시작은 승인된 창에서 수행 | 미인증 Catalog 401, 등록 PC의 인증된 목록·파일 조회. Range 장애는 206과 최종 해시까지 확인 |
| 새 권장 버전에 결함 | 개발자 / Windows, 운영자 / 서버 콘솔 | 지원 ID·진단 ZIP으로 영향 확인. 권한 있는 개발자가 이전 공개판을 exact 검증하고 운영자가 콘솔 6에서 현재 추천/revision 확인 후 이전 판을 명시 승격 | PC가 다음 조회에서 허용 추천을 확인. 실행 중인 앱은 사용자가 관리 |
| 한 PC만 실패 | PC 관리자 / PowerShell, 서버 관리자 / Bash | `doctor --online`. 401이면 PC 키·폐기, 403이면 관측 IP/CIDR·grant 확인. 정상 Catalog 0건이면 grants·OS별 공개판, 추천 대기면 명시 승격 이력 확인 | 해당 PC의 허용 배포와 추천 확인. [등록과 IP 변경](intranet-auth.md#d-문제-해결키-교체) |
| 저장소 손상, 최신 원본 생존 | 서버 관리자 / Bash | 작업 정지 → `backup verify` → 빈 전용 root로 `restore stage` → 최신 원본과 정책·순번·승격·파일 대조 후 `restore activate --confirm`. staging 펜스가 남으면 공개 중단 유지 | 새 root·반환한 정책 경로와 기존 origin/signer로 연결. 인증 Catalog와 파일 확인. [복원 명령](distribution-workflow.md#1-서버-준비) |
| 원본 저장소까지 완전 유실 | 서버 관리자·개발자 / Bash·PowerShell | 원본 패키지 보관 위치 확인 → 깨끗한 `/dt/store`에 서버 재구축. 운영 책임자가 서명·클라이언트 신뢰 절차를 승인한 뒤 필요한 버전별 `upload` → 수동 승인 → 명시 승격 | 기존 보안·순번 보호를 유지한 운영 절차에서 인증 목록과 실행 확인. 주간 백업 목표 RPO 1주, 회사 RTO는 T4 실측 |
| 전원 차단·최장 경로·디스크 소진 | PC 관리자 / Windows 시험 PC | 런처와 앱 상태, 여유 공간, 설치 경로 확인. 이전 앱을 정상 종료하고 [실행 안전성](runtime-safety.md)의 읽기 전용 진단부터 진행 | 이전 설치와 사용자 데이터 해시, 차단/복구 결과 기록. **TODO(09/09L/25 결과 반영)** |

## 실환경 이관 체크리스트

PROD-10 초안의 T1~T19는 모두 회사 **미실행**입니다. 필수 항목이 미실행인 동안 전사 배포를 보류합니다. 계획의 5~10대·2주는 파일럿 제안값이며 승인 범위와 동시 PC 수는 이관에서 정합니다.

| ID | 확인 항목 | 담당 | 필수 증거 | 구분 | 상태 |
|---|---|---|---|---|---|
| T1 | 실제 서버 설치와 재부팅 | 서버 관리자 | RHEL 8.4(Ootpa)·16 GB·약 250 GB, `install-server.sh` 적용, systemd active와 재부팅 후 미인증 401 | 필수 | 미실행 |
| T2 | SELinux·방화벽 | 서버 관리자 | enforcing 상태의 프록시·incoming 허용과 관련 거부 로그 0, 원격에서 18500 차단 | 필수 | 미실행 |
| T3 | nginx 대용량 | 서버 관리자 | 회사 nginx의 20 GB 다운로드 Range 206와 최종 해시 | 필수 | 미실행 |
| T4 | 주간 백업·복원 | 서버 관리자 | 오프라인 백업, 생존 원본으로 복원 훈련 1회, 전손 시 재게시 리허설 및 RPO/RTO 실측 | 필수 | 미실행 |
| T5 | 사내망 성능 | 서버·망 담당 | 확정 동시 PC 수, 다운로드 시도·실패 분모와 p95 | 필수 | 미실행 |
| T6 | 회사 프록시 | PC 관리자 | LocalService가 회사 프록시 경유로 인증된 Catalog 조회 | 필수 | 미실행 |
| T7 | 회사 업로드 계정 | 개발자·서버 관리자 | 실제 SSH 계정·known-host·incoming 권한으로 `upload` 후 같은 ID의 `pending` | 필수 | 미실행 |
| T8 | PC 등록·설치 | PC 관리자 | 파일럿 PC마다 업데이트 서비스의 실제 인증 Catalog와 한 버전의 설치 결과 | 필수 | 미실행 |
| T9 | DHCP·키 폐기 | 서버 관리자 | DHCP 변경 뒤 403과 정책 갱신 후 복구, 키 폐기 401과 새 키 등록 이후 복구 | 필수 | 미실행 |
| T10 | 설치본 수명주기 | PC 관리자 | 회사 표준 이미지에서 이전 MSI → 새 MSI, 업그레이드 실패 후 복구, 제거·재부팅 후 데이터 보존 | 필수 | 미실행 |
| T11 | Agent/GUI 불일치 | PC 관리자 | 서로 다른 버전의 갱신 안내와 지원되지 않는 변경 요청 차단 | 필수 | 미실행 |
| T12 | EDR·백신 | 보안 담당·PC 관리자 | 정책 적용 PC에서 실행. 차단 시 회사 서명본으로 실제 정상 실행 | 필수 | 미실행 |
| T13 | 현장 장애 | PC 관리자 | 실제 전원 차단·재부팅, 회사 최장 경로, 디스크 소진 이후 이전 설치·사용자 데이터 해시 | 필수 | 미실행 |
| T14 | 회사 UE | 개발자 | 실행 파일이 달라진 회사 UE 빌드 업데이트·실행 및 데이터 보존 | 필수 | 미실행 |
| T15 | 일괄 배포 | IT | 채택한 GPO/SCCM 적용의 성공·실패 PC 목록 | 조건부 | 미실행 |
| T16 | 화면·접근성 | 사용자 대표 | 현업 해상도·배율의 잘림 0, 필요 시 내레이터 관측 | 필수 | 미실행 |
| T17 | 현업 파일럿 | 사용자 대표 | 승인 범위와 기간, 첫 시도/최종 성공 각각의 분자·분모, 문의 건수 | 필수 | 미실행 |
| T18 | 운영 인계 | 운영 책임자 | 담당자 배정, 런북 리허설, SEC-04 잔여 위험 수용서 | 필수 | 미실행 |
| T19 | `/dt/store` 파일시스템 | 서버 관리자 | `findmnt -no FSTYPE,SOURCE --target /dt/store`의 로컬 ext4 또는 XFS 결과 | 필수 | 미실행 |
