# UE-DT Launcher 사내 상용 배포

## 구성

```text
Avalonia GUI / CLI
        ↓ named pipe 또는 Unix socket
Managed Agent (Windows Service / systemd)
        ↓ HTTPS + protected Bearer token
서명된 catalog v2 → manifest v2 → immutable release
        ↓
프로젝트별 transaction 설치·repair·rollback
```

Windows Agent는 `LocalService`, Linux Agent는 `uedt` 계정으로 실행합니다. GUI가 임의 URL·경로·프로세스를 Agent에 전달할 수 없으며 machine config에 선언된 프로젝트만 처리합니다.

## Windows 개발 패키지

```powershell
./scripts/build-windows-installer.ps1 -Version 1.0.0
```

결과 MSI와 payload에는 `UNSIGNED-DEV`가 표시됩니다. 관리자 권한으로 설치해야 하며 운영 배포에 사용하지 않습니다. 정식 tag workflow는 Authenticode PFX와 RPM GPG key가 없으면 즉시 실패합니다.

## RHEL 8 RPM

```bash
chmod +x scripts/build-rpm.sh
./scripts/build-rpm.sh 1.0.0
sudo dnf install artifacts/linux-rpm/rpmbuild/RPMS/x86_64/ue-dt-launcher-1.0.0-1.x86_64.rpm
sudo systemctl enable --now ue-dt-launcher-agent
```

RPM은 .NET single-file bundle을 strip하지 않으며 `/etc/ue-dt-launcher/launcher.config.json`을 `%config(noreplace)`로 보존합니다.

## 인증정보 준비

설정에는 token 대신 이름만 기록합니다.

```powershell
UeDtLauncher credential set --name ue-dt-prod
```

Windows는 DPAPI LocalMachine, Linux는 서비스 계정만 읽을 수 있는 0600 credential 파일을 사용합니다. 운영 config v2는 HTTPS, metadata 서명, 허용 host, credential 이름을 필수로 검사합니다.

## 배포와 점검

```bash
export UE_DT_SIGNING_PRIVATE_KEY=/secure/manifest-private-key.pem
export UE_DT_SIGNING_KEY_ID=prod-2026
./tools/publish-release.sh ... --set-latest --dry-run
./tools/publish-release.sh ... --set-latest
```

publish는 lock → 임시 release → manifest·서명 → hash 재검증 → immutable release → catalog·서명 → catalog 최종 전환 순서입니다.

```powershell
UeDtLauncher doctor --config launcher.config.json --online
UeDtLauncher agent check --project ue-dt-simulator
UeDtLauncher agent update --project ue-dt-simulator
UeDtLauncher diagnostics export --config launcher.config.json
```

## 기존 portable 이전

```powershell
UeDtLauncher.Agent migrate --config C:\ue-dt\launcher.config.json
UeDtLauncher.Agent migrate --config C:\ue-dt\launcher.config.json --apply
```

첫 명령은 dry-run이며 target config나 state를 변경하지 않습니다. 자동 경로 추측이나 기존 managed config 덮어쓰기는 하지 않습니다.

## 현재 운영 gate

- 실제 MSI machine-wide 설치·service 시작은 관리자 권한 테스트 PC에서 수행해야 합니다.
- Authenticode 인증서와 RPM GPG key가 준비되기 전에는 stable artifact를 생성하지 않습니다.
- 외부 telemetry는 없으며 진단 ZIP은 민감정보를 제거한 뒤 사용자가 직접 전달합니다.
