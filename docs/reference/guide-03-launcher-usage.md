# 런처 설정·CLI 레퍼런스

2026-09-28: `sample-config` 기본은 DistributionServer/schema 3 요청 서명입니다. 기존 정적 예제는 `--mode legacy-catalog`로 생성합니다. [새 명령·키 저장·doctor 안내](intranet-auth.md)를 참고하세요. 기존 파일은 `--force` 없이 덮어쓰지 않습니다.

> 현재 설정 가이드 / 2026-09-28, 코드 0d12957과 대조. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

현재 기본 배포는 DistributionServer의 ZIP + 외부 `release.json` 접수·승인 방식입니다. 서버는 [서버 가이드](distribution-workflow.md), 게시는 [게시 가이드](feature-workflow.md), 화면은 [GUI 사용법](launcher-user-guide.md), 개요는 [README](../../README.md)를 참고하세요.

## 설정 파일의 역할

| 설정 | 역할 |
| --- | --- |
| Agent 보호 설정 | 서버 URL, credential 이름, 공개키, 설치·상태 루트와 실행 설정 |
| GUI 표시 설정 | `clientProfile`, 프로젝트 이름·이미지·정렬, 화면의 릴리스 선택 |
| portable 설정 | Agent 없이 현재 사용자 권한으로 업데이트하는 전체 설정 |

GUI는 `gui --config <경로>` → 실행 파일 옆 `launcher.config.json` → 관리 설정 순으로 찾습니다. 명시한 파일이 없다고 다음 설정으로 넘어가지는 않습니다. 현재 작업 폴더 자동 탐색은 없으며 명시한 상대 경로만 현재 작업 폴더 기준입니다.

CLI의 `run --config` 등은 지정한 파일을 읽고, 생략하면 현재 작업 폴더의 `launcher.config.json`을 사용하므로 GUI와 다릅니다.

관리 설정 기본 위치는 Windows `%ProgramData%\UE-DT Launcher\config\launcher.config.json`, Linux `/etc/ue-dt-launcher/launcher.config.json`입니다. 관리형 DistributionServer GUI는 실제 작업 때 관리 설정을 다시 읽고 Agent도 자신의 보호 설정으로 검증합니다. GUI 설정만 고쳐 서버 권한을 늘릴 수 없습니다.

## 기존 HTTPS/Bearer 관리 설정 예시

Linux 경로 예시입니다. Windows에서는 경로를 Windows 관리 디렉터리로 바꾸고 `targetPlatform`을 `windows-x64`로 지정합니다. 공개키 상대 경로는 관리 설정 파일 기준입니다.

```json
{
  "schemaVersion": 2,
  "deploymentMode": "managed-agent",
  "distributionServerUrl": "https://updates.example.com",
  "projectId": "demo",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "targetPlatform": "linux-x64",
  "installDir": "/var/lib/ue-dt-launcher/apps",
  "stateRootDir": "/var/lib/ue-dt-launcher/state",
  "logDir": "/var/log/ue-dt-launcher",
  "requireSignedManifests": true,
  "security": {
    "credentialName": "company-distribution",
    "allowedDownloadHosts": ["updates.example.com"],
    "trustedSigningKeys": [
      {"keyId": "release-1", "publicKeyPath": "release-public.pem"}
    ]
  }
}
```

`distributionServerUrl`에서 `/api/v1/catalog` 주소가 구성됩니다. 서버는 토큰 + 실제 IP + 프로젝트/환경/채널/버전 grant로 접근을 제한합니다. `clientProfile=developer`는 UI 선택지를 늘릴 뿐 서버 접근 권한이 아닙니다.

`installDir`은 버전 설치 루트이며 실제 설치는 `{project}/{environment}/{channel}/{version}/{platform}`으로 분리됩니다. 상태·staging·백업도 릴리스 단위입니다. 예전 단일 설치의 `stagingDir`, `backupDir`, `installedManifestPath`를 버전별 경로로 직접 조립하지 마세요.

사내 CA가 필요하면 `security.customCaCertificatePath`를 사용합니다. HTTPS 검증을 끄거나 URL에 비밀번호를 넣지 않습니다. 허용 host와 공개키는 실제 배포 서버에 맞춰 지정합니다.

## 인증정보와 점검

