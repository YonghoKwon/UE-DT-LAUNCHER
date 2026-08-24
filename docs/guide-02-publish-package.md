# 가이드 2편 — 패키징 파일 업로드 (릴리스 퍼블리시)

> 언리얼 패키징이 끝난 폴더를 업데이트 서버에 "릴리스"로 올리는 모든 방법을 다룹니다.
> 서버 구축은 [1편](guide-01-linux-server-setup.md), 클라이언트 사용법은 [3편](guide-03-launcher-usage.md)을 보세요.

## 릴리스가 만들어지는 과정 (원리)

```text
패키징 폴더            manifest 생성              catalog 갱신                클라이언트
WindowsNoEditor/  →  파일별 SHA-256 목록     →  "이 프로젝트의 1.0.0이    →  catalog를 보고 버전 선택
  m7at10_dt.exe      manifest.json 생성         최신이다"라고 등록           manifest를 보고 변경 파일만 다운로드
  ...                                           catalog.json 갱신
```

도구가 이 과정을 자동화합니다. **로컬에서 패키징 → ZIP으로 압축 → 서버에 업로드 → 서버에서 등록**하는 가장 일반적인 운영 흐름은 **방법 D**(아래)를 보세요. 같은 흐름을 **메뉴로 안내**받고 싶으면 **방법 E(대화형 위저드)**가 가장 편합니다. 그 외에 서버에 빌드 폴더를 직접 둔 경우는 방법 A, Windows에서 원격 전송은 방법 B, 내부 동작 이해는 방법 C입니다.

> ⚠️ **ZIP은 "업로드(전송) 편의용"입니다.** 서버에서 압축을 풀어 `files/` 아래 **개별 파일**로 두면, 클라이언트는 manifest(파일별 SHA-256)를 보고 **바뀐 파일만 개별 다운로드**합니다 — ZIP을 통째로 받는 게 아닙니다. (클라이언트가 ZIP을 통째로 받아 푸는 건 별도 `packages` 기능이며 이 문서 범위가 아닙니다.)

### 사전 준비물

| 항목 | 설명 |
| --- | --- |
| 패키징 결과 폴더 | 예: `C:\PackageBuilds\ue-dt-simulator\1.2.0\windows-x64` (UE가 만든 `Windows/` 폴더 구조 그대로) |
| 런처 실행 파일 | 도구들이 내부적으로 사용. 리눅스: `./scripts/publish-linux-x64.sh`로 빌드, Windows: `.\scripts\publish-win-x64.ps1` |
| 릴리스 정보 | projectId / 버전 / 환경(prod·dev) / 채널(stable·beta·dev) / 플랫폼 / 실행 진입점(entry point) |

> 리눅스에서 도구 스크립트는 런처 바이너리를 `publish/linux-x64/UeDtLauncher` → PATH 순으로 찾습니다.
> 다른 위치에 있으면 `export UE_DT_LAUNCHER_BIN=/path/to/UeDtLauncher`로 지정하세요.

---

## 방법 D — 로컬 패키징 → ZIP 업로드 → 서버에서 등록 (가장 일반적인 운영 흐름)

> 윈도우에서 UE 패키징 → 결과 폴더를 ZIP으로 압축 → 리눅스 서버에 업로드 → 서버에서 **압축을 풀고** manifest/catalog 생성.
> 다시 강조: ZIP은 전송용일 뿐, 서버에서 풀어 `files/` 아래 개별 파일로 둡니다. 클라이언트는 그 개별 파일을 차등 다운로드합니다.

### D-0. 변수 정의 (서버에서)

이후 명령에 그대로 쓸 값들을 먼저 잡아두면 편합니다.

