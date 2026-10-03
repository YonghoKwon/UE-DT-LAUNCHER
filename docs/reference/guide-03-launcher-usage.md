# 런처 설정·CLI 레퍼런스

2026-09-28: `sample-config` 기본은 DistributionServer/schema 3 요청 서명입니다. 기존 정적 예제는 `--mode legacy-catalog`로 생성합니다. [새 명령·키 저장·doctor 안내](intranet-auth.md)를 참고하세요. 기존 파일은 `--force` 없이 덮어쓰지 않습니다.

> 현재 설정 가이드 / 2026-10-03, codex/operations-hardening-closure와 대조. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

현재 기본 배포는 DistributionServer의 ZIP + 외부 `release.json` 접수·승인 방식입니다. 서버는 [서버 가이드](distribution-workflow.md), 게시는 [게시 가이드](feature-workflow.md), 화면은 [GUI 사용법](launcher-user-guide.md), 개요는 [README](../../README.md)를 참고하세요.

## 설정 파일의 역할

취소/재개 CLI의 owner/session은 실제 OS 신원이며 임의 주장으로 바꾸지 않습니다. 재개는 새 권한/Manifest 검증이 필요합니다. `performance.resumeCacheBytes`는 명시 예산으로만 활성화되며 partial/verified/new를 합산합니다. 비활성 `operation discard`는 기록 archive와 live 슬롯 회복이며 다른 사용자 캐시/데이터를 일괄 삭제하지 않습니다.

| 설정 | 역할 |
| --- | --- |
| Agent 보호 설정 | 서버 URL, credential 이름, 공개키, 설치·상태 루트와 실행 설정 |
| GUI 표시 설정 | 프로젝트 이름·이미지·정렬과 릴리스 선택 기본값. 화면 종류는 실행 파일의 빌드 에디션 |
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

`distributionServerUrl`에서 `/api/v1/catalog` 주소가 구성됩니다. 서버는 PC 인증 + 실제 IP + 배포 grant로 접근을 제한합니다. 새 GUI의 clientProfile 값은 종류 결정에 사용하지 않습니다. 아래 기존 CLI/운영 설정의 필드는 레거시 선택 호환용이며 서버 접근 권한이 아닙니다.

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

## 최초 연결 준비도와 오류 조치 (2026-10-03)

GUI 설정에서 **연결·준비 상태 점검**을 열면 오프라인 설정 검사를 수행합니다. **온라인 연결 점검**은 실제 인증·서명·요청 결속을 검증하고 현재 선택의 권한과 추천을 확인합니다. 검사 대상이 바뀌면 이전 결과를 적용하지 않습니다. 검사 창은 설치/실행을 하지 않으며 닫기는 진행 중인 해당 검사만 취소합니다.

```powershell
# Windows PC: 일반/개발자 표시 설정 또는 portable 운영 설정
.\UeDtLauncher.exe doctor --config .\launcher.config.json --format text
.\UeDtLauncher.exe doctor --config .\launcher.config.json --online --format text
.\UeDtLauncher.exe diagnostics export --config .\launcher.config.json --output .\diagnostics.zip
```

```bash
# Linux CLI: 관리자에게 준비도 JSON/텍스트를 전달
./UeDtLauncher doctor --config /etc/ue-dt-launcher/launcher.config.json --online --format text
```

| 결과/코드 | 의미 | 담당자와 다음 조치 |
|---|---|---|
| configuration-invalid | 설정 없음/읽기/검증 실패 | 관리자 설정 수정 → 다시 확인. 조회 재시도는 설치하지 않음 |
| service-unavailable | 관리형 업데이트 서비스 미연결 | 관리자 서비스 점검. portable에는 서비스 불필요 |
| client-upgrade-required | 읽기 전용 상세 진단 미지원 | 런처/업데이트 서비스 동시 갱신. 구형 doctor 자동 호출 없음 |
| authentication-failed / 401 | PC 인증 등록/폐기 등 확인 필요 | 관리자 PC 키 등록·폐기·읽기 권한 점검 |
| access-denied / 403 | 현재 PC 주소/배포 권한 거부 | 관리자 IP와 선택 트랙 권한 점검 |
| no-authorized-release | 정상 연결됐으나 해당 PC·선택 조건의 배포 없음 | 관리자 승인 목록·grant 확인. 설치/실행 우회 없음 |
| no-promoted-release | 허용 승인판은 있으나 일반 추천 미지정 | 관리자 명시적 promote. 개발자 exact는 현재 권한 안에서 선택 |
| integrity-failed | 서명·요청 결속 등 보안 검증 실패 | 검사를 끄지 말고 관리자 키·배포 확인 |
| deferred | 온라인 또는 실제 사용자 환경 검사 보류 | 온라인 점검 또는 실행 직전 host 검사 필요 |