기존 HTTPS/Bearer credential 명령입니다. 신규 schema 3의 키 생성·등록·교체는 [요청 서명 안내](intranet-auth.md)를 따릅니다. PC 개인키와 서버 배포 검증 공개키를 혼용하지 않습니다.

```text
UeDtLauncher credential set --name company-distribution
UeDtLauncher credential status --name company-distribution
UeDtLauncher doctor --config launcher.config.json --online
UeDtLauncher agent status
UeDtLauncher agent check --project demo
```

토큰은 대화형 입력으로 저장하고 JSON·명령 이력에 직접 넣지 않습니다. Linux credential은 0600이므로 Agent 계정이 읽을 수 있는 소유권도 확인합니다. Windows는 DPAPI LocalMachine과 파일 ACL을 함께 사용합니다.

## 업데이트·실행

```text
UeDtLauncher run --config launcher.config.json
UeDtLauncher run --config launcher.config.json --no-launch
UeDtLauncher run --config launcher.config.json --repair --no-launch
UeDtLauncher agent update --project demo --environment dev --channel dev --version 1.2.0
UeDtLauncher diagnostics export --config launcher.config.json --output diagnostics.zip
```

- `run`: 선택 릴리스 업데이트 후 실행. `--no-launch`는 설치만 수행합니다.
- `--repair`: 파일 해시를 재검증하고 손상·누락 파일을 복구합니다.
- `agent update`: 보호 설정으로 설치만 수행하며 UE 앱을 실행하지 않습니다. 명시 버전 선택은 DistributionServer 설정이 필요합니다.
- GUI는 현재 OS 패키지만 선택합니다. CLI/Agent 플랫폼도 실행 PC와 일치하게 설정하세요.
- 이전 버전 사용은 개발자 `exact` 선택과 해당 버전 설치로 수행합니다. 백업 복원인 `rollback`과 구분합니다.

portable Linux CLI는 같은 설정에서 `deploymentMode=portable`로 지정하고 현재 계정이 쓸 수 있는 경로를 사용합니다. 같은 업데이트 엔진을 거치지만 보호된 Agent 경계는 사용하지 않습니다.

## 성능 옵션

관리형은 Agent 보호 설정, portable은 해당 설정의 선택 필드입니다.

```json
{
  "performance": {
    "downloadConcurrency": 2,
    "hashConcurrency": 2,
    "reusePreviousInstallations": true
  }
}
```

| 옵션 | 기본 / 허용값 | 동작 |
|---|---|---|
| downloadConcurrency | 2 / 1~8 | 스트리밍 다운로드 동시 수. 1은 순차 진단 |
| hashConcurrency | 2 / 1~4 | 해시 검사 동시 수 |
| reusePreviousInstallations | true / boolean | 인증된 새 버전 설치에 한해 같은 프로젝트/환경/채널/OS의 최근 3개 설치에서 파일 복사 후 해시 검증 |

기존 설치 repair는 재사용하지 않습니다. hard link·공용 콘텐츠 캐시는 사용하지 않으며 transaction 적용/rollback은 직렬입니다. 큰 pak 파일 내용이 바뀌면 파일 전체를 받을 수 있어 합성 시험의 90% 절감이 실제 UE에 보장되지는 않습니다.

서버 설정의 `intakeWorkers`는 기본 1, 허용값 1 또는 2입니다. bounded queue는 worker 수의 2배이며 초과 업로드는 다음 스캔에서 다시 발견합니다. 2 worker는 선택 기능이고 자동 승인을 추가하지 않습니다. inspect 진행/공간 추정은 공간 예약이나 게시 성공 보장이 아닙니다. DB 교체 전 기존 프로세스를 중지하고 백업해야 합니다.

[과거 측정·재현 조건](archive/validation/performance-validation.md)은 이력입니다. PERF-03 지연 미달과 회사 실측 조건은 [개선 대장](../../IMPROVEMENTS.md)에 계속 남습니다.

## 실행 차단과 이전 제한

실행 중/Pending/Unknown에서는 update·repair·rollback을 하지 않습니다. 상태 확인과 정지 후 명시적 복구는 [실행 안전성](runtime-safety.md)을 따릅니다. `Agent migrate --apply`는 동일 설치 공유 위험으로 현재 차단되며 dry-run만 지원합니다. `import-install`의 명시적 복사와 사용자 세이브 이전은 서로 다른 작업입니다.

