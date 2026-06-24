# 가이드 3편 — 런처 사용법 (일반 사용자 / 개발자 / 무인 서버)

> 변경된(하드닝 적용) 런처의 설치, 설정, GUI/CLI 사용법, 문제 해결을 다룹니다.
> 서버 구축은 [1편](guide-01-linux-server-setup.md), 릴리스 올리기는 [2편](guide-02-publish-package.md)을 보세요.

## 1. 설치와 최초 설정

클라이언트 PC에 폴더 하나를 만들고 두 개(서명을 쓰면 세 개)의 파일만 두면 됩니다:

```text
C:\ue-dt\  (또는 /opt/ue-dt/)
├── UeDtLauncher.exe          # 런처 (scripts/publish-*-x64 스크립트로 빌드)
├── launcher.config.json      # 이 PC의 역할을 정하는 설정 파일
└── manifest-public-key.pem   # 서명 검증을 켠 경우만
```

런처가 실행되면서 만들어지는 것들: `app/`(설치된 프로젝트), `.staging/`(다운로드 임시), `.backup/`(롤백용 백업), `logs/`(로그), `installed-manifest.json`, `install-state.json`, `app.pid`.

설정 템플릿 생성:

```bash
UeDtLauncher sample-config --output launcher.config.json
```

### launcher.config.json 전체 필드

**배포 선택 (어떤 빌드를 받을지)**

| 필드 | 기본값 | 설명 |
| --- | --- | --- |
| `catalogUrl` | - | 카탈로그 주소. 예: `http://서버IP/catalogs/general/catalog.json` |
| `projectId` | - | 받을 프로젝트 ID |
| `clientProfile` | `general` | `general`(일반) 또는 `developer`(개발자). GUI 화면과 권한이 달라집니다 |
| `environment` | `prod` | `prod`(가동/운영) / `dev`(개발). 일반 프로필은 `prod`만 허용 |
| `channel` | `stable` | `stable` / `beta` / `dev`. 일반 프로필은 `stable`만 허용 |
| `versionPolicy` | `latest` | `latest`(최신 자동) / `exact`(`requestedVersion`에 지정한 버전 고정) |
| `requestedVersion` | - | `versionPolicy: "exact"`일 때 받을 버전 |
| `targetPlatform` | OS 자동 | `windows-x64` / `linux-x64` |
| `manifestUrl` | - | 카탈로그 없이 manifest 하나만 바라보는 단순 모드 (`catalogUrl`이 비어 있을 때만 사용) |

**보안**

| 필드 | 기본값 | 설명 |
| --- | --- | --- |
| `catalogSignatureUrl` / `catalogPublicKeyPath` | - | catalog 서명 검증 (`.sig` URL + 공개키 파일 경로) |
| `manifestSignatureUrl` / `manifestPublicKeyPath` | - | manifest 서명 검증. 카탈로그 모드에서는 catalog의 릴리스 항목에서 자동 설정됨 |
| `requireSignedManifests` | `false` | **true면 서명 검증이 실제로 수행되지 않는 한 업데이트를 거부.** 운영 배포에서 켜는 것을 권장. false면 서명 미설정 시 경고 로그만 남깁니다 |

**동작/경로**

| 필드 | 기본값 | 설명 |
| --- | --- | --- |
| `installDir` | `app` | 프로젝트 설치 폴더 |
| `stagingDir` / `backupDir` | `.staging` / `.backup` | 다운로드 임시 / 백업 폴더 |
| `installedManifestPath` | `installed-manifest.json` | 설치된 파일 목록 기록 |
| `installStatePath` | `install-state.json` | 설치 버전/시각 기록 (롤백·서비스 모드가 사용) |
| `appPidPath` | `app.pid` | 런처가 실행한 앱 프로세스 기록 (서비스 모드가 사용) |
| `logDir` | `logs` | 일별 로그 파일 폴더 (`launcher-YYYYMMDD.log`, 14일 보관) |
| `maxBackupCount` | `3` | 보관할 백업 개수. 초과분은 오래된 것부터 자동 삭제 |
| `launchAfterUpdate` | `true` | 업데이트 후 앱 자동 실행 |
| `launchArguments` | - | 앱 실행 인자 배열. 예: `["-log"]` |
| `removeFilesNotInManifest` | `false` | manifest에 없는 설치 파일 삭제 (깨끗한 동기화를 원하면 true) |
| `maxRetryCount` / `httpTimeoutSeconds` | `3` / `120` | 다운로드 재시도 횟수 / HTTP 타임아웃. 4xx 오류는 재시도하지 않고, 일시 오류만 지수 백오프로 재시도합니다 |
| `serviceMode` | - | 무인 서버용. `{ "intervalSeconds": 300, "autoRestartApp": true, "processName": "m7at10_dt" }` — 자세한 내용은 [service-mode.md](service-mode.md) |
| `selfUpdate` | - | 런처 자체 업데이트. `autoApply: true`면 다음 실행 시 자동 교체 |
| `projects` | - | GUI 프로젝트 카드 목록(이름/설명/이미지/정렬/프로필별 표시). [launcher-ui-customization.md](launcher-ui-customization.md) 참고 |