```bash
SERVER_ROOT=/dt/ue-dt-updates       # 회사 서버 기준 (가이드 1편 기본 예시는 /srv/ue-dt-updates)
SERVER_URL=http://<서버IP>          # 클라이언트가 접속할 주소 (예: http://10.10.20.5)
PROJ=ue-dt-simulator                # projectId
VER=1.0.0                           # 버전
ENV=prod                            # 운영=prod, 개발/테스트=dev
CH=stable                           # 운영=stable, 개발=dev 또는 beta
PLAT=windows-x64                    # 또는 linux-x64
DEST=$SERVER_ROOT/projects/$PROJ/$ENV/$CH/$VER/$PLAT
```

### D-1. (윈도우) 패키징 결과를 ZIP으로 압축

UE 패키징 결과 폴더를 압축합니다. **압축 최상위에 들어가는 구조가 곧 서버 `files/` 바로 아래 구조**가 되고, 실행 파일이 압축 안에서 갖는 상대경로가 `--entry-point` 값이 됩니다.

```powershell
# 폴더 "내용물"을 담는 예 → files/ 바로 아래 M7AT10_DT.exe, Engine/ ...
Compress-Archive -Path "C:\PackageBuilds\ue-dt-simulator\1.0.0\Windows\*" `
                 -DestinationPath "C:\PackageBuilds\ue-dt-simulator-1.0.0.zip"
```

- `...\Windows\*`(내용물)을 담으면 → entry-point = `M7AT10_DT.exe`
- `...\Windows`(폴더째)를 담으면 → `files/Windows/M7AT10_DT.exe` → entry-point = `Windows/M7AT10_DT.exe`
- 둘 중 무엇이든 OK. **entry-point만 실제 경로에 맞추면** 됩니다 (D-3에서 확인).

### D-2. 서버로 업로드

```bash
# 내부망 SSH 가능
scp ue-dt-simulator-1.0.0.zip dt2prod@<서버IP>:/dt/incoming/
# 완전 폐쇄망이면 USB로 /dt/incoming/ 에 복사
```

### D-3. (서버) 압축 해제 → `files/`에 배치

```bash
mkdir -p "$DEST/files"
unzip -o /dt/incoming/ue-dt-simulator-1.0.0.zip -d "$DEST/files"

# 실제 실행파일 상대경로 확인 → entry-point 값 결정
cd "$DEST/files" && find . -name "*.exe" | head
```

> `unzip`이 없으면(폐쇄망) `unzip` RPM을 반입해 설치하거나, 윈도우에서 미리 풀어 폴더째 업로드해도 됩니다.

배치 후 서버 디렉터리 구조 (windows-x64 / 1.0.0 예):

```text
/dt/ue-dt-updates/
├── catalogs/
│   ├── general/catalog.json          # D-5에서 생성·갱신 (일반 사용자)
│   └── developer/catalog.json        # 개발 빌드용 (인증)
└── projects/
    └── ue-dt-simulator/              # PROJ
        └── prod/                     # ENV
            └── stable/               # CH
                └── 1.0.0/            # VER
                    └── windows-x64/  # PLAT
                        ├── manifest.json   # D-4에서 생성
                        └── files/          # ← ZIP 푼 내용 (클라이언트가 받는 실제 파일)
                            ├── M7AT10_DT.exe
                            ├── M7AT10_DT/
                            └── Engine/
```

### D-4. (서버) manifest 생성

서버에 `UeDtLauncher` 리눅스 바이너리가 있어야 합니다(self-contained, .NET 설치 불필요). 예: `/dt/tools/UeDtLauncher`.

```bash
export UE_DT_LAUNCHER_BIN=/dt/tools/UeDtLauncher
/dt/tools/generate-manifest.sh \
  --package-dir "$DEST/files" \
  --base-url    "$SERVER_URL/projects/$PROJ/$ENV/$CH/$VER/$PLAT/files" \
  --entry-point "M7AT10_DT.exe" \
  --version "$VER" --channel "$CH" --platform "$PLAT" --app-id "$PROJ" \
  --output "$DEST/manifest.json"
