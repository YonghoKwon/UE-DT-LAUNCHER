# Launcher Refinement Notes

이 문서는 `feature/launcher-production-hardening` 브랜치에서 추가로 다듬은 런처 개선 사항을 정리합니다.

## 반영된 개선 사항

### 1. Legacy UI 정리 방향

실제 빌드 대상 UI는 `src/UeDtLauncher/Gui/MainWindowRefinedDashboard.cs`입니다.

`UeDtLauncher.csproj`에서는 이전 실험용 UI 파일들을 빌드에서 제외합니다.

```xml
<Compile Remove="Gui\MainWindow.axaml.cs" />
<Compile Remove="Gui\MainWindowDashboard.cs" />
<Compile Remove="Gui\MainWindowProductDashboard.cs" />
<Compile Remove="Gui\MainWindowProductDashboardV2.cs" />
<Compile Remove="Gui\MainWindowModeFixedDashboard.cs" />
<Compile Remove="Gui\MainWindowStructuredDashboard.cs" />
```

파일은 현재 히스토리 비교를 위해 남겨두었지만, 안정화 후 삭제해도 됩니다.

### 2. exact 버전 선택 UI

개발자 모드에서 `버전`을 `exact`로 선택하면 `요청 버전` 항목이 표시됩니다.

카탈로그에서 현재 프로젝트/환경/채널/OS에 맞는 버전 목록을 찾을 수 있으면 ComboBox로 표시하고, 찾지 못하면 직접 입력 TextBox를 표시합니다.

### 3. 일반 사용자 오류 대응

일반 사용자 모드에서 오류가 발생하면 오류 팝업을 표시합니다.

팝업에는 다음 버튼이 포함됩니다.

```text
다시 시도
로그 ZIP 저장
닫기
```

### 4. Catalog 기반 프로젝트/릴리스 탐색 준비

`CatalogSnapshotService`를 추가했습니다.

```text
src/UeDtLauncher/Gui/CatalogSnapshot.cs
```

이 서비스는 `catalogUrl`을 읽어서 현재 OS와 clientProfile에서 접근 가능한 프로젝트/릴리스 목록을 UI에 제공하는 역할을 합니다.

현재 UI에서는 카탈로그 새로고침 버튼으로 카탈로그를 읽고, 카탈로그에만 존재하는 프로젝트가 있으면 UI 프로젝트 목록에 병합합니다.

### 5. 개발자 실행 전 확인

개발자 모드에서 `실행` 버튼을 누르면 실행 전 확인 팝업이 표시됩니다.

팝업에는 다음 정보가 표시됩니다.

```text
프로젝트
가동/개발
채널
버전
OS
```

### 6. 릴리스 노트 표시

카탈로그 release에 `notes`가 있으면 hero 영역과 개발자 릴리스 정보 영역에 표시합니다.

release notes가 없으면 프로젝트 설명을 fallback으로 표시합니다.

### 7. 백업 정리

개발자 모드에 `백업 정리` 버튼을 추가했습니다.

이 버튼은 `backupDir` 폴더를 삭제 후 다시 생성합니다.

### 8. 로그 ZIP 내보내기

기존 로그 저장 기능을 확장해 `로그 ZIP` 기능을 추가했습니다.

로그 ZIP은 아래 경로에 생성됩니다.

```text
publish\win-x64\logs\launcher-logs-yyyyMMdd-HHmmss.zip
```

## 다음 개선 후보

아직 남은 고도화 후보는 다음과 같습니다.

```text
1. legacy UI 파일 실제 삭제
2. CatalogSnapshotService에 서명 검증 연결
3. 다운로드 중 현재 파일명/용량/파일 수를 별도 UI로 표시
4. UI 파일을 Sidebar / Actions / StatusPanel 단위로 분리
5. 백업 정리 정책을 보관 개수/보관 기간 기준으로 변경
6. catalog 새로고침을 앱 시작 시 자동 수행할지 옵션화
```