### 역할별 설정 예시

일반 사용자 PC — 운영 최신 버전만:

```json
{
  "catalogUrl": "http://updates.example.com/catalogs/general/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod", "channel": "stable", "versionPolicy": "latest",
  "targetPlatform": "windows-x64",
  "requireSignedManifests": true,
  "catalogSignatureUrl": "http://updates.example.com/catalogs/general/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem"
}
```

개발자 PC — dev 채널 자유 선택 (서버 Basic Auth 계정 필요):

```json
{
  "catalogUrl": "http://devuser:비밀번호@updates.example.com/catalogs/developer/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev", "channel": "dev", "versionPolicy": "latest",
  "targetPlatform": "windows-x64"
}
```

픽셀 스트리밍 서버 — 무인 자동 업데이트+재실행:

```json
{
  "catalogUrl": "http://updates.example.com/catalogs/general/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod", "channel": "stable", "versionPolicy": "latest",
  "targetPlatform": "windows-x64",
  "launchArguments": ["-RenderOffscreen", "-PixelStreamingURL=ws://localhost:8888"],
  "serviceMode": { "intervalSeconds": 300, "autoRestartApp": true, "processName": "m7at10_dt" }
}
```

## 2. GUI 사용법

실행: `UeDtLauncher.exe` 더블클릭 (인자 없이 실행하면 GUI).

### 일반 사용자 모드 (`clientProfile: "general"`)

화면 구성: 왼쪽에 프로젝트 검색/목록, 오른쪽에 큰 **실행** 버튼과 4개의 정보 타일.

- **설치 상태** — 최신/업데이트 가능/설치 필요/오류
- **설치 버전** — 설치된 버전과 카탈로그 최신 버전을 나란히 표시. 초록=최신 상태, 주황=업데이트 필요
- **실행** — 업데이트 확인 → 변경 파일만 다운로드 → 적용 → 실행까지 자동. 진행 중에는 `다운로드 중 · 파일 12/87 · 34.2 MB/s · 전체 46%` 형태로 표시되고, 작업 중에는 버튼이 비활성화됩니다
- **설정 팝업** — 설치 버전/캐시·백업 용량 확인, 설치 폴더 열기, **이전 버전으로 롤백**, 로그 ZIP 저장

오류가 나면 알기 쉬운 메시지의 오류 창이 뜨고 **다시 시도** 버튼으로 재시도할 수 있습니다. 문제 보고 시 "로그 ZIP 저장"으로 만든 파일을 관리자에게 보내세요.

### 개발자 모드 (`clientProfile: "developer"`)