```

- `--base-url` = **`files`까지 포함한 클라이언트 접속 URL**. manifest 안 각 파일 URL이 이걸 기준으로 만들어집니다.
- `--entry-point` = D-3에서 확인한 실제 상대경로.

> 서버에 바이너리를 못 두는 경우 → 윈도우에서 `UeDtLauncher.exe generate-manifest ...`로 manifest를 만들어 ZIP과 함께 업로드해도 됩니다(SHA-256은 OS 무관 동일). 단 `--base-url`은 반드시 서버 URL로 지정.

### D-5. (서버) catalog 갱신

```bash
/dt/tools/update-catalog.sh \
  --catalog "$SERVER_ROOT/catalogs/general/catalog.json" \
  --project-id "$PROJ" --display-name "UE-DT Simulator" \
  --version "$VER" --environment "$ENV" --channel "$CH" --platform "$PLAT" \
  --manifest-url "$SERVER_URL/projects/$PROJ/$ENV/$CH/$VER/$PLAT/manifest.json" \
  --allowed-profiles general,developer \
  --set-latest
```

- 운영 배포 → `catalogs/general/`. 개발 빌드면 `catalogs/developer/` + `--allowed-profiles developer`.

### D-6. (서버) SELinux 컨텍스트 재적용 + 정리

```bash
sudo restorecon -Rv "$SERVER_ROOT"               # 새로 푼 파일에 httpd 라벨 적용 (안 하면 403)
rm -f /dt/incoming/ue-dt-simulator-1.0.0.zip      # 업로드용 zip은 이제 불필요
```

### D-7. 서버에서 동작 확인

```bash
curl -s "$SERVER_URL/catalogs/general/catalog.json" | head
curl -I "$SERVER_URL/projects/$PROJ/$ENV/$CH/$VER/$PLAT/manifest.json"          # 200
curl -I "$SERVER_URL/projects/$PROJ/$ENV/$CH/$VER/$PLAT/files/M7AT10_DT.exe"    # 200
```

### D-8. (클라이언트 PC) 다운로드 + 실행

클라이언트 폴더에 `UeDtLauncher.exe` + `launcher.config.json`을 둡니다.

```json
{
  "catalogUrl": "http://<서버IP>/catalogs/general/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod", "channel": "stable", "versionPolicy": "latest",
  "targetPlatform": "windows-x64",
  "installDir": "app"
}
```

```powershell
.\UeDtLauncher.exe run --config launcher.config.json --no-launch   # 업데이트만 (검증용)
.\UeDtLauncher.exe run --config launcher.config.json               # 업데이트 + 실행
.\UeDtLauncher.exe                                                 # GUI로 실행
```

→ 클라이언트는 manifest를 보고 **바뀐 파일만** `app/`로 받은 뒤 `entryPoint`(M7AT10_DT.exe)를 실행합니다. 다음 버전(예 1.1.0)을 D-1~D-6으로 올리면, 재실행 시 **달라진 파일만** 받습니다.

---

## 방법 E — 대화형 위저드 (`publish-wizard.sh`)

> 방법 D를 **메뉴로 안내**합니다. 운영자가 서버에 SSH로 접속해 zip 하나만 올려두면, 위저드가 **기존 디렉터리 구조를 스캔**해 프로젝트/환경/채널/버전/플랫폼을 **번호 선택**으로 고르게 합니다. `windows-64` 같은 오타가 원천 차단되고, 압축 해제 → manifest 생성 → catalog 갱신까지 한 번에 끝납니다(내부적으로 `publish-release.sh --no-copy`에 위임).

### E-0. 사전 준비물

| 항목 | 설명 |
| --- | --- |
| 업로드된 zip | 패키징 결과를 압축해 `INCOMING_DIR`(기본 `$SERVER_ROOT/incoming`)에 업로드 |
| 런처 바이너리 | 서버에 `UeDtLauncher` 리눅스 바이너리. `publish/linux-x64/UeDtLauncher` → PATH 순으로 탐색, 또는 `export UE_DT_LAUNCHER_BIN=/dt/tools/UeDtLauncher` |
| `unzip` | 서버에 설치되어 있어야 함(없으면 명확한 오류로 중단) |

설정값(`SERVER_ROOT`/`BASE_URL_ROOT`/`INCOMING_DIR`)은 **환경변수 → `tools/publish.env` → 기본값을 보여주는 프롬프트** 순으로 해결됩니다. 처음 실행 시 입력한 값을 `tools/publish.env`에 저장해 다음부터 묻지 않게 할 수 있습니다(템플릿: `tools/publish.env.example`).

### E-1. 실행

```bash
# zip 경로를 직접 넘기거나(권장), 생략하면 INCOMING_DIR/현재 폴더의 zip 목록에서 선택
./tools/publish-wizard.sh /dt/incoming/m7at10-dt-1.0.0.zip
```

### E-2. 예시 세션

```text
$ ./tools/publish-wizard.sh /dt/incoming/m7at10-dt-1.0.0.zip
업로드(incoming) 디렉터리(INCOMING_DIR) [/srv/ue-dt-updates/incoming]:
이 설정을 .../tools/publish.env 에 저장할까요? [Y/n]: n
ZIP: /dt/incoming/m7at10-dt-1.0.0.zip
프로젝트를 선택하세요:
  1) ue-dt-simulator
  2) [새 프로젝트 입력]
