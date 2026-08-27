# 서비스 모드 (픽셀 스트리밍 서버 / 무인 PC)

서비스 모드는 사람이 조작하지 않는 PC(픽셀 스트리밍 서버, 전시용 PC 등)에서 런처가 스스로 다음 동작을 반복하게 합니다.

```text
1. catalog/manifest에서 새 버전 확인
2. 앱을 실행한 상태로 다운로드·해시/서명 검증·패키지 해제
3. 준비가 끝난 경우에만 실행 중인 앱 프로세스 정지
4. 백업 후 업데이트를 transaction으로 적용
5. 앱 재실행 및 시작/HTTP 상태 확인
6. 시작 실패 시 이전 백업 복원 후 이전 앱 재실행
7. 다음 주기까지 대기
```

앱이 스스로 죽어 있으면(크래시 등) 다음 주기에 자동으로 다시 실행합니다.

## 실행 방법

```bash
# 5분(기본) 주기로 계속 감시
./UeDtLauncher service --config launcher.config.json

# 주기 변경 (예: 60초)
./UeDtLauncher service --config launcher.config.json --interval 60

# 한 번만 점검하고 종료 (cron이나 작업 스케줄러에 등록할 때)
./UeDtLauncher service --config launcher.config.json --once
```

`Ctrl+C` 또는 systemd `stop`(SIGTERM)으로 안전하게 종료됩니다.

## 설정

`launcher.config.json`에 `serviceMode` 블록을 추가합니다.

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/general/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "targetPlatform": "linux-x64",
  "serviceMode": {
    "intervalSeconds": 300,
    "autoRestartApp": true,
    "startupGraceSeconds": 10,
    "healthCheckUrl": "http://127.0.0.1:8080/health",
    "healthCheckTimeoutSeconds": 60,
    "rollbackOnHealthCheckFailure": true,
    "processName": "m7at10_dt"
  }
}
```

| 필드 | 기본값 | 설명 |
| --- | --- | --- |
| `intervalSeconds` | 300 | 업데이트 확인 주기(초). 최소 15초. `--interval`이 우선합니다. |
| `autoRestartApp` | true | 업데이트 후 앱을 자동으로 재실행할지 여부. false면 업데이트만 합니다. |
| `startupGraceSeconds` | 5 | 앱 재실행 후 최초 상태 확인 전 대기 시간(초). 0~300 범위로 제한됩니다. |
| `healthCheckUrl` | (없음) | 선택적 HTTP(S) 상태 확인 URL. 없으면 시작 유예 시간 동안 프로세스가 살아 있는지만 확인합니다. |
| `healthCheckTimeoutSeconds` | 60 | HTTP 상태 확인 제한 시간(초). 1~900 범위로 제한됩니다. |
| `rollbackOnHealthCheckFailure` | true | 새 앱의 상태 확인이 실패하면 직전 백업을 복원하고 이전 앱을 다시 실행합니다. |
| `processName` | (없음) | pid 파일이 없거나 오래됐을 때 실행 중인 앱을 찾기 위한 프로세스 이름(확장자 제외). |

런처가 앱을 실행하면 `app.pid` 파일(경로는 `appPidPath`로 변경 가능)에 프로세스 정보를 기록하고, 서비스 모드는 이 파일로 앱을 추적합니다. pid가 재사용된 경우를 대비해 프로세스 이름이 일치할 때만 신뢰하고, 그 외에는 `processName`으로 다시 찾습니다.

설정 파일은 매 주기마다 다시 읽기 때문에, 서버에서 채널/버전 정책을 바꾸면 서비스 재시작 없이 다음 주기부터 반영됩니다.

## Linux: systemd 등록

`/etc/systemd/system/ue-dt-launcher.service`:

```ini
[Unit]
Description=UE-DT Launcher service mode (auto update + relaunch)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=uedt
WorkingDirectory=/opt/ue-dt
ExecStart=/opt/ue-dt/UeDtLauncher service --config /opt/ue-dt/launcher.config.json
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now ue-dt-launcher.service

# 상태/로그 확인
systemctl status ue-dt-launcher.service
journalctl -u ue-dt-launcher.service -f
```

런처 자체 로그는 `logs/launcher-YYYYMMDD.log`에도 동일하게 기록됩니다.

## Windows: 무인 PC

방법 1 — 작업 스케줄러(기본 제공):

```powershell
schtasks /Create /TN "UE-DT Launcher Service" /SC ONSTART /RU SYSTEM `
  /TR "C:\ue-dt\UeDtLauncher.exe service --config C:\ue-dt\launcher.config.json"
```

방법 2 — NSSM(서비스로 등록, 자동 재시작 지원):

```powershell
nssm install UeDtLauncher "C:\ue-dt\UeDtLauncher.exe" "service --config C:\ue-dt\launcher.config.json"
nssm start UeDtLauncher
```

## 동작 규칙 정리

- 버전 비교는 `install-state.json`의 설치 버전과 원격 manifest 버전이 다르면 업데이트로 판단합니다. 카탈로그가 더 낮은 버전을 가리키면 **다운그레이드도 그대로 따라갑니다**(의도된 롤백 배포 지원).
- 앱 정지는 먼저 정상 종료(`CloseMainWindow`, 10초 대기)를 시도하고, 안 되면 프로세스 트리 전체를 강제 종료합니다.
- 다운로드·검증 실패는 앱을 중지하기 전에 끝납니다. 적용 또는 시작 확인 실패는 이전 설치를 복원하고 이전 앱을 재실행한 뒤 다음 주기에 다시 시도합니다.
- `--once` 실행에서 점검 또는 업데이트가 실패하면 종료 코드 1을 반환하므로 systemd/작업 스케줄러가 실패를 감지할 수 있습니다.
- 단일 인스턴스 잠금이 있으므로 같은 설치 폴더를 향한 GUI/CLI와 동시에 돌려도 서로 덮어쓰지 않습니다.
