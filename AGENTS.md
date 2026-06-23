# AGENTS.md

이 문서는 UE-DT-LAUNCHER 저장소를 수정하는 자동화 에이전트/개발자를 위한 작업 지침입니다.

## 프로젝트 개요

UE-DT-LAUNCHER는 Unreal Engine 패키징 결과물을 Windows/Linux PC에 배포하고 실행 전 최신 상태로 업데이트하는 .NET 8 + Avalonia 기반 런처입니다.

핵심 흐름:

```text
launcher.config.json
  ↓
release catalog 선택 또는 직접 manifest 사용
  ↓
manifest 다운로드 및 선택적 서명 검증
  ↓
파일 SHA-256 비교
  ↓
변경/누락 파일 다운로드
  ↓
staging 검증
  ↓
backup 후 installDir 반영
  ↓
앱 실행
```

## 주요 디렉터리

```text
src/UeDtLauncher/
  Program.cs                         CLI/GUI 진입점
  LauncherEngine.cs                  업데이트 엔진
  CatalogResolver.cs                 release catalog 선택 로직
  ManifestGenerator.cs               manifest 생성
  ManifestSignatureVerifier.cs       ECDSA 서명 검증/생성
  PackageExtractor.cs                ZIP/7z 압축 해제
  SelfUpdateManager.cs               런처 자기 업데이트 준비
  WindowsIntegration.cs              shortcut 등 Windows 연동
  Models.cs                          설정/manifest/catalog 모델
  Gui/
    MainWindow.axaml                 최소 Window XAML
    MainWindow.axaml.cs              실제 GUI 구성 및 동작

docs/
  README.md                          문서 색인 + 빠른 시작
  guide-01-linux-server-setup.md     서버 구성 정본
  guide-02-publish-package.md        릴리스 퍼블리시
  guide-03-launcher-usage.md         런처 사용/설정 레퍼런스
  launcher-user-guide.md             GUI 화면 사용법
  launcher-ui-customization.md       UI 커스터마이징
  service-mode.md                    무인 서비스 모드
  reference/                         보조·레거시 문서

examples/
  catalogs/
  configs/
```

## 빌드/실행

Windows publish:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\publish-win-x64.ps1
```

Windows GUI 실행:

```powershell
.\publish\win-x64\UeDtLauncher.exe
```

Linux publish:

```bash
chmod +x ./scripts/publish-linux-x64.sh
./scripts/publish-linux-x64.sh
```

## GUI 개발 지침

현재 GUI는 `MainWindow.axaml`에 복잡한 XAML을 두지 않고, `MainWindow.axaml.cs`에서 프로그래밍 방식으로 구성합니다.

이유:

- Avalonia XAML 파싱 오류를 줄이기 위함
- 일반 사용자/개발자 모드 분기를 코드에서 쉽게 관리하기 위함
- 프로젝트 카드, 검색, 동적 상태 표시, ComboBox 등을 설정 기반으로 구성하기 위함

### 일반 사용자 모드

`clientProfile = general`일 때는 다음 원칙을 지켜야 합니다.

- 기술 로그, stack trace, config path 입력창을 노출하지 않습니다.
- 개발자용 기능인 복구, 캐시 정리, 매니페스트 검증, 환경/채널/플랫폼 변경은 숨깁니다.
- 노출 기능은 실행, 상태 확인, 설정 팝업, 설치 폴더 열기, 문제 보고용 로그 저장 정도로 제한합니다.
- 오류 메시지는 친화적인 문장으로 변환해서 보여줍니다.

### 개발자 모드

`clientProfile = developer`일 때는 다음 기능을 유지합니다.

- 환경 ComboBox: `prod`, `dev`
- 채널 ComboBox: `stable`, `beta`, `dev`
- 플랫폼 ComboBox: `windows-x64`, `linux-x64`
- 버전 정책 ComboBox: `latest`, `exact`
- 업데이트, 실행, 검증/복구, 캐시 정리, 로그 저장, 로그 지우기, 다시 시도
- 상세 로그와 예외 정보 표시

## 프로젝트 UI 메타데이터

프로젝트 목록은 `launcher.config.json`의 `projects` 배열로 구성합니다.

```json
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
```

정렬 기준:

```text
1. isPinned = true 먼저
2. sortOrder 낮은 순
3. displayName 이름순
```

`visibleToProfiles`가 비어 있으면 모든 프로필에 표시합니다.

## 이미지 자산 규칙

기본 이미지 위치:

```text
publish/win-x64/assets/projects/{projectId}/thumbnail.png
publish/win-x64/assets/projects/{projectId}/hero.png
```

권장 크기:

```text
thumbnail.png: 480 x 320
hero.png: 1920 x 720
```

이미지가 없으면 GUI가 placeholder를 표시해야 합니다. 이미지 누락으로 런처가 실패하면 안 됩니다.

## 업데이트/상태 확인 지침

- 상태 확인은 manifest를 다운로드하고 실제 설치 파일의 SHA-256을 비교합니다.
- 상태는 최소한 `설치 필요`, `업데이트 가능`, `최신 상태`, `오류`, `확인 필요`로 구분합니다.
- 일반 사용자에게는 내부 단계명 대신 친화적인 메시지를 표시합니다.
- 개발자에게는 내부 단계명과 상세 로그를 표시할 수 있습니다.

## 로그 지침

- 일반 사용자에게는 중복/기술 로그를 최소화합니다.
- 개발자에게는 상세 로그와 stack trace를 보여도 됩니다.
- 로그 저장은 `logs/launcher-yyyyMMdd-HHmmss.log` 형식으로 저장합니다.

## 캐시/백업 지침

- 캐시는 `_config.StagingDir`입니다.
- 백업은 `_config.BackupDir`입니다.
- UI에서는 캐시/백업 용량을 표시합니다.
- 캐시 정리는 staging 폴더만 삭제/재생성합니다. backup 폴더는 자동 삭제하지 않습니다.

## 보안 지침

일반 사용자가 개발 버전을 받지 못하게 하는 것은 런처 UI만으로는 충분하지 않습니다.

서버에서도 반드시 다음 구조를 지킵니다.

```text
/catalogs/general/        공개
/projects/*/prod/stable/ 공개
/catalogs/developer/      인증 필요
/projects/*/dev/          인증 필요
```

catalog와 manifest는 ECDSA SHA-256 서명을 권장합니다.

## 문서 업데이트 규칙

다음 변경이 있으면 반드시 README.md와 관련 docs를 함께 수정합니다.

- config schema 변경
- GUI 노출 기능 변경
- catalog/manifest 형식 변경
- publish 스크립트 변경
- 서버 배포 구조 변경
- 일반 사용자/개발자 권한 정책 변경

## 주의사항

- 실행 파일은 Git에 커밋하지 않습니다.
- `publish/`, `bin/`, `obj/`, `.staging/`, `.backup/`, `app/`은 커밋하지 않습니다.
- Avalonia XAML에 `&` 문자를 직접 쓰지 않습니다. 필요하면 `&amp;`로 escape합니다.
- single-file publish에서 SkiaSharp 네이티브 DLL이 누락되지 않도록 `IncludeNativeLibrariesForSelfExtract=true` 옵션을 유지합니다.
