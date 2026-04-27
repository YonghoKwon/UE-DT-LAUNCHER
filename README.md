# UE-DT-LAUNCHER

UE-DT-LAUNCHER는 Unreal Engine 패키징 결과물을 Windows/Linux PC에 배포하고, 실행 전에 최신 파일로 자동 업데이트한 뒤 앱을 실행하기 위한 경량 런처입니다.

이 브랜치는 Netmarble Launcher 구조를 분석한 뒤, PC 배포에 꼭 필요한 핵심만 1차로 구현한 버전입니다. Netmarble Launcher처럼 Electron 기반 플랫폼 런처 전체를 복제하지 않고, UE 패키징 파일의 안정적인 업데이트/검증/실행에 집중합니다.

## 현재 구현된 기능

- Windows/Linux 대상 크로스플랫폼 .NET 8 콘솔 런처
- 원격 `manifest.json` 다운로드
- 파일별 SHA-256 비교
- 변경/누락 파일만 다운로드
- `.staging` 폴더에 먼저 다운로드 후 검증
- `.backup` 폴더 백업 후 실제 설치 폴더에 반영
- 적용 실패 시 backup 기반 rollback
- HTTP Range 기반 이어받기 시도
- 다운로드 retry
- repair 모드
- Unreal 실행 파일 자동 실행
- manifest 생성 명령 제공
- sample config 생성 명령 제공

## 저장소 구조

```text
UE-DT-LAUNCHER/
  src/
    UeDtLauncher/
      UeDtLauncher.csproj
      Program.cs
      LauncherEngine.cs
      ManifestGenerator.cs
      Models.cs
      Hashing.cs
      JsonFiles.cs
      SafePath.cs
  docs/
    netmarble-launcher-analysis.md
  README.md
```

## 기본 동작 흐름

```text
UeDtLauncher 실행
  ↓
launcher.config.json 로드
  ↓
원격 manifest.json 다운로드
  ↓
로컬 installed-manifest.json 및 실제 파일 SHA-256 비교
  ↓
변경/누락 파일만 .staging에 다운로드
  ↓
다운로드 파일 SHA-256 검증
  ↓
기존 파일 .backup에 백업
  ↓
app/ 폴더에 새 파일 반영
  ↓
installed-manifest.json 갱신
  ↓
manifest.entryPoint 실행
```

## 빌드 방법

.NET 8 SDK가 필요합니다.

```powershell
cd UE-DT-LAUNCHER

dotnet build src/UeDtLauncher/UeDtLauncher.csproj -c Release
```

Windows용 단일 실행 파일로 publish:

```powershell
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o publish/win-x64
```

Linux용 단일 실행 파일로 publish:

```bash
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o publish/linux-x64
```

## 1. Unreal 프로젝트 패키징

예를 들어 Windows 패키징 결과가 아래 위치에 있다고 가정합니다.

```text
C:\UpdateServer\files\windows-x64\
  Windows\m7at10_dt.exe
  Windows\m7at10_dt\Content\Paks\...
  Windows\m7at10_dt\Binaries\Win64\...
```

Linux 패키징 결과는 예를 들어 아래처럼 둘 수 있습니다.

```text
/home/ue/update-server/files/linux-x64/
  Linux/m7at10_dt.sh
  Linux/m7at10_dt/Binaries/Linux/...
  Linux/m7at10_dt/Content/Paks/...
```

## 2. manifest.json 생성

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

manifest는 파일 경로, 크기, SHA-256, 다운로드 URL, 실행 파일 여부를 포함합니다.

## 3. 업데이트 서버 구성

가장 단순한 테스트 서버는 Python HTTP 서버입니다.

```powershell
cd C:\UpdateServer\files
python -m http.server 8080
```

이 경우 Windows manifest URL은 다음과 같습니다.

```text
http://localhost:8080/windows-x64/manifest.json
```

실제 운영에서는 `localhost`가 아니라 Nginx, IIS, S3, NAS HTTP 서버, 사내 파일 서버 같은 외부 접근 가능한 서버를 사용해야 합니다.

예시:

```text
https://updates.your-company.com/files/windows-x64/manifest.json
https://updates.your-company.com/files/linux-x64/manifest.json
```

## 4. 클라이언트 PC 설정 파일 생성

샘플 설정 파일 생성:

```powershell
.\UeDtLauncher.exe sample-config --output launcher.config.json
```

예시 설정:

