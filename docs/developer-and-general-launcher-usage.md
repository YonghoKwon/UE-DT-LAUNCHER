# UE-DT Launcher 개발자/일반 사용자 사용 가이드

이 문서는 UE-DT Launcher를 로컬 또는 운영 서버에서 사용할 때, 개발자 모드와 일반 사용자 모드를 어떻게 구분하고 설정하는지 설명합니다.

## 1. 핵심 개념

런처는 `launcher.config.json`의 `clientProfile` 값으로 모드를 나눕니다.

```json
"clientProfile": "developer"
```

개발자 모드는 개발/검증용 배포를 받을 수 있습니다. 화면은 다크 테마이고, `prod/dev`, `stable/beta/dev`, `latest/exact`를 선택할 수 있습니다.

```json
"clientProfile": "general"
```

일반 사용자 모드는 운영 안정화 배포만 받을 수 있습니다. 화면은 밝은 테마이고, `prod / stable / latest` 조합으로 고정됩니다.

OS는 현재 실행 중인 PC 기준으로 자동 고정됩니다.

```text
Windows 실행: windows-x64
Linux 실행: linux-x64
```

## 2. 권장 서버 구조

여러 프로젝트, 여러 버전, 여러 OS, 운영/개발 배포를 한 서버에서 관리하려면 아래 구조를 권장합니다.

```text
/updates-root
  catalogs
    developer
      catalog.json
    general
      catalog.json

  projects
    ue-dt-simulator
      dev
        dev
          1.0.0-local
            windows-x64
              manifest.json
              files
                Windows
                  m7at10_dt.exe
                  ...
            linux-x64
              manifest.json
              files
      prod
        stable
          1.0.0-local
            windows-x64
              manifest.json
              files
            linux-x64
              manifest.json
              files
```

로컬 Windows 테스트 기준 루트는 보통 아래처럼 둡니다.

```text
C:\UpdateServer
```

로컬 서버 실행은 반드시 `C:\UpdateServer`에서 해야 합니다.

```powershell
cd C:\UpdateServer
python -m http.server 8080
```

## 3. 개발자 모드 서버 설정

개발자 모드는 보통 아래 조합을 사용합니다.

```text
clientProfile = developer
environment   = dev
channel       = dev
versionPolicy = latest 또는 exact
platform      = windows-x64 또는 linux-x64
```

### 3.1 개발자용 release 폴더

Windows 로컬 테스트 예시입니다.

```text
C:\UpdateServer\projects\ue-dt-simulator\dev\dev\1.0.0-local\windows-x64
  manifest.json
  files
    Windows
      m7at10_dt.exe
      ...
```

### 3.2 개발자용 catalog.json

위치:

```text
C:\UpdateServer\catalogs\developer\catalog.json
```

예시:

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-01-01T00:00:00Z",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator Local",
      "releases": [
        {
          "version": "1.0.0-local",
          "channel": "dev",
          "environment": "dev",
          "platform": "windows-x64",
          "manifestUrl": "http://localhost:8080/projects/ue-dt-simulator/dev/dev/1.0.0-local/windows-x64/manifest.json",
          "manifestSignatureUrl": null,
          "allowedClientProfiles": ["developer"],
          "isLatest": true,
          "notes": "로컬 개발자 테스트용 Unreal 패키징 빌드입니다."
        }
      ]
    }
  ]
}
```

## 4. 일반 사용자 모드 서버 설정

일반 사용자 모드는 아래 조합으로 고정됩니다.

```text
clientProfile = general
environment   = prod
channel       = stable
versionPolicy = latest
platform      = windows-x64 또는 linux-x64
```

### 4.1 일반 사용자용 release 폴더

Windows 로컬 테스트 예시입니다.

```text
C:\UpdateServer\projects\ue-dt-simulator\prod\stable\1.0.0-local\windows-x64
  manifest.json
  files
    Windows
      m7at10_dt.exe
      ...
