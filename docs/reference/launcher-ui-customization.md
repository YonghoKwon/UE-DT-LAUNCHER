# GUI 표시 설정·이미지 커스터마이징

> 참고 가이드 / 문서 점검 2026-09-28 / 코드 기준 0d12957. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

이 문서는 이름·이미지·정렬을 다룹니다. 서버 접근 권한, 서명키, 설치 루트는 Agent 보호 설정과 서버 정책이 담당합니다. [설정 레퍼런스](guide-03-launcher-usage.md)의 설정 선택 순서를 먼저 확인하세요.

## 표시 설정 예시

관리형 DistributionServer GUI 설정의 예시입니다. URL은 실제 서버로 바꾸고 Agent 관리 설정도 별도로 준비합니다. 로컬 이름·이미지 값은 초기/레거시 표시용이며, 통합 서버 목록을 읽은 뒤에는 서버 값이 우선합니다.

```json
{
  "deploymentMode": "managed-agent",
  "distributionServerUrl": "https://updates.example.com",
  "clientProfile": "developer",
  "projects": [
    {
      "projectId": "demo",
      "displayName": "Digital Twin Demo",
      "description": "디지털 트윈 시연 프로젝트",
      "thumbnailPath": "assets/projects/demo/thumbnail.png",
      "heroPath": "assets/projects/demo/hero.png",
      "engineVersion": "Unreal Engine",
      "technology": "Windows",
      "sortOrder": 0,
      "isPinned": true,
      "visibleToProfiles": ["general", "developer"]
    }
  ]
}
```

`projectId`는 서버 프로젝트 ID와 일치해야 합니다. 로컬 메타데이터는 서버 목록과 병합되며 권한 없는 프로젝트를 추가하는 수단이 아닙니다. DistributionServer 목록을 갱신하면 `displayName`, `heroPath`, `thumbnailPath`는 서버 값으로 교체됩니다. 통합 배포의 이름·이미지는 외부 `release.json`에서 지정하며 이미지는 내려받은 로컬 캐시로 표시합니다. 서버 이미지가 없으면 로컬 설정 이미지가 유지되는 것이 아니라 대체 이미지가 표시됩니다. 관리형 서버 이미지는 Agent가 권한·크기·해시를 확인해 IPC로 전달하고 GUI는 사용자 캐시에 저장합니다. GUI에 개인키를 공유하지 않습니다. DistributionServer에서 `installPath`로 버전별 설치 경로를 덮어쓰지 않습니다.

정렬은 고정(`isPinned`) 우선 → `sortOrder` 오름차순 → 표시 이름순입니다. `visibleToProfiles`가 비면 모든 프로필에 표시하며 로컬 필터일 뿐 보안 정책이 아닙니다.

## 이미지

권장 폴더는 GUI 설정 옆 `assets/projects/{projectId}/`입니다. `thumbnailPath`와 `heroPath`에 절대 경로 또는 GUI 설정 기준 상대 경로를 명시합니다.

| 이미지 | 권장 크기 | 표시 |
| --- | --- | --- |
| thumbnail.png | 480 × 320 | 프로젝트 카드 |
| hero.png | 1920 × 720 | 선택 프로젝트 배너 |

PNG, JPG/JPEG, WebP, 파일당 최대 20 MiB를 지원합니다. 확장자·시그니처를 확인하고 누락·잘못된 이미지·디코딩 실패에는 이니셜 기반 대체 이미지를 사용합니다. Hero는 `UniformToFill`로 잘리므로 주요 로고는 중앙 안전 영역에 배치하세요.

로컬 이미지와 서버 외부 `release.json`의 이미지 메타데이터는 구분합니다. 로컬 경로가 자동으로 서버에 업로드되지는 않습니다.

## 화면과 소스

`general`은 단순한 밝은 화면, `developer`는 배포·유지보수·진단을 추가한 어두운 화면입니다. 플랫폼은 두 프로필 모두 현재 OS에 고정됩니다.

실제 레이아웃은 `src/UeDtLauncher/Gui/MainWindowRefinedDashboard.cs`와 `MainWindowRefinedHelpers.cs`에서 구성하며 `MainWindow.axaml`은 최소 Window입니다. 공통 값은 `LauncherVisualTokens.cs`, 상태·노출 기능은 `LauncherDashboardViewModel.cs`, 이미지 검사는 `ProjectVisualResolver.cs`를 확인합니다. 예전 `MainWindow.axaml.cs` 경로를 편집 대상으로 사용하지 않습니다.

수정 후 general/developer 양쪽에서 긴 이름, 이미지 없음·손상, 작은 창, 허용 목록 없음, Agent 연결 실패를 확인합니다. 코드 빌드 성공만으로 GUI 배치·실제 앱 실행을 검증했다고 기록하지 않습니다.