선택 (1-2): 2
새 projectId (소문자/숫자/하이픈, 공백·슬래시 금지): m7at10-dt
표시 이름(displayName) [m7at10-dt]:
환경(environment)을 선택하세요 (✓ = 이미 존재):
  1) prod
  2) dev
선택 (1-2): 1
채널(channel)을 선택하세요 (✓ = 이미 존재):
  1) stable
  2) beta
  3) dev
선택 (1-3): 1
기존 버전 없음 (m7at10-dt/prod/stable)
새 버전(version): 1.0.0
플랫폼(platform)을 선택하세요 (✓ = 이미 존재):
  1) windows-x64
  2) linux-x64
선택 (1-2): 2
압축 해제: /dt/incoming/m7at10-dt-1.0.0.zip -> .../1.0.0/linux-x64/files
실행 진입점(entry-point)을 선택하세요 (files/ 기준 상대경로):
  1) Linux/m7at10_dt.sh
  2) [직접 입력]
선택 (1-2): 1
카탈로그 프로필(catalog-profile)을 선택하세요:
  1) general
  2) developer
선택 (1-2): 1
받을 수 있는 클라이언트 프로필(allowed-profiles, 쉼표 구분) [general]: general,developer
이 버전을 같은 트랙의 '최신(latest)'으로 표시할까요? [Y/n]:
릴리스 노트(notes, 비워도 됨):

================ 요약 ================
  ZIP             : /dt/incoming/m7at10-dt-1.0.0.zip
  projectId       : m7at10-dt
  displayName     : m7at10-dt
  environment     : prod
  channel         : stable
  version         : 1.0.0
  platform        : linux-x64
  entry-point     : Linux/m7at10_dt.sh
  catalog-profile : general
  allowed-profiles: general,developer
  set-latest      : yes
  notes           : (없음)
  server-root     : /srv/ue-dt-updates
  base-url-root   : http://10.10.20.5
  dest            : .../1.0.0/linux-x64/files
======================================
위 내용으로 퍼블리시할까요? [y/N]: y
Manifest generated: .../1.0.0/linux-x64/manifest.json
Catalog updated: .../catalogs/general/catalog.json
Release published ...

등록된 릴리스 (m7at10-dt):
m7at10-dt  (m7at10-dt)  — 1 release(s)
  1.0.0          prod /stable /linux-x64    profiles=[general,developer] [latest]