```

### 4.2 일반 사용자용 catalog.json

위치:

```text
C:\UpdateServer\catalogs\general\catalog.json
```

예시:

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-01-01T00:00:00Z",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator Local",
      "releases": [
        {
          "version": "1.0.0-local",
          "channel": "stable",
          "environment": "prod",
          "platform": "windows-x64",
          "manifestUrl": "http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0-local/windows-x64/manifest.json",
          "manifestSignatureUrl": null,
          "allowedClientProfiles": ["general"],
          "isLatest": true,
          "notes": "로컬 일반 사용자 테스트용 안정화 릴리스입니다."
        }
      ]
    }
  ]
}
```

일반 사용자가 개발용 버전을 받지 못하게 하려면, 일반 사용자 catalog에는 `dev/dev` release를 넣지 않습니다. 또한 개발자 release의 `allowedClientProfiles`에는 `general`을 넣지 않습니다.

## 5. manifest.json 구조

manifest는 실제 배포 파일 목록입니다.

예시:

```json
{
  "appId": "ue-dt-simulator",
  "version": "1.0.0-local",
  "channel": "stable",
  "platform": "windows-x64",
  "entryPoint": "Windows/m7at10_dt.exe",
  "baseUrl": "http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0-local/windows-x64/files",
  "files": [
    {
      "path": "Windows/m7at10_dt.exe",
      "sha256": "...",
      "size": 146432,
      "url": "Windows/m7at10_dt.exe",
      "executable": true
    }
  ]
}
```

중요한 점:

```text
entryPoint는 installDir 기준 상대 경로입니다.
baseUrl은 files 폴더 URL입니다.
files[].path는 files 폴더 아래 상대 경로입니다.
files[].url도 files 폴더 아래 상대 경로로 두면 됩니다.
```

예를 들어 최종 설치 결과가 아래라면:

```text
publish\win-x64\app\Windows\m7at10_dt.exe
```

entryPoint는 아래처럼 해야 합니다.

```json
"entryPoint": "Windows/m7at10_dt.exe"
```

## 6. 개발자 모드 launcher.config.json

위치:

```text
publish\win-x64\launcher.config.json
```

개발자 로컬 테스트 예시:

```json
{
  "catalogUrl": "http://localhost:8080/catalogs/developer/catalog.json",
  "catalogSignatureUrl": null,
  "catalogPublicKeyPath": null,

  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "requestedVersion": null,
  "targetPlatform": "windows-x64",

  "manifestUrl": "http://localhost:8080/projects/ue-dt-simulator/dev/dev/1.0.0-local/windows-x64/manifest.json",
  "manifestSignatureUrl": null,
  "manifestPublicKeyPath": null,

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

  "projectAssetsDir": "assets/projects",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator Local",
      "description": "로컬 개발자 업데이트 테스트 프로젝트입니다.",
      "status": "로컬 테스트",
      "installPath": "app",
      "engineVersion": "Unreal",
      "technology": "Windows",
      "sortOrder": 0,
      "isPinned": true,
      "visibleToProfiles": ["developer"]
    }
  ],
  "packages": []
}
```

## 7. 일반 사용자 모드 launcher.config.json

일반 사용자 로컬 테스트 예시:

```json
{
  "catalogUrl": "http://localhost:8080/catalogs/general/catalog.json",
  "catalogSignatureUrl": null,
  "catalogPublicKeyPath": null,

  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "requestedVersion": null,
  "targetPlatform": "windows-x64",

  "manifestUrl": "http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0-local/windows-x64/manifest.json",
  "manifestSignatureUrl": null,
  "manifestPublicKeyPath": null,

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

  "projectAssetsDir": "assets/projects",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator Local",
      "description": "로컬 일반 사용자 업데이트 테스트 프로젝트입니다.",
      "status": "안정화 로컬 테스트",
      "installPath": "app",
      "engineVersion": "Unreal",
      "technology": "Windows",
      "sortOrder": 0,
      "isPinned": true,
      "visibleToProfiles": ["general"]
    }
  ],
  "packages": []
}
```

## 8. 로컬 테스트 순서

### 8.1 공통 준비

```powershell
cd C:\Users\ho270\RiderProjects\UE-DT-LAUNCHER
git checkout feature/launcher-production-hardening
git pull origin feature/launcher-production-hardening

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\publish-win-x64.ps1
```

서버 실행:

```powershell
cd C:\UpdateServer
python -m http.server 8080
```

### 8.2 개발자 모드 테스트