```json
{
  "manifestUrl": "https://your-update-server.example.com/windows-x64/manifest.json",
  "installDir": "app",
  "stagingDir": ".staging",
  "backupDir": ".backup",
  "installedManifestPath": "installed-manifest.json",
  "launchAfterUpdate": true,
  "repairMode": false,
  "removeFilesNotInManifest": false,
  "maxRetryCount": 3,
  "httpTimeoutSeconds": 300,
  "launchArguments": [
    "-log"
  ]
}
```

주요 옵션:

| 옵션 | 설명 |
|---|---|
| `manifestUrl` | 원격 manifest.json 주소 |
| `installDir` | 실제 UE 패키징 파일이 설치될 폴더 |
| `stagingDir` | 다운로드 임시 폴더 |
| `backupDir` | 기존 파일 백업 폴더 |
| `installedManifestPath` | 로컬에 설치된 manifest 기록 파일 |
| `launchAfterUpdate` | 업데이트 후 앱 자동 실행 여부 |
| `repairMode` | manifest와 실제 파일을 강제로 재검증/복구할지 여부 |
| `removeFilesNotInManifest` | 새 manifest에 없는 기존 파일 삭제 여부 |
| `maxRetryCount` | 다운로드 재시도 횟수 |
| `launchArguments` | UE 실행 파일에 전달할 인자 |

## 5. 클라이언트 PC에서 실행

Windows:

```powershell
.\UeDtLauncher.exe run --config launcher.config.json
```

Linux:

```bash
chmod +x ./UeDtLauncher
./UeDtLauncher run --config launcher.config.json
```

실행하면 런처가 먼저 업데이트를 확인하고, 필요한 파일만 다운로드한 뒤 manifest의 `entryPoint`를 실행합니다.

## 6. 강제 복구 모드

사용자 PC의 파일이 손상되었거나 일부 파일이 누락된 경우 repair 모드를 실행합니다.

```powershell
.\UeDtLauncher.exe run --config launcher.config.json --repair
```

앱 실행 없이 업데이트/복구만 하고 싶으면 다음 옵션을 사용합니다.

```powershell
.\UeDtLauncher.exe run --config launcher.config.json --repair --no-launch
```

## 7. 버전 업데이트 운영 절차

실제 운영 시 권장 절차는 다음과 같습니다.

```text
1. Unreal 프로젝트 새 버전 패키징
2. 업데이트 서버의 files/windows-x64 또는 files/linux-x64에 업로드
3. UeDtLauncher generate-manifest로 새 manifest.json 생성
4. manifest.json도 서버에 업로드
5. 사용자 PC에서 UeDtLauncher 실행
6. 변경된 파일만 다운로드 후 앱 실행
```

중요한 점은 `version`을 올리는 것만으로 업데이트되는 것이 아니라, manifest의 파일 SHA-256이 바뀐 파일이 실제 업데이트 대상이 됩니다.

## 8. Netmarble Launcher와의 차이

자세한 분석은 `docs/netmarble-launcher-analysis.md`에 정리했습니다.

현재 UE-DT-LAUNCHER는 아래에 집중합니다.

- UE 패키징 파일 업데이트
- 파일 검증
- 실패 시 rollback
- Windows/Linux 실행

Netmarble Launcher가 가진 아래 기능은 아직 1차 범위에 포함하지 않았습니다.

- Electron GUI
- 계정 로그인
- 게임 라이브러리
- 배너/마케팅 영역
- 런처 자기 자신 업데이트
- Windows registry 기반 설치/삭제 관리
- shortcut 생성
- pause/resume UI
- fragment/7z 기반 대용량 패키지 다운로드

## 9. 다음 개발 추천 순서

1. WPF/Avalonia 기반 GUI 추가
2. manifest 서명 검증 추가
3. ZIP/7z 패키지 단위 업데이트 추가
4. 런처 자기 자신 업데이트 구조 추가
5. Windows shortcut/uninstall 등록 추가
6. 로그 파일 저장 및 오류 리포트 추가
7. Pixel Streaming 서버 주소/상태 표시 기능 추가

## 주의사항

현재 버전은 1차 코어 구현입니다. 실제 상용/사내 배포에 쓰기 전에는 반드시 다음을 추가하는 것을 권장합니다.

- HTTPS 서버 사용
- manifest 서명 검증
- 코드 서명된 Windows 실행 파일 배포
- 파일 잠금/디스크 부족/권한 부족에 대한 UI 처리
- CI에서 Windows/Linux publish 검증