추가 기능: 가동/개발·채널·버전 정책 콤보박스(바꾸면 즉시 해당 배포 기준으로 동작), 업데이트만/검증·복구(전체 해시 재검사)/캐시 정리/백업 정리(최근 `maxBackupCount`개는 보존)/**롤백**(직전 백업으로 복원, 확인 다이얼로그 포함)/로그 보기·지우기.

## 3. CLI 명령 레퍼런스

```text
UeDtLauncher <command> [options]
```

| 명령 | 용도 | 주요 옵션 |
| --- | --- | --- |
| `gui` (또는 인자 없음) | GUI 실행 | `--gui`(GUI 강제 실행) `--cli`(CLI 강제 실행 — 기본적으로 `run`으로 매핑, `--config` 등 인자 추가 가능). `--gui`와 `--cli`를 함께 쓰면 오류(종료 코드 2) |
| `run` | 업데이트(+실행) 1회 | `--config <file>` `--repair`(전체 재검증) `--no-launch`(업데이트만) |
| `service` | 무인 감시 루프: 주기 확인→앱 정지→업데이트→재실행 | `--config <file>` `--interval <초>` `--once`(1회 점검) |
| `rollback` | 백업으로 이전 버전 복원 | `--config <file>` `--list`(백업 목록) `--backup <타임스탬프>`(특정 백업 지정, 생략 시 최신) |
| `generate-manifest` | 패키지 폴더 → manifest.json | `--package-dir` `--base-url` `--entry-point` `--version` `--platform` `--app-id` `--output` |
| `update-catalog` | catalog.json에 릴리스 등록/제거 | `--catalog` `--project-id` `--version` `--environment` `--channel` `--platform` `--manifest-url` `--allowed-profiles` `--set-latest` `--remove` |
| `list-releases` | 카탈로그에 등록된 릴리스를 표로 출력(점검용) | `--catalog` `--project`(선택) |
| `generate-nginx-acl` | 프로젝트별 IP 허용목록 → nginx 설정 생성 | `--allowlist` `--output`(생략 시 stdout) |
| `sign-manifest` | manifest/catalog ECDSA 서명 생성 | `--manifest` `--private-key` `--output` |
| `sample-config` | 설정 템플릿 생성 | `--output` |

> Windows에서 런처는 GUI 앱으로 빌드되어 **더블클릭하면 검은 콘솔 창 없이 런처 창만** 뜹니다. CLI 명령을 cmd/PowerShell에서 실행하면 그 터미널에 출력이 보입니다.

예시:

```bash
# 업데이트만 하고 실행은 안 함 (배포 확인용)
UeDtLauncher run --config launcher.config.json --no-launch

# 파일 깨짐 의심 시 전체 검증/복구
UeDtLauncher run --config launcher.config.json --repair --no-launch

# 백업 목록 확인 후 특정 시점으로 롤백
UeDtLauncher rollback --config launcher.config.json --list
UeDtLauncher rollback --config launcher.config.json --backup 20260611075230
```

## 4. 무인 서버(픽셀 스트리밍) 운영

요약 (자세한 내용과 NSSM/작업 스케줄러 방법은 [service-mode.md](service-mode.md)):

```bash
# 수동 테스트: 1회 점검
./UeDtLauncher service --config launcher.config.json --once

# systemd 등록 (Linux)
sudo systemctl enable --now ue-dt-launcher.service
journalctl -u ue-dt-launcher.service -f      # 실시간 로그
```

동작 규칙: 설치 버전 ≠ 카탈로그 버전이면 앱을 정상 종료(안 되면 강제 종료)하고 업데이트 후 재실행합니다. 앱이 크래시로 죽어 있으면 다음 주기에 자동 재실행. 카탈로그가 구버전을 가리키면 다운그레이드도 따라갑니다(운영 롤백 배포 지원).

## 4-A. 리눅스 클라이언트/서버 실행

런처는 Windows뿐 아니라 **Linux에서도 1급(first-class)으로 GUI/CLI 모두** 동작합니다.

### GUI / CLI 모드를 명시적으로 고르기

| 실행 | 동작 |
| --- | --- |
| `./UeDtLauncher` (인자 없음) 또는 `./UeDtLauncher gui` | GUI 실행 |
| `./UeDtLauncher --gui` | GUI 강제 실행 (인자 위치 무관) |
| `./UeDtLauncher --cli --config launcher.config.json` | CLI 강제 실행. 내부적으로 `run`으로 매핑되어 1회 업데이트(+실행) |
| `./UeDtLauncher --cli service --config launcher.config.json` | `--cli` 뒤에 이미 알려진 서브커맨드(`run`/`service`/…)가 오면 그 명령을 그대로 사용 |
| `./UeDtLauncher run …` / `./UeDtLauncher service …` | 기존과 동일하게 그대로 동작 |

> `--gui`와 `--cli`를 **동시에** 주면 `--gui and --cli cannot be used together.` 를 출력하고 종료 코드 2로 끝납니다.

### GUI 실행 요건 (데스크톱 + CJK 폰트)

GUI는 **그래픽 데스크톱 환경(X11 또는 Wayland)** 이 있어야 뜹니다.

- 그래픽 디스플레이가 전혀 없는 경우(헤드리스 서버: `DISPLAY`/`WAYLAND_DISPLAY` 미설정), GUI 실행을 시도하면 안내 메시지를 출력하고 **종료 코드 1**로 끝납니다. 이때는 아래 CLI 모드를 쓰세요.
- 디스플레이는 있는데 GUI 초기화에 실패하면(폰트/디스플레이 서버 문제) 역시 안내 메시지를 출력하고 종료 코드 1로 끝납니다.
- 한글이 깨지지 않게 **CJK 폰트**를 설치하세요:

```bash
# Fedora/RHEL/Rocky
sudo dnf install -y google-noto-sans-cjk-fonts

# Debian/Ubuntu
sudo apt install -y fonts-noto-cjk
```

### 헤드리스 서버는 CLI/서비스 모드로

화면이 없는 서버(픽셀 스트리밍 등)에서는 GUI 대신 CLI를 사용합니다:

```bash
# 1회 업데이트(+실행)
./UeDtLauncher --cli --config launcher.config.json
# 또는 동일하게
./UeDtLauncher run --config launcher.config.json

# 무인 감시 루프
./UeDtLauncher service --config launcher.config.json
```

### 일반 프로필도 Linux에서 동작

이제 `clientProfile: "general"` 도 `targetPlatform: "linux-x64"` 를 사용할 수 있습니다(여전히 `prod` + `stable` + `latest` 고정). 단, **서버 catalog에 해당 프로젝트의 `linux-x64` 릴리스가 등록되어 있어야** 합니다(2편으로 linux-x64 패키지를 퍼블리시).

### Linux 바이너리 빌드

- 저장소 루트에서 `scripts/publish-linux-x64.sh` 실행
- 또는 Windows에서 크로스 컴파일 ([2편](guide-02-publish-package.md) 참고)

## 5. 문제 해결 (Troubleshooting)

| 증상 / 메시지 | 원인 | 해결 |
| --- | --- | --- |
| `Project was not found in catalog` | config의 `projectId`가 catalog에 없음 | catalog.json의 `projects[].projectId`와 일치시키기. 2편으로 릴리스가 올라갔는지 확인 |
| `No release in catalog matched` | 환경/채널/플랫폼/프로필 조합에 맞는 릴리스 없음 | 메시지에 **카탈로그 실제값 vs 요청값**과 힌트가 같이 표시됨(예: `platform (catalog 'windows-64' vs requested 'windows-x64')`). 그대로 보고 고치거나 `list-releases`로 등록 확인 |
| `이 네트워크(IP)에서는 접근이 허용되지 않은 프로젝트` / 403 | 서버의 프로젝트별 IP 제한에 막힘 | 허용된 네트워크에서 접속하거나 서버 관리자에게 IP 추가 요청(1편 9단계) |
| `General users are allowed to use only ...` | 일반 프로필로 dev/beta/exact 또는 허용되지 않은 플랫폼 요청 | 일반 PC는 prod+stable+latest 고정(플랫폼은 `windows-x64` 또는 `linux-x64` 허용). 개발 빌드가 필요하면 developer 프로필 + 서버 계정 사용 |
| `No graphical display detected ...` (종료 코드 1) | 헤드리스 Linux에서 GUI 실행 시도 | `--cli --config …` 또는 `run`/`service` 사용. 데스크톱이 있으면 CJK 폰트 설치(4-A절) |
| `WARNING: ... signature verification skipped` | 서명 미설정 (경고일 뿐 동작은 함) | 운영 PC라면 1편 6단계 키 배포 후 `requireSignedManifests: true` 설정 |
| `requireSignedManifests is enabled, but ...` | 서명 강제인데 서명 URL/공개키 미설정 | `catalogSignatureUrl`/`manifestSignatureUrl`과 공개키 경로 설정, 서버에 `.sig` 업로드 확인 |
| `Manifest signature verification failed` | 서명 불일치(변조 또는 catalog 갱신 후 재서명 누락) | 2편 6장처럼 catalog/manifest 재서명 |
| `Not enough free disk space` | 디스크 부족 (필요 용량+10% 여유 기준) | 디스크 확보 또는 `installDir`를 다른 드라이브로 |
| `Another launcher instance is already updating` | 같은 설치 폴더에 런처 2개 동시 실행 | 다른 런처/서비스 모드 종료 후 재시도 |
| `Download failed (not retryable)` + 404 | manifest의 파일 URL이 서버에 없음 | 릴리스 재퍼블리시(2편). 서버에서 해당 URL `curl -I`로 확인 |
| 401 Unauthorized | 개발자 경로 인증 실패 | catalog URL에 계정 포함(`http://user:pass@...`) 또는 서버 htpasswd 확인 |
| 업데이트가 중간에 끊김 | 네트워크 불안정 | 자동 이어받기/재시도 됨. 반복되면 `maxRetryCount`/`httpTimeoutSeconds` 상향 |
| 업데이트 후 앱이 이상함 | 배포 자체 문제 | `rollback`으로 즉시 복귀 → 서버에서 구버전을 `--set-latest`로 재지정(2편) |
| 원인을 모르겠을 때 | - | `logs/launcher-YYYYMMDD.log` 확인, GUI는 "로그 ZIP 저장" 후 관리자 전달 |

### 로그 위치

- 파일 로그: 런처 폴더의 `logs/launcher-YYYYMMDD.log` (일별, 14일 보관) — CLI/GUI/서비스 모드 공통
- GUI 화면 로그: 상태 패널 하단 (최근 500줄)
- systemd: `journalctl -u ue-dt-launcher.service`
