# UE-DT-LAUNCHER

UE-DT-LAUNCHER는 Unreal Engine 패키징 결과물을 Windows/Linux PC에 배포하고, 실행 전에 최신 파일로 자동 업데이트한 뒤 앱을 실행하기 위한 경량 런처입니다.

이 브랜치는 Netmarble Launcher 분석 결과를 바탕으로, PC 배포에 필요한 핵심 기능과 여러 프로젝트/버전/OS/환경/클라이언트 프로필을 관리하는 release catalog 기능을 포함합니다.

## 먼저 알아둘 점

GitHub 저장소에는 `UeDtLauncher.exe` 실행 파일을 직접 커밋하지 않습니다. 저장소에는 소스 코드만 들어있고, 실행 파일은 로컬 PC 또는 GitHub Actions에서 `dotnet publish`로 생성해야 합니다.

Windows에서 바로 실행 파일을 만들려면:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\publish-win-x64.ps1
```

생성 후 실행 파일 위치:

```text
publish\win-x64\UeDtLauncher.exe
```

GUI 실행:

```powershell
.\publish\win-x64\UeDtLauncher.exe
```

CLI 실행:

```powershell
.\publish\win-x64\UeDtLauncher.exe run --config launcher.config.json
```

Linux에서 실행 파일을 만들려면:

```bash
chmod +x ./scripts/publish-linux-x64.sh
./scripts/publish-linux-x64.sh
```

생성 후 실행 파일 위치:

```text
publish/linux-x64/UeDtLauncher
```

## 현재 구현된 기능

- Avalonia 기반 Windows/Linux GUI 런처
- CLI 런처 유지
- release catalog 기반 프로젝트/버전/OS/환경/클라이언트 프로필 선택
- 일반 사용자 프로필 제한: `windows-x64` + `prod` + `stable` + `latest`만 허용
- 개발자 프로필: catalog 권한에 따라 Windows/Linux 개발 버전 선택 가능
- 일반 사용자용 설정 팝업
- 개발자용 환경/채널/플랫폼/버전 정책 ComboBox
- 프로젝트 검색, 고정, 정렬, 프로필별 표시 제어
- 설치됨/업데이트 가능/오류 상태 확인
- 업데이트 실패 시 다시 시도 버튼
- 로그 저장 및 로그 지우기
- 캐시/백업 용량 표시
- GitHub Actions 기반 Windows/Linux 빌드 검증 워크플로
- 원격 `manifest.json` 다운로드
- catalog/manifest ECDSA SHA-256 서명 검증 옵션
- 파일별 SHA-256 비교
- 변경/누락 파일만 다운로드
- `.staging` 다운로드 후 검증
- `.state/{projectId}/{platform}` 기반 프로젝트별 캐시·백업·설치 상태·PID 격리
- `.backup` 백업 후 실제 설치 폴더 반영
- 적용 실패 시 rollback
- 중단된 적용 transaction을 다음 실행에서 자동 감지·롤백
- HTTP Range 기반 이어받기 시도
- 다운로드 retry
- repair 모드
- ZIP 패키지 다운로드/압축 해제
- 7z 패키지 다운로드/압축 해제. 단, `7z`, `7zz`, `7za` 실행 파일이 PATH에 있어야 함
- 런처 자기 자신 업데이트 (`selfUpdate.autoApply` 활성 시 다음 실행에서 자동 교체)
- Windows 바탕화면/시작 메뉴 shortcut 생성 옵션
- manifest 생성 명령 (`--app-id` 지원)
- catalog 릴리스 등록/제거 명령 (`update-catalog`)
- catalog/manifest 서명 생성 명령
- sample config 생성 명령
- `requireSignedManifests` 서명 강제 옵션 (서명 미설정 시 명시적 경고 로그)
- 디스크 여유 공간 사전 확인
- 동일 설치 폴더 다중 실행 잠금
- 일시 오류만 지수 백오프로 재시도 (404/403 등 4xx는 즉시 실패)
- ZIP/7z 추출 경로 검증 및 설치 폴더 내 symlink 차단
- 일별 파일 로그 (`logs/launcher-YYYYMMDD.log`, 14일 보관)
- 설치 버전 기록 (`install-state.json`) 및 백업 보존 개수 관리 (`maxBackupCount`)
- 이전 버전 롤백: CLI `rollback` 명령 + GUI 롤백 버튼
- 무인 서버용 서비스 모드: `service` 명령 (주기 확인 → 앱 정지 → 업데이트 → 재실행)
- GUI: 설치/최신 버전 나란히 표시, 다운로드 속도·파일 n/m·전체 % 진행률, 작업 중 버튼 비활성화
- 리눅스 퍼블리싱 도구: `tools/*.sh` + rsync/scp 원격 업로드 (`--remote`)
- xUnit 테스트 스위트 + CI 테스트 실행

## GUI 사용법

### 일반 사용자 모드

`launcher.config.json`의 `clientProfile`이 `general`이면 일반 사용자용 화면으로 표시됩니다.

일반 사용자에게는 다음 기능만 노출됩니다.

```text
프로젝트 검색
프로젝트 선택
실행
상태 확인
설정 팝업
설치 폴더 열기
문제 보고용 로그 저장
```

일반 사용자 설정 팝업에서는 프로젝트명, 설치 위치, 배포 채널, 설치 상태, 캐시/백업 용량을 확인할 수 있습니다.

### 개발자 모드

`launcher.config.json`의 `clientProfile`이 `developer`이면 개발자용 화면으로 표시됩니다.

개발자 화면에서는 다음 기능이 추가됩니다.

```text
환경 ComboBox: prod / dev
채널 ComboBox: stable / beta / dev
플랫폼 ComboBox: windows-x64 / linux-x64
버전 정책 ComboBox: latest / exact
업데이트
검증/복구
캐시 정리
로그 저장
로그 지우기
다시 시도
폴더 크기 새로고침
```

개발자 화면에서 ComboBox를 변경하면 현재 UI 설정에 반영되고, 실행/업데이트/상태 확인 시 변경된 값으로 catalog release를 선택합니다.

## 문서

처음이라면 **[docs/README.md](docs/README.md)**(문서 색인 + 5분 빠른 시작)부터 보세요. 핵심은 아래 **가이드 3부작**입니다 — 서버 구축 → 릴리스 배포 → 클라이언트 운영 순서.

| 가이드 | 내용 |
| --- | --- |
| [docs/guide-01-linux-server-setup.md](docs/guide-01-linux-server-setup.md) | 리눅스(RHEL 8.4) 업데이트 서버 세팅: 디렉터리, nginx, 인증, SELinux, 프로젝트별 IP 제한, 동작 확인 |
| [docs/guide-02-publish-package.md](docs/guide-02-publish-package.md) | 패키징 파일 업로드(ZIP) → manifest 생성 → catalog 갱신, 시나리오별 예시, 확인 |
| [docs/guide-03-launcher-usage.md](docs/guide-03-launcher-usage.md) | 런처 사용법: 설정 전체 필드, GUI(일반/개발자), CLI 레퍼런스, 무인 서버, 문제 해결 |

루트의 나머지 핵심: `docs/launcher-user-guide.md`(GUI 화면 사용법), `docs/launcher-ui-customization.md`(UI 커스터마이징), `docs/service-mode.md`(무인 서비스 모드).

덜 중요한 보조·레거시 문서는 [docs/reference/](docs/reference/)로 분리했습니다(퍼블리시 스크립트 상세, 구 서버 구성 문서들 — 서버 구성 정본은 guide-01).

예시 파일:

```text
examples/catalogs/general/catalog.json
examples/catalogs/developer/catalog.json
examples/catalogs/developer/m7at10-catalog.json
examples/configs/general-windows-launcher.config.json
examples/configs/developer-windows-launcher.config.json
examples/configs/developer-linux-launcher.config.json
examples/configs/m7at10-developer-windows-launcher.config.json
```

## 저장소 구조

```text
UE-DT-LAUNCHER/
  scripts/
    publish-win-x64.ps1
    publish-linux-x64.sh
  examples/
    catalogs/
    configs/
  src/
    UeDtLauncher/
      UeDtLauncher.csproj
      Program.cs
      CatalogResolver.cs
      LauncherEngine.cs
      ManifestGenerator.cs
      ManifestSignatureVerifier.cs
      PackageExtractor.cs
      SelfUpdateManager.cs
      WindowsIntegration.cs
      Gui/
        App.axaml
        App.axaml.cs
        MainWindow.axaml
        MainWindow.axaml.cs
  docs/
    README.md                       # 색인 + 5분 빠른 시작
    guide-01-linux-server-setup.md
    guide-02-publish-package.md
    guide-03-launcher-usage.md
    launcher-user-guide.md
    launcher-ui-customization.md
    service-mode.md
    reference/                      # 보조·레거시 문서
```

## release catalog 방식

기존에는 클라이언트가 `manifestUrl` 하나만 바라봤습니다. 이제는 다음 구조를 권장합니다.

```text
launcher.config.json
  ↓
catalogUrl
  ↓
projectId + clientProfile + environment + channel + targetPlatform + versionPolicy 기준 release 선택
  ↓
선택된 release의 manifestUrl 다운로드
  ↓
업데이트/실행
```

일반 사용자 PC 예시:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/general/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/general/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "targetPlatform": "windows-x64"
}
```

개발자 Windows PC 예시:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/developer/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/developer/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "targetPlatform": "windows-x64"
}
```

개발자 Linux PC 예시:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/developer/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/developer/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "targetPlatform": "linux-x64"
}
```

## 프로젝트 UI 설정

`projects` 배열로 프로젝트 목록, 이미지, 정렬, 프로필별 표시를 제어합니다.

```json
{
  "projectAssetsDir": "assets/projects",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator",
      "description": "센서/디지털 트윈 개발 검증용 빌드입니다.",
      "thumbnailPath": "assets/projects/ue-dt-simulator/thumbnail.png",
      "heroPath": "assets/projects/ue-dt-simulator/hero.png",
      "status": "최신 버전",
      "installPath": "app",
      "engineVersion": "Unreal 5.4",
      "technology": "Windows",
      "sortOrder": 0,
      "isPinned": true,
      "visibleToProfiles": ["general", "developer"]
    }
  ]
}
```

정렬 기준:

```text
1. isPinned = true 먼저
2. sortOrder 낮은 순
3. displayName 이름순
```

## 보안상 중요한 점

일반 사용자가 개발 버전을 못 받게 하려면 런처 코드만 믿으면 안 됩니다.

반드시 서버에서도 다음처럼 나눠야 합니다.

```text
/catalogs/general/        공개
/projects/*/prod/stable/ 공개
/catalogs/developer/      인증 필요
/projects/*/dev/          인증 필요
```

자세한 Nginx 설정(프로젝트별 IP 제한 포함)은 `docs/guide-01-linux-server-setup.md`에 있습니다.

## 빌드 방법

.NET 8 SDK가 필요합니다.

```powershell
dotnet build src/UeDtLauncher/UeDtLauncher.csproj -c Release
```

Windows publish:

```powershell
.\scripts\publish-win-x64.ps1
```

Linux publish:

```bash
./scripts/publish-linux-x64.sh
```

## 실행 방법

Windows GUI:

```powershell
.\publish\win-x64\UeDtLauncher.exe
```

Windows CLI:

```powershell
.\publish\win-x64\UeDtLauncher.exe run --config launcher.config.json
```

Linux GUI:

```bash
./publish/linux-x64/UeDtLauncher
```

Linux CLI:

```bash
./publish/linux-x64/UeDtLauncher run --config launcher.config.json
```

복구 모드:

```powershell
.\publish\win-x64\UeDtLauncher.exe run --config launcher.config.json --repair
```

업데이트만 하고 앱 실행은 하지 않기:

```powershell
.\publish\win-x64\UeDtLauncher.exe run --config launcher.config.json --no-launch
```

이전 버전으로 롤백:

```powershell
.\publish\win-x64\UeDtLauncher.exe rollback --config launcher.config.json --list
.\publish\win-x64\UeDtLauncher.exe rollback --config launcher.config.json
```

무인 서버(픽셀 스트리밍) 서비스 모드 — 자세한 내용은 `docs/service-mode.md`:

```bash
./publish/linux-x64/UeDtLauncher service --config launcher.config.json --interval 300
```

## manifest 생성

Windows 패키징 파일용 manifest 생성 예시:

```powershell
.\publish\win-x64\UeDtLauncher.exe generate-manifest `
  --package-dir "C:\PackageBuilds\ue-dt-simulator\1.0.0\windows-x64" `
  --base-url "https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files" `
  --entry-point "Windows/m7at10_dt.exe" `
  --version "1.0.0" `
  --platform "windows-x64" `
  --output "manifest.json"
```

Linux 패키징 파일용 manifest 생성 예시:

```bash
./publish/linux-x64/UeDtLauncher generate-manifest \
  --package-dir "/home/builds/ue-dt-simulator/1.1.0-dev.3/linux-x64" \
  --base-url "https://updates.example.com/projects/ue-dt-simulator/dev/dev/1.1.0-dev.3/linux-x64/files" \
  --entry-point "Linux/m7at10_dt.sh" \
  --version "1.1.0-dev.3" \
  --platform "linux-x64" \
  --output "manifest.json"
```

## catalog/manifest 서명

운영 환경에서는 catalog와 manifest 변조 방지를 위해 서명 검증을 켜는 것을 권장합니다.

ECDSA P-256 키 생성 예시:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out manifest-private-key.pem
openssl ec -in manifest-private-key.pem -pubout -out manifest-public-key.pem
```

manifest 또는 catalog 서명 생성:

```powershell
.\publish\win-x64\UeDtLauncher.exe sign-manifest `
  --manifest "catalog.json" `
  --private-key "manifest-private-key.pem" `
  --output "catalog.json.sig"
```

클라이언트에는 public key만 배포합니다.

## ZIP/7z 패키지 업데이트

파일 단위 manifest 업데이트와 별개로 큰 파일 묶음을 패키지 단위로 받을 수 있습니다.

```json
{
  "packages": [
    {
      "id": "content-paks-1.0.1",
      "url": "https://your-server.example.com/packages/content-paks-1.0.1.zip",
      "sha256": "<package sha256>",
      "size": 123456789,
      "extractTo": ".",
      "required": true,
      "format": "zip"
    }
  ]
}
```

7z를 쓰려면 클라이언트 PC의 PATH에서 `7z`, `7zz`, `7za` 중 하나가 발견되어야 합니다.

## CI

`.github/workflows/build.yml`에서 Windows/Linux `dotnet build`와 publish를 수행합니다. GitHub Actions 결과를 통해 실제 빌드 오류를 확인할 수 있습니다.
