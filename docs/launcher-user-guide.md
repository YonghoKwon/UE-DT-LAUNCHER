# UE-DT Launcher 사용 가이드

이 문서는 UE-DT Launcher를 실제로 빌드하고, 일반 사용자 모드와 개발자 모드로 실행하고, 서버 배포 파일을 업데이트하는 기본 흐름을 설명합니다.

## 1. 모드 개념

런처는 `launcher.config.json`의 `clientProfile` 값으로 화면과 권한을 나눕니다.

```json
{
  "clientProfile": "general"
}
```

일반 사용자 모드는 운영/안정화 버전을 실행하는 화면입니다. 화면은 밝은 테마이며, `prod / stable / latest / 현재 OS` 조합만 사용할 수 있습니다.

```json
{
  "clientProfile": "developer"
}
```

개발자 모드는 개발/검증용 화면입니다. 화면은 어두운 테마이며, 가동/개발 구분, 채널, 버전 정책을 선택할 수 있습니다. OS는 현재 실행 중인 PC 기준으로 고정됩니다.

## 2. 빌드 방법

저장소 루트에서 실행합니다.

```powershell
cd C:\Users\ho270\RiderProjects\UE-DT-LAUNCHER
git checkout feature/launcher-production-hardening
git pull origin feature/launcher-production-hardening

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\publish-win-x64.ps1
```

빌드가 성공하면 실행 파일은 아래에 생성됩니다.

```text
publish\win-x64\UeDtLauncher.exe
```

## 3. 일반 사용자 모드 실행

저장소 루트에서 빌드한 뒤 아래 명령을 실행합니다.

```powershell
cd .\publish\win-x64
Copy-Item ..\..\examples\configs\general-windows-launcher.config.json .\launcher.config.json -Force
Get-Content .\launcher.config.json | Select-String clientProfile
.\UeDtLauncher.exe
```

정상이라면 `clientProfile`이 `general`로 보입니다.

```text
"clientProfile": "general"
```

화면 우측 상단 배지는 `일반 사용자`로 표시됩니다.

일반 사용자 모드의 배포 선택은 아래로 고정됩니다.

```text
가동/개발: prod
채널: stable
버전: latest
OS: 현재 실행 중인 OS
```

Windows에서 실행하면 `windows-x64`, Linux에서 실행하면 `linux-x64`만 사용합니다.

## 4. 개발자 모드 실행

저장소 루트에서 빌드한 뒤 아래 명령을 실행합니다.

```powershell
cd .\publish\win-x64
Copy-Item ..\..\examples\configs\developer-windows-launcher.config.json .\launcher.config.json -Force
Get-Content .\launcher.config.json | Select-String clientProfile
.\UeDtLauncher.exe
```

정상이라면 `clientProfile`이 `developer`로 보입니다.

```text
"clientProfile": "developer"
```

화면 우측 상단 배지는 `개발자`로 표시되고, 화면은 어두운 테마로 표시됩니다.

개발자 모드에서는 왼쪽 `배포 선택` 영역에서 다음 값을 고를 수 있습니다.

```text
가동/개발: prod / dev
채널: prod 선택 시 stable / beta, dev 선택 시 dev / beta / stable
버전: latest / exact
OS: 현재 실행 중인 OS로 고정
```

OS는 의도적으로 ComboBox로 열어두지 않았습니다. Windows 개발자 PC에서는 Windows 빌드만, Linux 개발자 PC에서는 Linux 빌드만 받도록 하기 위한 구조입니다.

## 5. 프로젝트/버전/OS/가동·개발 선택 방식

왼쪽 영역은 단순 프로젝트 목록이 아니라 `배포 선택` 영역입니다.

```text
프로젝트 검색
가동/개발 선택
채널 선택
버전 정책 선택
현재 OS 표시
프로젝트 카드 목록
```

프로젝트 카드는 다음 정보를 함께 보여줍니다.

```text
프로젝트명
상태
가동/개발 배지
채널 배지
버전 정책 배지
OS 배지
```

현재 구현에서 `버전`은 `latest` 또는 `exact` 정책으로 관리합니다. `exact`를 실제 특정 버전 번호와 연결하려면 `launcher.config.json`의 `requestedVersion` 값과 catalog의 release 버전을 맞춰야 합니다.

## 6. 프로젝트 이미지 넣는 방법

프로젝트별 대표 이미지는 publish 폴더 아래에 넣습니다.

```text
publish\win-x64\assets\projects\ue-dt-simulator\thumbnail.png
publish\win-x64\assets\projects\ue-dt-simulator\hero.png
```

권장 크기:

```text
thumbnail.png : 480 x 320
hero.png      : 1920 x 720
```

`launcher.config.json`에서는 다음처럼 연결합니다.

```json
{
  "projectAssetsDir": "assets/projects",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator",
      "thumbnailPath": "assets/projects/ue-dt-simulator/thumbnail.png",
      "heroPath": "assets/projects/ue-dt-simulator/hero.png"
    }
  ]
}
```

이미지가 없으면 런처가 자동 placeholder를 표시합니다.

## 7. 일반 사용자 화면에서 하는 일

일반 사용자 화면에서는 아래 기능만 사용합니다.

```text
프로젝트 검색
프로젝트 선택
실행
상태 확인
설치 폴더
설정
문제 보고용 로그 저장
```

중복을 줄이기 위해 일반 사용자 메인 화면에서는 `설치 폴더` 버튼을 실행 영역에 두고, 왼쪽 하단에는 `설정`만 남겼습니다.

### 실행

선택한 프로젝트를 업데이트 확인 후 실행합니다.

### 상태 확인