```

- 이미 존재하는 환경/채널/플랫폼은 메뉴에 ` ✓`로 표시됩니다. 같은 `version/platform`이 이미 있으면 `덮어쓸까요? [y/N]`로 확인합니다.
- 마지막에 `restorecon`이 있으면 `sudo restorecon -Rv "$SERVER_ROOT"`를 시도합니다(실패해도 경고만, 중단 안 함). SELinux를 쓰는 서버라면 403 방지를 위해 권장됩니다.
- 모든 프롬프트는 stdin에서 읽으므로, 답을 파이프로 넣어 자동화/테스트할 수 있습니다.

---

## 방법 A — 리눅스 서버에서 직접 (권장: 서버에 빌드를 복사해 둔 경우)

운영 publish 전에 개인키와 key ID를 세션 환경변수로 지정합니다. 개인키는 저장소나 클라이언트에 복사하지 않습니다.

```bash
export UE_DT_SIGNING_PRIVATE_KEY=/secure/manifest-private-key.pem
export UE_DT_SIGNING_KEY_ID=prod-2026
```

`publish-release`는 release 임시 구성·manifest 서명·hash 재검증·immutable release 전환·catalog 서명·catalog 최종 교체를 하나의 transaction으로 수행합니다. 기존 version을 바꾸려면 명시적으로 `--replace`가 필요하며 운영에서는 unsigned publish가 거부됩니다.

서버에 패키징 폴더를 옮겨 놨다면(scp, USB 등) 명령 한 번이면 끝납니다.

### A-1. 운영(가동) 정식 릴리스

```bash
./tools/publish-release.sh \
  --package-dir   /home/builds/ue-dt-simulator/1.2.0/windows-x64 \
  --server-root   /srv/ue-dt-updates \
  --base-url-root http://updates.example.com \
  --project-id    ue-dt-simulator \
  --display-name  "UE-DT Simulator" \
  --version       1.2.0 \
  --environment   prod \
  --channel       stable \
  --platform      windows-x64 \
  --entry-point   Windows/m7at10_dt.exe \
  --catalog-profile  general \
  --allowed-profiles general,developer \
  --notes         "1.2.0 정식 배포" \
  --set-latest
```

각 인자의 의미:

| 인자 | 의미 |
| --- | --- |
| `--package-dir` | 패키징 결과 폴더. 이 안의 모든 파일이 릴리스에 포함됩니다 |
| `--server-root` | 1편에서 만든 서버 루트 (`/srv/ue-dt-updates`) |
| `--base-url-root` | 클라이언트가 접속할 주소의 시작 부분. **catalog/manifest 안의 URL이 이 값으로 만들어지므로 반드시 클라이언트 입장의 주소**를 쓰세요 (`http://서버IP` 또는 도메인) |
| `--environment` / `--channel` | `prod stable`=운영 정식, `dev dev`=개발 테스트. 서버 폴더 경로와 접근 권한이 이 값으로 갈립니다 |
| `--entry-point` | 패키지 폴더 기준 실행 파일 상대 경로 |
| `--catalog-profile` | 갱신할 카탈로그: `general`(일반 공개) 또는 `developer` |
| `--allowed-profiles` | 이 릴리스를 받을 수 있는 클라이언트 프로필 목록 |
| `--set-latest` | 이 버전을 같은 트랙(환경/채널/플랫폼)의 "최신"으로 표시. 기존 최신 표시는 자동 해제됩니다 |

실행하면: 파일 복사 → `manifest.json` 생성 → `catalog.json` 갱신까지 자동으로 끝납니다.

실제 변경 없이 검증만 하려면 동일 명령 끝에 `--dry-run`을 추가합니다.

### A-2. 개발/테스트 빌드

```bash
./tools/publish-release.sh \
  --package-dir   /home/builds/ue-dt-simulator/1.3.0-dev.1/linux-x64 \
  --server-root   /srv/ue-dt-updates \
  --base-url-root http://updates.example.com \
  --project-id    ue-dt-simulator \
  --display-name  "UE-DT Simulator" \
  --version       1.3.0-dev.1 \
  --environment   dev \
  --channel       dev \
  --platform      linux-x64 \
  --entry-point   Linux/m7at10_dt.sh \
  --catalog-profile  developer \
  --allowed-profiles developer \
  --notes         "센서 연동 테스트 빌드" \
  --set-latest
```

