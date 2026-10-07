# GUI 표시 설정·이미지 커스터마이징

> 현재 가이드 / 문서 점검 2026-10-03 / codex/client-editions-ux-acceptance. 일반/개발자 종류는 별도 빌드로 고정됩니다. 새 마우스 적용은 세션 Disc/입력 거부로 미실행이며 문서 점검은 DPI/음성 수용이 아닙니다. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

이 문서는 이름·이미지·정렬을 다룹니다. 서버 접근 권한, 서명키, 설치 루트는 Agent 보호 설정과 서버 정책이 담당합니다. [설정 레퍼런스](guide-03-launcher-usage.md)의 설정 선택 순서를 먼저 확인하세요.

## 표시 설정 예시

관리형 DistributionServer GUI 설정의 예시입니다. URL은 실제 서버로 바꾸고 Agent 관리 설정도 별도로 준비합니다. 로컬 이름·이미지 값은 초기/레거시 표시용이며, 통합 서버 목록을 읽은 뒤에는 서버 값이 우선합니다.

```json
{
  "deploymentMode": "managed-agent",
  "distributionServerUrl": "https://updates.example.com",
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
| thumbnail.png | 480 × 320 | 서버/캐시 메타데이터 지원; 현행 프로젝트 목록은 이름 중심 |
| hero.png | 1920 × 720 | 선택 프로젝트 요약의 작은 이미지(좁은 창/고대비에서는 숨김) |

PNG, JPG/JPEG, WebP, 파일당 최대 20 MiB를 지원합니다. 확장자·시그니처를 확인하고 누락·잘못된 이미지·디코딩 실패에는 이니셜 기반 대체 이미지를 사용합니다. Hero는 `UniformToFill`로 잘리므로 주요 로고는 중앙 안전 영역에 배치하세요.

로컬 이미지와 서버 외부 `release.json`의 이미지 메타데이터는 구분합니다. 로컬 경로가 자동으로 서버에 업로드되지는 않습니다.

## 브랜드·접근성

일반 화면은 밝은 Surface, 개발자는 다크 Surface를 사용하며 POSCO BLUE `#05507D`를 주 버튼에 적용합니다. LIGHT BLUE `#00A5E5`는 흰색 본문 글자의 배경으로 쓰지 않습니다. 공식 로고 파일·출처·SHA-256은 `Assets/Branding/`에서 관리합니다. 앱 구현용 사용과 회사 최종 CI/브랜드 승인 여부는 구분합니다.

글자 크기·앱 고대비는 서버 config에 추가하지 않습니다. Windows `%LOCALAPPDATA%/UE-DT Launcher/ui-preferences.json`, Linux `$XDG_CONFIG_HOME/UE-DT Launcher/ui-preferences.json`(미지정 시 `~/.config`)에 사용자별 저장합니다. 허용 배율은 1/1.25/1.5/2이며 손상 설정은 기본값으로 읽습니다. 키·토큰·Agent 설정과 분리하며 설정 저장은 비동기로 처리합니다.

유지보수·도움말 Expander와 상세·로그 Tab 제목에도 같은 배율을 적용합니다. 큰 글자의 긴 상태/오류는 작업 상세 안에서 스크롤하며 주 버튼 접근을 유지합니다. 실제 OS DPI 시험은 창 크기나 headless DIP 시험과 다르므로 별도 기록합니다. 2026-09-29 사용자 합의에 따라 UI-01은1920×1080·OS 배율100%·앱 글자100%·고대비 끔의 확인된 범위에서100%로 수용했습니다. 내레이터 음성은 별도 미검증입니다.

> **추가 테스트 필요:** 1366×768·1280×720 등 다른 해상도. OS 배율125/150%, 큰 글자·고대비 최악 조건도 이번 검증 완료 범위에 포함하지 않으며 후속으로 시험합니다.

## 화면과 소스

일반 빌드는 밝은 화면, 개발자 빌드는 배포·유지보수·진단이 있는 어두운 화면입니다. 설정으로 빌드 종류를 바꾸지 않습니다. 플랫폼은 현재 OS에 고정됩니다.

실제 레이아웃은 `src/UeDtLauncher/Gui/MainWindowEnterprise.cs`, 대화창은 `MainWindowAccessibility.cs`, 작업 피드백은 `MainWindowFeedback.cs`에서 구성하며 `MainWindow.axaml`은 최소 Window입니다. 공통 값은 `LauncherVisualTokens.cs`, 상태·노출 기능은 `LauncherDashboardViewModel.cs`, 이미지 검사는 `ProjectVisualResolver.cs`를 확인합니다. 예전 `MainWindow.axaml.cs` 경로를 편집 대상으로 사용하지 않습니다.

수정 후 general/developer 양쪽에서 긴 이름, 이미지 없음·손상, 작은 창, 허용 목록 없음, Agent 연결 실패를 확인합니다. 코드 빌드 성공만으로 GUI 배치·실제 앱 실행을 검증했다고 기록하지 않습니다.
