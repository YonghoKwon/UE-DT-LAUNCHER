# Release Publishing Scripts

이 문서는 UE-DT Launcher 업데이트 서버에 새 릴리스를 등록할 때 사용하는 PowerShell 스크립트를 설명합니다.

## 1. 스크립트 목록

```text
tools/generate-manifest.ps1
tools/update-catalog.ps1
tools/publish-release.ps1
```

### generate-manifest.ps1

패키징 파일 폴더를 읽어서 `manifest.json`을 생성합니다.

### update-catalog.ps1

`catalog.json`에 프로젝트/릴리스 정보를 추가, 갱신, 삭제합니다.

### publish-release.ps1

패키징 파일 복사, manifest 생성, catalog 갱신을 한 번에 수행합니다.

운영에서는 보통 `publish-release.ps1`만 사용하면 됩니다.

현재 publish는 원자적 transaction이며 운영 배포에는 `UE_DT_SIGNING_PRIVATE_KEY`와 `UE_DT_SIGNING_KEY_ID`가 필요합니다. 서명키가 없으면 production publish는 실패하고, `--dry-run`은 release/catalog를 변경하지 않습니다.

## 2. 권장 서버 구조

로컬 테스트:

```text
C:\UpdateServer
  catalogs
    general
      catalog.json
    developer
      catalog.json
  projects
    ue-dt-simulator
      prod
        stable
          1.0.0
            windows-x64
              manifest.json
              files
      dev
        dev
          1.0.0-dev.1
            windows-x64
              manifest.json
              files
```

Linux 서버 운영:

```text
/srv/ue-dt-updates
  catalogs
    general
      catalog.json
    developer
      catalog.json
  projects
    ...
```

## 3. 한 번에 릴리스 등록하기

### 3.1 일반 사용자용 prod/stable 릴리스 등록

Windows PowerShell 예시입니다.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

.\tools\publish-release.ps1 `
  -PackageDir "C:\UnrealPackages\m7at10_dt" `
  -ServerRoot "C:\UpdateServer" `
  -BaseUrlRoot "http://localhost:8080" `
  -ProjectId "ue-dt-simulator" `
  -DisplayName "UE-DT Simulator" `
  -Version "1.0.0" `
  -Environment "prod" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -EntryPoint "Windows/m7at10_dt.exe" `
  -CatalogProfile "general" `
  -AllowedClientProfiles "general" `
  -Notes "운영 안정화 릴리스입니다." `
  -SetLatest `
  -CleanFiles
```

결과:

```text
C:\UpdateServer\projects\ue-dt-simulator\prod\stable\1.0.0\windows-x64\files
C:\UpdateServer\projects\ue-dt-simulator\prod\stable\1.0.0\windows-x64\manifest.json
C:\UpdateServer\catalogs\general\catalog.json
```

### 3.2 개발자용 dev/dev 릴리스 등록

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

.\tools\publish-release.ps1 `
  -PackageDir "C:\UnrealPackages\m7at10_dt" `
  -ServerRoot "C:\UpdateServer" `
  -BaseUrlRoot "http://localhost:8080" `
  -ProjectId "ue-dt-simulator" `
  -DisplayName "UE-DT Simulator" `
  -Version "1.0.0-dev.1" `
  -Environment "dev" `
  -Channel "dev" `
  -Platform "windows-x64" `
  -EntryPoint "Windows/m7at10_dt.exe" `
  -CatalogProfile "developer" `
  -AllowedClientProfiles "developer" `
  -Notes "개발자 테스트 릴리스입니다." `
  -SetLatest `
  -CleanFiles