`--catalog-profile developer` + `--allowed-profiles developer`이므로 일반 사용자에게는 보이지도, 받아지지도 않습니다 (서버 인증 + 카탈로그 분리 + 프로필 필터 3중 차단).

---

## 방법 B — Windows PC에서 리눅스 서버로 원격 업로드

패키징 PC(Windows)에서 바로 서버로 쏘는 방법입니다. PowerShell 스크립트에 `-Remote` 옵션을 쓰면 로컬 스테이징 폴더에 릴리스를 구성한 뒤 rsync(또는 scp)로 서버에 올립니다.

사전 준비: Windows에 OpenSSH 클라이언트(Win10+ 기본 포함)와, 가능하면 Git for Windows의 rsync. 서버 계정으로 ssh 접속이 되는지 먼저 확인하세요 (`ssh deploy@서버IP`).

```powershell
.\tools\publish-release.ps1 `
  -PackageDir  "C:\PackageBuilds\ue-dt-simulator\1.2.0\windows-x64" `
  -ServerRoot  "C:\ue-dt-staging" `
  -BaseUrlRoot "http://updates.example.com" `
  -ProjectId   "ue-dt-simulator" `
  -DisplayName "UE-DT Simulator" `
  -Version     "1.2.0" `
  -Environment prod -Channel stable -Platform windows-x64 `
  -EntryPoint  "Windows/m7at10_dt.exe" `
  -CatalogProfile general `
  -AllowedClientProfiles general,developer `
  -Notes "1.2.0 정식 배포" `
  -SetLatest `
  -Remote "deploy@updates.example.com:/srv/ue-dt-updates"
```

- `-ServerRoot`는 **로컬 스테이징 폴더**입니다. 여기에 서버와 똑같은 구조가 만들어진 뒤 `-Remote`로 전송됩니다. 스테이징 폴더를 지우지 말고 유지하면 다음 릴리스 때 rsync가 변경분만 전송합니다.
- 주의: 이 방식은 스테이징 폴더의 catalog.json을 서버로 덮어씁니다. **릴리스를 올리는 관리 PC를 한 대로 정해서** 카탈로그가 서로 덮어쓰지 않게 하세요. 여러 PC에서 올려야 하면 방법 A(서버에서 직접 catalog 갱신)를 쓰는 것이 안전합니다.
- 리눅스/맥에서 원격 업로드를 하려면 `tools/publish-release.sh ... --remote deploy@server:/srv/ue-dt-updates` 형태로 동일하게 동작합니다.

---

## 방법 C — 수동 단계별 (내부 동작 이해용)

publish-release가 하는 일을 손으로 하면 다음 3단계입니다.

```bash
# 1) 파일 복사: 서버 규칙 경로에 패키지 넣기
mkdir -p /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/files
cp -a /home/builds/.../windows-x64/. /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/files/

# 2) manifest 생성: 파일별 SHA-256 목록
./tools/generate-manifest.sh \
  --package-dir /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/files \
  --base-url    http://updates.example.com/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/files \
  --entry-point Windows/m7at10_dt.exe \
  --version 1.2.0 --channel stable --platform windows-x64 \
  --app-id ue-dt-simulator \
  --output /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/manifest.json

# 3) catalog 갱신: "이 버전이 최신"이라고 등록
./tools/update-catalog.sh \
  --catalog /srv/ue-dt-updates/catalogs/general/catalog.json \
  --project-id ue-dt-simulator --display-name "UE-DT Simulator" \
  --version 1.2.0 --environment prod --channel stable --platform windows-x64 \
  --manifest-url http://updates.example.com/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/manifest.json \
  --allowed-profiles general,developer \
  --set-latest
