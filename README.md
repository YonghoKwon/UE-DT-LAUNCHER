# UE-DT-LAUNCHER

UE-DT-LAUNCHER는 Unreal Engine 패키징 결과물을 Windows/Linux PC에 배포하고, 실행 전에 최신 파일로 자동 업데이트한 뒤 앱을 실행하기 위한 경량 런처입니다.

이 브랜치는 Netmarble Launcher 분석 결과를 바탕으로, PC 배포에 필요한 핵심 기능을 2차로 확장한 버전입니다.

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
- GitHub Actions 기반 Windows/Linux 빌드 검증 워크플로
- 원격 `manifest.json` 다운로드
- manifest ECDSA SHA-256 서명 검증 옵션
- 파일별 SHA-256 비교
- 변경/누락 파일만 다운로드
- `.staging` 다운로드 후 검증
- `.backup` 백업 후 실제 설치 폴더 반영
- 적용 실패 시 rollback
- HTTP Range 기반 이어받기 시도
- 다운로드 retry
- repair 모드
- ZIP 패키지 다운로드/압축 해제
- 7z 패키지 다운로드/압축 해제. 단, `7z`, `7zz`, `7za` 실행 파일이 PATH에 있어야 함
- 런처 자기 자신 업데이트 준비 기능
- Windows 바탕화면/시작 메뉴 shortcut 생성 옵션
- manifest 생성 명령
- manifest 서명 생성 명령
- sample config 생성 명령

## 저장소 구조

```text
UE-DT-LAUNCHER/
  scripts/
    publish-win-x64.ps1
    publish-linux-x64.sh
  src/
    UeDtLauncher/
      UeDtLauncher.csproj
      Program.cs
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
    netmarble-launcher-analysis.md
```

## 빌드 방법

.NET 8 SDK가 필요합니다.

```powershell
dotnet build src/UeDtLauncher/UeDtLauncher.csproj -c Release
```

Windows publish:

```powershell
.\scripts\publish-win-x64.ps1
```

또는 직접:

```powershell
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o publish/win-x64
```

Linux publish:

```bash
./scripts/publish-linux-x64.sh
```

또는 직접:

```bash
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/linux-x64
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

## manifest 생성

Windows 패키징 파일용 manifest 생성 예시:

```powershell
.\publish\win-x64\UeDtLauncher.exe generate-manifest `
  --package-dir "C:\UpdateServer\files\windows-x64" `
  --base-url "https://your-server.example.com/files/windows-x64" `
  --entry-point "Windows/m7at10_dt.exe" `
  --version "1.0.0" `
  --platform "windows-x64" `
  --output "C:\UpdateServer\files\windows-x64\manifest.json"
```

Linux 패키징 파일용 manifest 생성 예시:

```bash
./publish/linux-x64/UeDtLauncher generate-manifest \
  --package-dir "/home/ue/update-server/files/linux-x64" \
  --base-url "https://your-server.example.com/files/linux-x64" \
  --entry-point "Linux/m7at10_dt.sh" \
  --version "1.0.0" \
  --platform "linux-x64" \
  --output "/home/ue/update-server/files/linux-x64/manifest.json"
```

## manifest 서명

운영 환경에서는 manifest 변조 방지를 위해 서명 검증을 켜는 것을 권장합니다.

ECDSA P-256 키 생성 예시:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out manifest-private-key.pem
openssl ec -in manifest-private-key.pem -pubout -out manifest-public-key.pem
```

manifest 서명 생성:

```powershell
.\publish\win-x64\UeDtLauncher.exe sign-manifest `
  --manifest "C:\UpdateServer\files\windows-x64\manifest.json" `
  --private-key "manifest-private-key.pem" `
  --output "C:\UpdateServer\files\windows-x64\manifest.json.sig"
```

클라이언트에는 public key만 배포합니다.

```json
{
  "manifestUrl": "https://your-server.example.com/files/windows-x64/manifest.json",
  "manifestSignatureUrl": "https://your-server.example.com/files/windows-x64/manifest.json.sig",
  "manifestPublicKeyPath": "manifest-public-key.pem"
}
```

## launcher.config.json 예시

```json
{
  "manifestUrl": "https://your-server.example.com/files/windows-x64/manifest.json",
  "manifestSignatureUrl": "https://your-server.example.com/files/windows-x64/manifest.json.sig",
  "manifestPublicKeyPath": "manifest-public-key.pem",
  "installDir": "app",
  "stagingDir": ".staging",
  "backupDir": ".backup",
  "installedManifestPath": "installed-manifest.json",
  "launchAfterUpdate": true,
  "repairMode": false,
  "removeFilesNotInManifest": false,
  "maxRetryCount": 3,
  "httpTimeoutSeconds": 300,
  "launchArguments": ["-log"],
  "packages": [],
  "selfUpdate": {
    "enabled": false,
    "manifestUrl": "https://your-server.example.com/launcher/win-x64/manifest.json",
    "manifestSignatureUrl": "https://your-server.example.com/launcher/win-x64/manifest.json.sig",
    "manifestPublicKeyPath": "manifest-public-key.pem",
    "installDir": "launcher-update",
    "entryPoint": "UeDtLauncher.exe"
  },
  "windowsIntegration": {
    "appName": "UE Digital Twin",
    "publisher": "UE-DT",
    "shortcutName": "UE Digital Twin Launcher",
    "iconPath": null,
    "createDesktopShortcut": false,
    "createStartMenuShortcut": false,
    "registerAppEntry": false
  }
}
```

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

## 런처 자기 자신 업데이트

현재 구현은 자동 교체가 아니라 안전한 준비 단계입니다.

```text
1. selfUpdate.enabled=true
2. 별도 self update manifest 다운로드
3. 새 런처 파일을 launcher-update 폴더에 다운로드/검증
4. SELF_UPDATE_READY.txt 생성
5. 운영자 또는 설치 관리자가 기존 런처를 교체
```

실제 운영에서 완전 자동 교체를 하려면 별도의 bootstrapper 또는 installer를 두는 구조를 권장합니다.

## Windows shortcut / app entry

`windowsIntegration.createDesktopShortcut=true` 또는 `createStartMenuShortcut=true`로 shortcut을 만들 수 있습니다.

현재는 무거운 MSI/MSIX installer를 직접 생성하지 않고, `.url` shortcut과 앱 등록 설명 파일을 생성하는 가벼운 형태입니다. 정식 배포에서는 WiX, MSIX, Inno Setup, NSIS 같은 installer를 붙이는 것을 권장합니다.

## 업데이트 서버 구성

테스트 서버:

```powershell
cd C:\UpdateServer\files
python -m http.server 8080
```

운영에서는 `localhost`가 아니라 외부 접근 가능한 HTTPS 서버를 사용해야 합니다.

```text
https://updates.your-company.com/files/windows-x64/manifest.json
https://updates.your-company.com/files/linux-x64/manifest.json
```

## CI

`.github/workflows/build.yml`에서 Windows/Linux `dotnet build`와 publish를 수행합니다. GitHub Actions 결과를 통해 실제 빌드 오류를 확인할 수 있습니다.