## 선택적 UE 데이터 경로 (2026-10-02)

기존 **schema3 전체 설정에 추가할 블록**입니다. 아래만으로는 서버/credential 설정이 완성되지 않습니다. `sample-config`는 기존처럼 기본 비활성화를 유지합니다.

```json
{
  "runtimeData": {
    "enabled": true,
    "adapter": "unreal-engine",
    "policy": "per-user-per-release"
  }
}
```

| 항목 | 계약 |
|---|---|
| 미지정/disabled | 기존 LaunchArguments와 경로 동작 유지. schema1/2 자동 변환 없음 |
| 지원 | schema3 DistributionServer, 위 adapter/policy만 지원 |
| 기본 루트 | Windows `%LOCALAPPDATA%/UE-DT Launcher/RuntimeData`, Linux `$XDG_DATA_HOME/UE-DT Launcher/RuntimeData`(미설정이면 `~/.local/share`) |
| 하위 구조 | `users/<OS owner SHA-256>/releases/<project>/<environment>/<channel>/<version>/<platform>/user`, `logs/<launch attempt>.log` |
| rootDirectory | 선택적 절대 경로. 환경변수/템플릿 확장 없음. 관리형은 보호된 Agent 설정에서 결정 |
| 실행 | 인증된 사용자 runtime-host가 owner/권한/링크/중첩 검사와 실제 write probe 후 UserDir/abslog 인수 생성 |
| 충돌 | 기존 `-UserDir`/`-abslog`, 설치/state/backup/credential 경로와 중첩은 거부. 자동 fallback 없음 |
| 호환 | IPC v1의 선택 필드와 `runtime-data-v1` capability. 새 모드 실행은 지원하는 런처/Agent 동시 갱신 필요 |
| 데이터 | 버전 간 자동 공유/복사 없음. repair·payload backup 복원·prune은 외부 사용자 데이터 유지 |

후속 cf99ba1: rootDirectory를 plan에서 누락하면 불명 상태로 처리하고 명시적인 null만 기본 사용자 루트로 인정합니다. 실행 시 확정된 릴리스와 설치 경로를 대조하며, user host의 credential 경로도 보호합니다. 실제 사용자 디렉터리의 Linux 공용 쓰기 권한은 sticky bit와 관계없이 거부합니다. UserDir·로그 디렉터리 모두 write/flush를 확인합니다.

`doctor`는 정책을 확인합니다. Portable은 현재 사용자 루트 권한을 읽기 전용으로 검사하며, 관리형 Agent 진단은 **사용자 세션 쓰기 검사를 지연**한다고 명시합니다. 실제 write/flush 접근 검사는 실행 host가 합니다. doctor 성공을 서비스 계정/사용자 계정의 실제 실행 성공으로 확대하지 마세요.

UE 인수는 앱의 독자적인 쓰기 위치를 강제 변경하지 않습니다. ma0t10_dt의 DTCore CustomLogs는 설치 안에 쓰는 한계가 남고, 실제 SaveGame/사용자 설정 보존 수용과 회사 서비스 계정 검증도 남습니다. [이번 증거](archive/validation/real-ue-data-safety-validation.md)

## 레거시 호환과 검증 범위

`catalogUrl`/직접 `manifestUrl`, `generate-manifest`, `update-catalog`, `publish-release`는 기존 정적 배포 호환 기능입니다. 기본 `sample-config`는 DistributionServer/schema 3 요청 서명 설정을 생성합니다. 정적 예제는 `--mode legacy-catalog`를 명시한 경우에만 사용합니다. 공개 `/catalogs/general` 또는 `/projects` 구조는 현재 통합 서버의 보안 모델이 아닙니다.

무인 실행은 [서비스 모드](service-mode.md), 이전은 [통합 운영](distribution-workflow.md), 패키징은 [상용 배포 준비](commercial-deployment.md)를 참고하세요. [2026-09-12 검증](archive/validation/distribution-validation.md)은 Windows GUI/Agent·Linux CLI 테스트 패키지 결과이며 실제 회사 RHEL·실제 UE 검증 완료를 뜻하지 않습니다.