```

> `.sh` 스크립트는 런처 CLI(`generate-manifest`, `update-catalog` 명령)의 얇은 래퍼입니다. Windows에서는 `UeDtLauncher.exe generate-manifest ...` 처럼 런처를 직접 실행해도 똑같습니다.

## 자주 쓰는 시나리오 모음

**특정 구버전을 다시 최신으로 (운영 롤백 배포)** — 파일은 이미 서버에 있으므로 catalog만 바꿉니다:

```bash
./tools/update-catalog.sh --catalog /srv/ue-dt-updates/catalogs/general/catalog.json \
  --project-id ue-dt-simulator --version 1.1.0 --environment prod --channel stable --platform windows-x64 \
  --manifest-url http://updates.example.com/projects/ue-dt-simulator/prod/stable/1.1.0/windows-x64/manifest.json \
  --allowed-profiles general,developer --set-latest
```

클라이언트는 다음 실행/서비스 주기에 1.1.0으로 (다운그레이드 포함) 맞춰집니다.

**릴리스 제거**:

```bash
./tools/update-catalog.sh --catalog /srv/ue-dt-updates/catalogs/developer/catalog.json \
  --project-id ue-dt-simulator --version 1.3.0-dev.1 \
  --environment dev --channel dev --platform linux-x64 \
  --manifest-url unused --remove --remove-project-if-empty
# 파일까지 지우려면:
rm -rf /srv/ue-dt-updates/projects/ue-dt-simulator/dev/dev/1.3.0-dev.1
```

**같은 빌드를 일반+개발자 카탈로그 양쪽에 등록**: `--catalog-profile general`로 한 번, `--no-copy`를 붙여 `--catalog-profile developer`로 한 번 더 실행 (파일 복사와 manifest 생성은 한 번만 됩니다).

## 서명 적용 릴리스 (서명을 켠 경우)

1편 6단계에서 만든 개인키로 manifest와 catalog에 서명을 만듭니다:

```bash
L=publish/linux-x64/UeDtLauncher   # 또는 UeDtLauncher.exe
$L sign-manifest --manifest /srv/ue-dt-updates/projects/.../manifest.json \
   --private-key manifest-private-key.pem \
   --output     /srv/ue-dt-updates/projects/.../manifest.json.sig

$L sign-manifest --manifest /srv/ue-dt-updates/catalogs/general/catalog.json \
   --private-key manifest-private-key.pem \
   --output     /srv/ue-dt-updates/catalogs/general/catalog.json.sig
```

> 중요: **catalog를 갱신할 때마다 catalog 서명도 다시** 만들어야 합니다. catalog에 릴리스를 등록할 때 `--manifest-signature-url`로 manifest 서명 URL도 함께 등록하세요.

## ⚠️ platform 값은 정확히 (흔한 실수)

`--platform`은 `windows-x64` 또는 `linux-x64`로 **정확히** 써야 합니다. `windows`, `windows-64`, `win-x64` 같은 변형을 쓰면 클라이언트가 릴리스를 못 찾습니다(`environment`/`channel`도 동일). 이제 도구가 잘못된 값을 **즉시 거부**합니다:
```text
ERROR: Invalid --platform 'windows-64'. Allowed: windows-x64, linux-x64.
```

## 업로드 후 확인 체크리스트

```bash
# 1. 등록된 릴리스를 표로 확인 (가장 확실)
/dt/tools/UeDtLauncher list-releases --catalog /dt/ue-dt-updates/catalogs/general/catalog.json
#   → m7at10-dt ... 0.0.1  prod /stable /windows-x64  profiles=[general,developer] [latest]

# 2. manifest가 받아지는가
curl -I http://서버IP/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/manifest.json

# 3. 클라이언트 PC에서 실제 업데이트 (앱 실행 없이)
UeDtLauncher.exe run --config launcher.config.json --no-launch
```

3번에서 `[Complete] Update completed.`가 나오면 끝입니다. 혹시 "No release in catalog matched"가 나오면 메시지에 **카탈로그의 실제 값 vs 요청 값**이 같이 표시되므로(예: `platform (catalog 'windows-64' vs requested 'windows-x64')`) 그대로 보고 고치면 됩니다. 클라이언트 쪽 명령과 문제 해결은 [3편](guide-03-launcher-usage.md)을 보세요.