`Healthy`/종료0은 기술적 실패가 없다는 뜻입니다. `preparationState`가 action-required이면 조치, verification-pending이면 미검증, checks-passed이면 현재 검사를 통과한 상태입니다. 실제 설치 용량·파일·사용자 쓰기 검증은 기존 작업 경계에서 수행합니다. CLI 기본 출력은 JSON이며 기존 필드를 유지하고 Code/State/Subject/ActionOwner/NextAction/Target/SupportId를 추가합니다.

확인된 응답 Target이 없으면 요청 Target을 대신 채우지 않습니다. 준비도/state 누락 또는 알 수 없는 state는 추가 검증 필요로 남습니다. 다른 대상·잘못된 검사 목록·성공 모순은 diagnostic-response-invalid이며 관리자 서비스 점검 후 재시도합니다. 설정 실패의 원래 코드와 지원 ID는 보존합니다.

관리형 CLI 지원 ZIP은 표시 설정과 Agent 진단만 받으며 보호 state/로그를 직접 읽지 않습니다. 보호 자료의 관리자 내보내기는 기존 Agent 진단 경로를 사용합니다. GUI 지원 로그 ZIP에는 마지막 확인한 진단 결과가 있으면 `doctor.json`을 함께 넣습니다. 키·토큰·인증 헤더·사용자 경로는 제거하며 자동 외부 전송하지 않습니다.

### 실행 차단과 이전 제한 안내

### 보류 중인 GUI 수용 체크리스트

마우스 검증은2026-10-03 재개됐습니다. 관리형 일반의 실제 v1 설치·v2 업데이트/실행·해시·v1 보존·창 종료 후 자식 유지·정상 종료, 개발자 exact v2 확인 취소 불변은 통과했습니다. 완료 후 취소 버튼 잔존은 수정 대기입니다. 아래는 **같은 게시본**의 관리형/portable·별도 일반/개발자 EXE에서 확인할 단일 목록이며 부분 성공을 전체 수용으로 합산하지 않습니다.

| 사례 | 재개 후 확인 기준 | 현재 최신 후보 전체 판정 |
|---|---|---|
| 최초 조회·관리자 승격 | 미설치 재연결/조회는 설치·실행0건, 추천 대기→promote 후 정상 조회 | 보류 |
| 설치·업데이트·실행 | v1→v2 정확한 실행, v1 보존·GUI 종료 후 자식 유지 | 관리형 일반 통과, portable 보류 |
| 개발자 exact 확인 | 정확한 선택, 취소 불변, 승인한 버전만 실행 | 관리형 v2 선택/취소 통과, 실행 승인·portable 보류 |
| 실행 중 보호 | 생존/Running 확인 후 update·repair·rollback 차단, 보호 hash 동일 | 보류 |
| 문제 해결 | 정상은 조회만, 손상은 복구/재검증, 미설치는 설치 안내 | 보류 |
| 정상 backup 복원 | 정상 상태 추가 repair 후 전체 backup hash 확인, 취소/적용 | 보류 |
| preview·재시도 | 변경된 preview 거부, 새 확인 필수, 복원 후 조회 재시도는 재복원하지 않음 | 보류 |
| 설정·오류·진단 | 수정 후 조회만,401/403/빈 목록/서명/서비스·서버 단절 구분, 원인/조치/지원 ID | 보류·403 양 모드 이력 있음 |

지원 해상도 추가 조건·내레이터·실제 UE 데이터·회사 계정은 각각 UI-01/03·USER-01·운영 인수 항목에서 별도 관리합니다.

### 실행 안전성

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

명시적 승격 Catalog의 `selectionPolicy=explicit-promotion-v1`을 Core·Agent·GUI가 전달합니다. latest는 IsLatest 추천만 사용하고 없으면 지정 대기, exact는 허용된 승인 목록에서 선택합니다. 기존 정적 Catalog나 구 서버 응답에 정책 필드가 없으면 기존 선택 동작을 유지합니다. 새 서버의 구형 요청 목록은 미승격판을 제외하므로 구형 fallback으로 미승격판이 자동 선택되지 않습니다.

`catalogUrl`/직접 `manifestUrl`, `generate-manifest`, `update-catalog`, `publish-release`는 기존 정적 배포 호환 기능입니다. 기본 `sample-config`는 DistributionServer/schema 3 요청 서명 설정을 생성합니다. 정적 예제는 `--mode legacy-catalog`를 명시한 경우에만 사용합니다. 공개 `/catalogs/general` 또는 `/projects` 구조는 현재 통합 서버의 보안 모델이 아닙니다.

무인 실행은 [서비스 모드](service-mode.md), 이전은 [통합 운영](distribution-workflow.md), 패키징은 [상용 배포 준비](commercial-deployment.md)를 참고하세요. [2026-09-12 검증](archive/validation/distribution-validation.md)은 Windows GUI/Agent·Linux CLI 테스트 패키지 결과이며 실제 회사 RHEL·실제 UE 검증 완료를 뜻하지 않습니다.
