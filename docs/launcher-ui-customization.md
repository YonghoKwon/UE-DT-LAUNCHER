# 런처 UI 커스터마이징 가이드

이번 GUI는 일반 사용자와 개발자 프로필에 따라 다른 화면을 보여줍니다.

## 화면 모드

`launcher.config.json`의 `clientProfile` 값에 따라 화면이 나뉩니다.

```json
{
  "clientProfile": "general"
}
```

일반 사용자 화면은 밝은 테마이며, 실행/설정/상태 확인 위주의 단순한 상업용 런처 화면으로 표시됩니다.

```json
{
  "clientProfile": "developer"
}
```

개발자 화면은 어두운 테마이며, 환경/채널/플랫폼/버전 정책, 업데이트, 복구, 로그, 매니페스트 검증 같은 개발자용 기능을 보여줍니다.

## 프로젝트 이미지 디렉터리

기본 이미지 위치는 다음과 같습니다.

```text
publish/win-x64/assets/projects/{projectId}/thumbnail.png
publish/win-x64/assets/projects/{projectId}/hero.png
```

예시:

```text
publish/win-x64/
  UeDtLauncher.exe
  launcher.config.json
  assets/
    projects/
      ue-dt-simulator/
        thumbnail.png
        hero.png
      ue-dt-client/
        thumbnail.png
        hero.png
```

이미지가 없으면 런처가 자동으로 gradient placeholder를 표시합니다.

권장 이미지 크기:

| 파일 | 권장 크기 | 용도 |
|---|---:|---|
| `thumbnail.png` | 480 x 320 | 왼쪽 프로젝트 카드 |
| `hero.png` | 1920 x 720 | 메인 히어로 배너 |

## 프로젝트 목록 등록

`launcher.config.json`에 `projects` 배열을 추가합니다.

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
      "status": "개발 중",
      "installPath": "app",
      "engineVersion": "Unreal 5.4",
      "technology": "Windows"
    }
  ]
}
```

`thumbnailPath`와 `heroPath`는 절대 경로 또는 `launcher.config.json`이 있는 폴더 기준 상대 경로를 사용할 수 있습니다.

지원 형식은 PNG, JPG/JPEG, WebP이며 파일당 최대 크기는 20MB입니다. 런처는 파일 크기와 이미지 시그니처를 확인하고 실제 디코딩에 실패해도 프로젝트 이니셜 기반 네이비·브랜드 패턴으로 자동 대체합니다.

Hero 이미지는 화면 비율에 맞게 `UniformToFill`로 자르고 텍스트 영역에 네이비 overlay를 적용합니다. 따라서 중요한 텍스트나 로고는 이미지 가장자리보다 중앙 안전 영역에 배치하는 것을 권장합니다.

일반 사용자 화면은 Hero 아래에 상태/버전 요약, 실행, 진행 단계를 배치합니다. 개발자 화면은 동일한 디자인 토큰의 다크 테마에서 명령을 `배포`, `유지보수`, `진단`으로 그룹화합니다.

## 일반 사용자에게 보이는 기능

일반 사용자 모드에서는 다음 위주로 보입니다.

- 프로젝트 목록
- 프로젝트 대표 이미지
- 설치 위치
- 사용자 유형
- 실행 버튼
- 설정 버튼
- 간단한 상태 메시지

개발자용 버튼인 복구, 매니페스트 검증, 캐시 정리, 환경/채널/플랫폼 선택 정보는 숨겨집니다.

## 개발자에게 보이는 기능

개발자 모드에서는 다음이 추가로 보입니다.

- 환경 정보
- 채널 정보
- 플랫폼 정보
- 버전 정책
- 업데이트 버튼
- 복구 버튼
- 로그 보기
- 매니페스트 검증
- 캐시 정리
- 릴리스 정보
- 상세 실행 로그

## 설정 예시 파일

예시 설정은 아래 파일을 참고하세요.

```text
examples/configs/general-windows-launcher.config.json
examples/configs/developer-windows-launcher.config.json
examples/configs/developer-linux-launcher.config.json
```

실제 테스트 시에는 예시 파일을 publish 폴더의 `launcher.config.json`으로 복사해서 사용하면 됩니다.

Windows 예시:

```powershell
Copy-Item examples\configs\general-windows-launcher.config.json publish\win-x64\launcher.config.json
```

그 다음 이미지 파일을 아래 위치에 넣습니다.

```text
publish/win-x64/assets/projects/ue-dt-simulator/thumbnail.png
publish/win-x64/assets/projects/ue-dt-simulator/hero.png
```

실행:

```powershell
.\publish\win-x64\UeDtLauncher.exe
```
