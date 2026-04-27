# Netmarble Launcher 분석 기반 UE-DT-LAUNCHER 개발 방향

## 요약

업로드된 Netmarble Launcher는 Electron + Vue 기반의 상용 게임 플랫폼 런처 구조에 가깝다. 단순히 게임 실행 파일을 실행하는 프로그램이 아니라, 런처 자체 업데이트, 게임 설치/업데이트/삭제, 계정/웹뷰, 배너, 상태 페이지, Windows registry, shortcut, protocol scheme, fragment download, 압축 해제, 실패 복구 등을 포함한다.

UE-DT-LAUNCHER의 1차 목표는 Netmarble Launcher 전체를 복제하는 것이 아니라, Unreal Engine 패키징 결과물을 Windows/Linux PC에 안전하게 배포하고 최신 버전으로 갱신한 뒤 실행하는 것이다.

## Netmarble Launcher에서 참고한 핵심 요소

1. 업데이트 대상과 런처 자체를 분리한다.
2. 다운로드는 임시 위치에 먼저 받고 검증 후 실제 설치 폴더에 반영한다.
3. 실패 가능성이 높은 구간을 명확히 나눈다.
   - manifest 다운로드
   - 파일 다운로드
   - checksum 검증
   - 압축 해제 또는 파일 이동
   - 실행
4. 사용자가 설치된 앱을 복구할 수 있는 repair 흐름을 둔다.
5. 상용 서비스에서는 UI 진행률, pause/resume, disk full, file lock, network failure 같은 상태를 명확히 보여준다.

## 이번 브랜치의 구현 범위

이번 `feature/pc-distribution-launcher` 브랜치에서는 PC 배포용 런처의 최소 실사용 코어를 구현한다.

- manifest 기반 파일 업데이트
- 파일별 SHA-256 검증
- 변경/누락 파일만 다운로드
- `.staging` 다운로드 후 검증
- `.backup` 백업 후 설치 폴더 반영
- 실패 시 backup 기반 rollback
- HTTP Range 기반 이어받기 시도
- retry 처리
- repair 모드
- Windows/Linux entry point 실행
- manifest 생성 명령
- sample config 생성 명령

## 아직 구현하지 않은 항목

아래는 이후 단계에서 구현하는 것이 좋다.

- GUI 런처
- manifest 서명 검증
- ZIP/7z package 단위 업데이트
- 런처 자기 자신 업데이트
- 다운로드 pause/resume UI
- 설치/삭제/바로가기/registry 연동
- 로그 파일 저장 및 UI 노출
- Windows service 또는 tray agent
- Unreal Pixel Streaming 서버 선택/상태 표시

## 설계 원칙

UE 패키징 결과물은 파일 수와 용량이 크기 때문에, 1차 구조는 파일 단위 manifest 업데이트를 사용한다. Unreal 프로젝트에서는 `.pak`, `.ucas`, `.utoc`, executable, dll, so 파일을 manifest에 포함시키고, 바뀐 파일만 다운로드한다.

향후 패키징 파일 수가 많아지면 Netmarble Launcher처럼 fragment 또는 archive 기반 업데이트로 확장한다.
