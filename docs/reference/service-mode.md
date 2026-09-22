# 무인 실행과 서비스 모드

> 참고 가이드 / 문서 점검 2026-09-22 / 구현 기준 2cd28c8. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

`UeDtLauncher service` 반복 실행과 `UeDtLauncher.Agent` OS 서비스는 서로 다릅니다.

| 방식 | 현재 동작 |
| --- | --- |
| portable service | 현재 사용자 권한으로 주기적 준비·적용·앱 재시작 |
| managed-agent service --once | IPC로 Agent에 한 회차 요청 |
| Windows Service/systemd Agent | IPC 요청 대기. 설치만으로 주기적 업데이트가 시작되지 않음 |

현재 Agent의 `AgentWorker`에는 자동 점검 스케줄러가 없습니다. 관리형 무인 운영은 운영 계정·스케줄러·실행 세션을 별도로 검증해야 합니다. Windows 서비스 계정에서 실행한 앱은 로그인 사용자 데스크톱에 표시되지 않습니다.

## 명령

아래 반복 명령은 `deploymentMode=portable` 전용입니다. 서버·서명·credential은 [설정 레퍼런스](guide-03-launcher-usage.md)를 따르고 현재 계정이 쓸 수 있는 설치·상태 경로를 사용합니다.

```bash
./UeDtLauncher service --config launcher.config.json
./UeDtLauncher service --config launcher.config.json --interval 60
./UeDtLauncher service --config launcher.config.json --once
```

관리형은 반복 실행이 거부되며 `service --config launcher.config.json --once`만 사용합니다. Agent는 GUI/CLI가 지정한 임의 파일이 아니라 자신의 관리 설정으로 실행합니다. `--interval`은 관리형 스케줄러 설정이 아닙니다.

## serviceMode 설정

전체 런처 설정에 추가하는 블록입니다.

```json
{
  "serviceMode": {
    "intervalSeconds": 300,
    "autoRestartApp": true,
    "startupGraceSeconds": 5,
    "healthCheckUrl": "http://127.0.0.1:8080/health",
    "healthCheckTimeoutSeconds": 60,
    "rollbackOnHealthCheckFailure": true,
    "processName": "YourApplication"
  }
}
```

| 필드 | 동작 |
| --- | --- |
| intervalSeconds | 기본 300초, 최소 15초. portable --interval 우선, 시작 시 결정 |
| autoRestartApp | 기본 true. 앱이 꺼졌거나 적용으로 중지한 경우 시작 |
| startupGraceSeconds | 기본 5초, 0~300초 |
| healthCheckUrl | 선택적 HTTP(S). 생략하면 시작 유예 뒤 프로세스 생존 확인 |
| healthCheckTimeoutSeconds | 기본 60초, 1~900초 |
| rollbackOnHealthCheckFailure | 기본 true. 이전 백업·manifest가 있어야 복원·재실행 가능 |
| processName | PID 추적 실패 시 프로세스 이름으로 검색. 확장자 제외 |

매 회차 설정을 다시 읽지만 반복 간격은 시작 시 계산되므로 간격 변경은 재시작 후 반영됩니다. PID 기록은 선택 설치의 상태 경로에 저장됩니다. 이름 검색은 동명 프로세스를 구분하지 못할 수 있으므로 전용 계정·환경에서 검증하세요.

## 순서와 제한

1. 릴리스 선택, 다운로드·서명·해시 검사, staging 준비를 먼저 수행합니다.
2. 실제 적용 변경이 있고 앱이 실행 중이면 정상 종료를 시도하고 필요 시 프로세스 트리를 종료합니다.
3. transaction 적용 후 필요 시 앱 시작과 상태 확인을 수행합니다.
4. 실패 시 복원 가능한 백업으로 되돌리고 이전 앱 재시작을 시도합니다.
5. 다음 주기를 기다립니다. --once 오류는 종료 코드 1입니다.

버전 비교는 문자열의 차이를 감지하며 단순히 숫자가 큰 버전만 적용하는 규칙이 아닙니다. 서버 정책·메타데이터 검증도 통과해야 합니다. 버전별 설치에서 이전 프로세스 탐색·동시 실행 방지·실제 UE 종료/재기동은 현장 검증 대상입니다.

Linux Agent unit은 `packaging/linux/ue-dt-launcher-agent.service`이며 `uedt`로 실행합니다. 기존 portable loop를 직접 systemd/작업 스케줄러에 등록하는 방식은 별도 운영 방식입니다. 관리 Agent와 같은 설치를 동시에 소유하게 구성하지 마세요.

[상용 배포 준비](commercial-deployment.md)와 [검증 범위](distribution-validation.md)를 확인한 뒤 실제 UE·픽셀 스트리밍에 적용하세요.