```

결과:

```text
C:\UpdateServer\projects\ue-dt-simulator\dev\dev\1.0.0-dev.1\windows-x64\files
C:\UpdateServer\projects\ue-dt-simulator\dev\dev\1.0.0-dev.1\windows-x64\manifest.json
C:\UpdateServer\catalogs\developer\catalog.json
```

## 4. Linux 서버용 URL 예시

Linux Nginx 서버가 `/updates/` URL로 `/srv/ue-dt-updates`를 제공한다면:

```powershell
-ServerRoot "\\internal-file-share\ue-dt-updates"
-BaseUrlRoot "http://10.10.10.20/updates"
```

또는 Linux 서버에 파일을 먼저 복사한 뒤 서버에서 PowerShell 7을 사용할 수 있다면:

```powershell
-ServerRoot "/srv/ue-dt-updates"
-BaseUrlRoot "http://10.10.10.20/updates"
```

인터넷이 차단된 Linux 서버라면 PowerShell 7 설치가 어려울 수 있으므로, 보통은 Windows 관리자 PC에서 결과물을 만든 뒤 보안 반입 절차로 `/srv/ue-dt-updates`에 복사하는 방식이 편합니다.

## 5. manifest만 생성하기

```powershell
.\tools\generate-manifest.ps1 `
  -PackageDir "C:\UpdateServer\projects\ue-dt-simulator\prod\stable\1.0.0\windows-x64\files" `
  -Output "C:\UpdateServer\projects\ue-dt-simulator\prod\stable\1.0.0\windows-x64\manifest.json" `
  -ProjectId "ue-dt-simulator" `
  -Version "1.0.0" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -EntryPoint "Windows/m7at10_dt.exe" `
  -BaseUrl "http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files"
```

## 6. catalog만 갱신하기

```powershell
.\tools\update-catalog.ps1 `
  -CatalogPath "C:\UpdateServer\catalogs\general\catalog.json" `
  -ProjectId "ue-dt-simulator" `
  -DisplayName "UE-DT Simulator" `
  -Version "1.0.0" `
  -Environment "prod" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -ManifestUrl "http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json" `
  -AllowedClientProfiles "general" `
  -Notes "운영 안정화 릴리스입니다." `
  -SetLatest
```

`-SetLatest`를 지정하면 같은 프로젝트/환경/채널/플랫폼의 기존 release는 `isLatest: false`로 바뀌고, 새 release가 `isLatest: true`가 됩니다.

## 7. release 삭제하기

```powershell
.\tools\update-catalog.ps1 `
  -CatalogPath "C:\UpdateServer\catalogs\general\catalog.json" `
  -ProjectId "ue-dt-simulator" `
  -DisplayName "UE-DT Simulator" `
  -Version "1.0.0" `
  -Environment "prod" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -ManifestUrl "unused" `
  -AllowedClientProfiles "general" `
  -RemoveRelease `
  -RemoveProjectIfEmpty
```

## 8. 주의 사항

### EntryPoint

`EntryPoint`는 `files` 폴더 아래 상대 경로입니다.

패키징 결과가 아래라면:

```text
files\Windows\m7at10_dt.exe
```

EntryPoint는 다음과 같습니다.

```text
Windows/m7at10_dt.exe
```

### BaseUrlRoot

로컬 `python -m http.server 8080` 테스트에서는:

```text
http://localhost:8080
```

Nginx에서 `/updates/`로 제공한다면:

```text
http://<SERVER_IP>/updates
```

### 일반 사용자와 개발자 분리

일반 사용자 catalog:

```text
CatalogProfile: general
AllowedClientProfiles: general
Environment: prod
Channel: stable
```

개발자 catalog:

```text
CatalogProfile: developer
AllowedClientProfiles: developer
Environment: dev 또는 prod
Channel: dev, beta, stable
```

## 9. 검증 URL

로컬 테스트:

```text
http://localhost:8080/catalogs/general/catalog.json
http://localhost:8080/catalogs/developer/catalog.json
http://localhost:8080/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
```

Nginx 테스트:

```text
http://<SERVER_IP>/updates/catalogs/general/catalog.json
http://<SERVER_IP>/updates/catalogs/developer/catalog.json
http://<SERVER_IP>/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
```