`publish\win-x64\launcher.config.json`을 개발자용으로 설정한 뒤:

```powershell
cd C:\Users\ho270\RiderProjects\UE-DT-LAUNCHER\publish\win-x64
.\UeDtLauncher.exe
```

확인할 항목:

```text
우측 상단: 개발자
가동/개발: dev
채널: dev
버전: latest 또는 exact
OS: windows-x64
```

실행 순서:

```text
1. 왼쪽 상단 ↻ 클릭
2. 카탈로그 확인 완료 확인
3. 상태 확인
4. 실행 또는 업데이트
5. 개발자 실행 확인 팝업에서 실행
6. 100% 완료 표시 확인
```

### 8.3 일반 사용자 모드 테스트

`publish\win-x64\launcher.config.json`을 일반 사용자용으로 설정한 뒤:

```powershell
cd C:\Users\ho270\RiderProjects\UE-DT-LAUNCHER\publish\win-x64
.\UeDtLauncher.exe
```

확인할 항목:

```text
우측 상단: 일반 사용자
가동/개발: prod 고정
채널: stable 고정
버전: latest 고정
OS: windows-x64
```

실행 순서:

```text
1. 왼쪽 상단 ↻ 클릭
2. 카탈로그 확인 완료 확인
3. 상태 확인
4. 실행
5. 100% 완료 표시 확인
```

## 9. 테스트 전 초기화

개발자/일반 사용자 모드를 번갈아 테스트할 때는 기존 설치 상태가 섞일 수 있습니다. 필요하면 아래를 실행합니다.

```powershell
cd C:\Users\ho270\RiderProjects\UE-DT-LAUNCHER\publish\win-x64

Remove-Item .\.staging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\.backup -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\app -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item .\installed-manifest.json -Force -ErrorAction SilentlyContinue
```

## 10. 자주 발생하는 문제

### 10.1 catalog 404

원인:

```text
http.server를 C:\UpdateServer가 아닌 다른 폴더에서 실행함
catalog.json이 catalogs/developer 또는 catalogs/general 아래에 없음
launcher.config.json의 catalogUrl이 잘못됨
```

확인:

```text
http://localhost:8080/catalogs/developer/catalog.json
http://localhost:8080/catalogs/general/catalog.json
```

### 10.2 받을 수 있는 배포 버전이 없음

원인:

```text
clientProfile 불일치
environment 불일치
channel 불일치
platform 불일치
versionPolicy/latest/exact 조건 불일치
```

개발자 성공 조합:

```text
config: developer / dev / dev / latest / windows-x64
catalog: allowedClientProfiles includes developer, environment dev, channel dev, isLatest true, platform windows-x64
```

일반 사용자 성공 조합:

```text
config: general / prod / stable / latest / windows-x64
catalog: allowedClientProfiles includes general, environment prod, channel stable, isLatest true, platform windows-x64
```

### 10.3 entryPoint 오류

최종 설치된 실행 파일 경로와 manifest의 entryPoint가 일치해야 합니다.

```text
app\Windows\m7at10_dt.exe
```

이면:

```json
"entryPoint": "Windows/m7at10_dt.exe"
```

### 10.4 98%에서 멈춰 보이는 문제

런처 엔진에서 launch 후 `Complete 100%` 이벤트를 보내도록 수정했습니다. 최신 브랜치를 pull하고 다시 publish해야 반영됩니다.

```powershell
git pull origin feature/launcher-production-hardening
.\scripts\publish-win-x64.ps1
```

## 11. 운영 배포 시 권장 정책

일반 사용자와 개발자 배포는 catalog를 분리하는 것이 좋습니다.

```text
catalogs/general/catalog.json   → prod/stable/latest만 포함
catalogs/developer/catalog.json → dev/beta/stable 개발자용 포함
```

일반 사용자에게 개발용 배포를 숨기려면 다음을 지킵니다.

```text
general catalog에는 dev release를 넣지 않음
dev release의 allowedClientProfiles에는 general을 넣지 않음
일반 사용자 launcher.config.json은 catalogs/general/catalog.json만 바라봄
```

운영 서버에서는 Nginx 인증 또는 네트워크 접근 제어로 developer catalog 경로를 보호하는 것을 권장합니다.
