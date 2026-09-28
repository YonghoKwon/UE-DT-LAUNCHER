# 무인 실행과 서비스 모드

문서 점검 2026-09-28 / `codex/runtime-safety-completion`. **현재 서비스는 앱을 자동 종료하거나 다른 버전으로 자동 전환하지 않습니다.** [실행 안전성·수동 복구](runtime-safety.md)를 함께 읽으세요.

`UeDtLauncher service` 반복 실행과 `UeDtLauncher.Agent` OS 서비스는 서로 다릅니다.

| 방식 | 현재 동작 |
|---|---|
| portable service | 현재 사용자 권한의 점검 반복. 정지 확인된 같은 설치만 적용·선택적 시작 |
| managed-agent service --once | IPC로 Agent에 한 회차 요청 |
| Windows Service/systemd Agent | IPC 요청 대기. 설치만으로 주기적 업데이트가 시작되지 않음 |

AgentWorker의 자동 스케줄러는 미구현입니다(OPS-07). Windows 서비스 계정에서 실행한 앱은 로그인 사용자 데스크톱에 표시되지 않습니다. GUI용 실행과 무인 service 실행을 혼용하지 마세요.

## 명령

아래 반복 명령은 `deploymentMode=portable` 전용입니다.

```bash
./UeDtLauncher service --config launcher.config.json
./UeDtLauncher service --config launcher.config.json --interval 60
./UeDtLauncher service --config launcher.config.json --once
```

관리형은 `--once`만 지원하며 자신의 보호 설정을 사용합니다. `--interval`은 Agent 스케줄러 설정이 아닙니다. runtime 추적 capability 없는 구형 클라이언트의 변경 요청은 거부됩니다.

## serviceMode 설정

```json
{
  "serviceMode": {
    "intervalSeconds": 300,
    "autoRestartApp": true,
    "startupGraceSeconds": 5,
    "healthCheckUrl": "http://127.0.0.1:8080/health",
    "healthCheckTimeoutSeconds": 60
  }
}
```

| 필드 | 현재 의미 |
|---|---|
| intervalSeconds | 기본 300초, 최소 15초. portable --interval 우선, 시작 시 결정 |
| autoRestartApp | 기본 true. 정지 확인·설치 적용 후 supervised 시작. 실행 중 앱을 중지/재시작하는 옵션 아님 |
| startupGraceSeconds | 기본 5초, 0~300초. 시작 후 대기 |
| healthCheckUrl | 선택적 HTTP(S). 유예 후 Running 확인 및 한 번의 HTTP 성공 검사 |
| healthCheckTimeoutSeconds | 기본 60초, 1~900초. 해당 HTTP 요청 제한 |
| rollbackOnHealthCheckFailure | 기존 설정 호환으로 읽지만 자동 rollback하지 않음 |
| processName | 기존 설정 호환으로 읽지만 검색/종료에 사용하지 않음 |

매 회차 설정을 다시 읽지만 반복 간격은 시작 시 계산하므로 간격 변경은 재시작 후 반영됩니다.

## 한 회차 순서

1. 배포 metadata 확인 → 서비스 선택 잠금 → 실행 상태·보호된 활성 버전 검사.
2. Running/LaunchPending/Unknown 또는 다른 활성 버전이면 중단. 설치·backup·transaction journal은 변경하지 않음.
3. 공통 설치 lease 안에서 prepare/transaction 적용. 시작 전에 단일 service snapshot에 StartupHealthPending 저장 후 같은 서비스 계정의 runtime-host로 시작.
4. health 성공 저장 후 Ready. 시작/health 실패·중간 종료는 pending 또는 NeedsReview와 backup 보존. 앱 자동 종료·파일 rollback·이전 앱 재실행 없음.
5. 다음 주기 대기. `--once` 오류는 종료 코드 1.

정지 확인과 서비스 버전 선택 변경은 `runtime recover --dry-run`으로 먼저 점검한 뒤, 관리자가 `--confirm-stopped --service-selection`으로 명시합니다. 실제 명령은 [수동 복구 안내](runtime-safety.md)를 따릅니다. 수동 확인은 OS가 증명한 종료와 다릅니다.

Linux Agent unit은 `packaging/linux/ue-dt-launcher-agent.service`이며 `uedt`로 실행합니다. portable loop와 Agent가 같은 설치를 동시에 소유하지 않도록 하세요.

현재 합성 앱의 health 500/timeout/연결 실패·중간 CLI 종료는 Windows/WSL에서 검증했습니다. 실제 설치 서비스 계정·UE/RHEL 운영 시험은 미완료입니다. [후속 검증](runtime-safety-completion-validation.md)과 [상용 배포 준비](commercial-deployment.md)를 확인하세요.
