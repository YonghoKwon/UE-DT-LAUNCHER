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

도구가 이 과정을 자동화합니다. **방법 A(리눅스 서버에서 직접)** 또는 **방법 B(Windows PC에서 원격 업로드)** 중 환경에 맞는 것을 쓰면 되고, 내부 동작을 이해하고 싶으면 방법 C를 읽어보세요.

### 사전 준비물

| 항목 | 설명 |
| --- | --- |
| 패키징 결과 폴더 | 예: `C:\PackageBuilds\ue-dt-simulator\1.2.0\windows-x64` (UE가 만든 `Windows/` 폴더 구조 그대로) |
| 런처 실행 파일 | 도구들이 내부적으로 사용. 리눅스: `./scripts/publish-linux-x64.sh`로 빌드, Windows: `.\scripts\publish-win-x64.ps1` |
| 릴리스 정보 | projectId / 버전 / 환경(prod·dev) / 채널(stable·beta·dev) / 플랫폼 / 실행 진입점(entry point) |

> 리눅스에서 도구 스크립트는 런처 바이너리를 `publish/linux-x64/UeDtLauncher` → PATH 순으로 찾습니다.
> 다른 위치에 있으면 `export UE_DT_LAUNCHER_BIN=/path/to/UeDtLauncher`로 지정하세요.

---

## 방법 A — 리눅스 서버에서 직접 (권장: 서버에 빌드를 복사해 둔 경우)

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

## 업로드 후 확인 체크리스트

```bash
# 1. catalog에 새 버전이 보이는가 (isLatest: true 확인)
curl -s http://서버IP/catalogs/general/catalog.json | python3 -m json.tool | grep -A2 version

# 2. manifest가 받아지는가
curl -I http://서버IP/projects/ue-dt-simulator/prod/stable/1.2.0/windows-x64/manifest.json

# 3. 클라이언트 PC에서 실제 업데이트 (앱 실행 없이)
UeDtLauncher.exe run --config launcher.config.json --no-launch
```

3번에서 `[Complete] Update completed.`가 나오면 끝입니다. 클라이언트 쪽 명령과 문제 해결은 [3편](guide-03-launcher-usage.md)을 보세요.