현재 설치 폴더와 manifest 파일을 비교해서 상태를 표시합니다.

상태 종류:

```text
확인 필요
설치 필요
업데이트 가능
최신 상태
오류
```

### 진행률

실행/업데이트/상태 확인 중에는 진행률이 숫자로 함께 표시됩니다.

```text
0% → 10% → 20% → 35% → 45% 이상 → 85% → 95% → 100%
```

엔진에서 실제 퍼센트를 주는 단계는 실제 값이 표시되고, 퍼센트를 주지 않는 단계는 단계명 기준으로 보정된 값이 표시됩니다.

### 설정

일반 사용자용 설정 팝업을 엽니다. 프로젝트명, 설치 위치, 설정 파일 경로, OS, 설치 상태, 캐시/백업 용량을 확인할 수 있습니다.

## 8. 개발자 화면에서 하는 일

개발자 화면에서는 아래 기능을 사용할 수 있습니다.

```text
가동/개발 선택
채널 선택
버전 정책 선택
프로젝트 검색
프로젝트 선택
실행
업데이트
상태 확인
검증/복구
설치 폴더
캐시 정리
로그 저장
로그 지우기
다시 시도
설정 팝업
설정 다시 읽기
```

중복 버튼은 줄였습니다. 환경/채널/버전 정책 선택은 왼쪽 `배포 선택` 영역으로 이동했고, 메인 개발자 패널에는 실제 동작 버튼만 배치했습니다.

### 가동/개발, 채널, 버전 정책 변경

왼쪽 배포 선택 영역에서 값을 변경하면 현재 런처 화면의 선택 값이 바뀝니다. 이후 `상태 확인`, `업데이트`, `실행`을 누르면 변경된 값으로 catalog release를 선택합니다.

### 검증/복구

파일이 누락되었거나 SHA-256이 다른 경우 다시 다운로드해서 복구합니다.

### 캐시 정리

`stagingDir` 폴더를 삭제하고 다시 생성합니다. `backupDir`는 자동 삭제하지 않습니다.

### 로그 저장

현재 로그를 아래 위치에 저장합니다.

```text
publish\win-x64\logs\launcher-yyyyMMdd-HHmmss.log
```

## 9. 서버 파일 구조 예시

Nginx 서버에 여러 프로젝트, 버전, OS, 환경을 함께 올릴 때는 다음 구조를 권장합니다.

```text
/var/www/ue-dt-updates/
  catalogs/
    general/
      catalog.json
      catalog.json.sig
    developer/
      catalog.json
      catalog.json.sig
  projects/
    ue-dt-simulator/
      prod/
        stable/
          1.0.0/
            windows-x64/
              manifest.json
              manifest.json.sig
              files/
      dev/
        dev/
          1.1.0-dev.3/
            windows-x64/
              manifest.json
              manifest.json.sig
              files/
            linux-x64/
              manifest.json
              manifest.json.sig
              files/
```

일반 사용자는 `catalogs/general`과 `prod/stable`만 접근 가능하게 두고, 개발자 catalog와 dev 파일들은 서버 인증으로 보호하는 것을 권장합니다.

## 10. 자주 생기는 문제

### developer 설정인데 일반 사용자로 보이는 경우

아래를 확인합니다.

```powershell
cd .\publish\win-x64
Get-Content .\launcher.config.json | Select-String clientProfile
```

값이 `developer`인데도 일반 사용자로 보이면, 화면에 표시되는 config 경로를 확인합니다.

```text
config: C:\...\publish\win-x64\launcher.config.json
```

이 경로가 실제로 복사한 파일과 다르면 다른 위치의 런처를 실행하고 있는 것입니다.

### OS 선택이 보이지 않는 경우

정상입니다. OS는 현재 실행 중인 PC 기준으로 자동 고정됩니다.

```text
Windows 실행: windows-x64
Linux 실행: linux-x64
```

다른 OS의 패키지를 받아야 한다면 해당 OS에서 런처를 실행하거나, 서버/테스트용 config를 별도로 만들어야 합니다.

### 버튼에 마우스를 올렸을 때 글자가 보이지 않는 경우

최신 UI에서는 버튼 content를 `TextBlock`으로 직접 구성해서 글자색을 고정했습니다. 최신 브랜치를 pull하고 다시 publish해야 반영됩니다.

```powershell
git pull origin feature/launcher-production-hardening
.\scripts\publish-win-x64.ps1
```

### 업데이트 서버 연결 실패

`catalogUrl`, `manifestUrl`이 실제 접근 가능한 URL인지 확인합니다.

로컬 테스트라면 서버를 먼저 켭니다.

```powershell
cd C:\UpdateServer
python -m http.server 8080
```

그 다음 config의 URL이 아래처럼 맞는지 확인합니다.

```json
{
  "catalogUrl": "http://localhost:8080/catalogs/developer/catalog.json"
}
```

## 11. 운영 배포 순서 요약

1. Unreal 프로젝트를 Windows/Linux로 패키징합니다.
2. 패키징 결과를 서버의 `files` 폴더에 올립니다.
3. `generate-manifest`로 manifest를 만듭니다.
4. 필요하면 manifest에 서명합니다.
5. catalog.json에 release 정보를 추가합니다.
6. 필요하면 catalog에 서명합니다.
7. 클라이언트 PC에 `UeDtLauncher.exe`, `launcher.config.json`, public key를 배포합니다.
8. 사용자는 런처를 실행합니다.
9. 런처가 catalog/manifest를 확인하고 필요한 파일만 다운로드합니다.
10. 업데이트 완료 후 Unreal 패키지 실행 파일을 실행합니다.
