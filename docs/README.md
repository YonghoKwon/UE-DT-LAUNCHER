# UE-DT Launcher 문서 — 여기서 시작하세요

UE-DT Launcher는 Unreal Engine 패키징 결과물을 **리눅스 업데이트 서버**에 올려두고, **클라이언트 PC**(일반 PC·픽셀 스트리밍 서버)가 실행 전에 자동으로 최신 파일만 받아 실행하게 하는 경량 런처입니다.

## 어떤 문서를 봐야 하나요? (역할별)

| 당신의 역할 | 읽을 문서 |
| --- | --- |
| **서버 담당자** — 업데이트 서버를 구축한다 | [guide-01 — 리눅스 서버 세팅](guide-01-linux-server-setup.md) |
| **배포 담당자** — 빌드를 서버에 올린다 | [guide-02 — 패키징 파일 업로드(릴리스)](guide-02-publish-package.md) |
| **사용자/운영자** — 런처를 설치·실행한다 | [guide-03 — 런처 사용법](guide-03-launcher-usage.md) · [화면 사용법](launcher-user-guide.md) |
| **픽셀 스트리밍/무인 서버** | [service-mode — 무인 자동 업데이트](service-mode.md) |
| **런처 화면 꾸미기** | [launcher-ui-customization](launcher-ui-customization.md) |

처음 전체를 구축한다면 **guide-01 → guide-02 → guide-03 순서**로 보면 됩니다.

## 전체 흐름 한눈에

```text
[Windows 빌드 PC]            [리눅스 업데이트 서버]              [클라이언트 PC]
UE 패키징 → ZIP 압축  ──업로드──▶ 압축 해제 → manifest 생성    런처 실행 → 바뀐 파일만
                                  → catalog 갱신 (nginx 서빙)    다운로드 → 앱 실행
  guide-02                        guide-01(서버) + guide-02       guide-03
```

## 5분 빠른 시작 (서버가 이미 있다고 가정)

> 서버가 아직 없으면 [guide-01](guide-01-linux-server-setup.md)부터 하세요. 아래는 "이미 구축된 서버에 클라이언트를 붙여 한 번 돌려보는" 최소 흐름입니다.

**1) 런처 빌드 (인터넷 되는 Windows, .NET 8 SDK 필요)**
```powershell
git clone <저장소 URL> UE-DT-LAUNCHER ; cd UE-DT-LAUNCHER
.\scripts\publish-win-x64.ps1          # → publish\win-x64\UeDtLauncher.exe
```

**2) 설정 파일 작성** — `publish\win-x64\launcher.config.json`
```json
{
  "catalogUrl": "http://<서버IP>/catalogs/general/catalog.json",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod", "channel": "stable", "versionPolicy": "latest",
  "installDir": "app"
}
```

**3) 실행**
```powershell
cd .\publish\win-x64
.\UeDtLauncher.exe run --config launcher.config.json --no-launch   # 업데이트만(검증)
.\UeDtLauncher.exe                                                 # GUI로 실행
```
`[Complete] Update completed.`가 나오면 성공입니다. 안 되면 guide-03의 "문제 해결"을 보세요.

> 모드 강제 지정: `--gui`(GUI 강제) / `--cli`(CLI 강제 — 기본 `run`으로 매핑). 둘을 동시에 주면 오류(종료 코드 2). 헤드리스 리눅스 서버에서는 `--cli --config …` 또는 `run`/`service`를 쓰세요(guide-03 4-A절).
> 대화형 터미널에서 `run`을 돌리면 한 줄짜리 **진행 바**(전체%·파일 n/m·속도·받은/전체 용량)가 보이고, 출력을 파일/서비스로 리다이렉트하면 평문 줄로 바뀝니다(guide-03 3-A절). 파일 로그는 항상 동일.

**서버 운영자용 점검·보안 명령**
```bash
# 카탈로그에 등록된 릴리스를 표로 확인 (점검용)
UeDtLauncher list-releases --catalog catalog.json [--project <id>]
# 프로젝트별 IP 허용목록(JSON) → nginx allow/deny location 블록 생성 (서버에서 강제)
UeDtLauncher generate-nginx-acl --allowlist project-ip-allowlist.json [--output acl.conf]
```
> 빌드를 메뉴로 안내받으며 올리고 싶으면 서버에서 **대화형 위저드** `tools/publish-wizard.sh` 를 쓰세요(guide-02 방법 E).

> ⚠️ 로컬에서 테스트 서버를 띄울 때 `python -m http.server`(단일 스레드)는 런처가 중간에 멈출 수 있습니다. 멀티스레드로:
> `python3 -c "from http.server import ThreadingHTTPServer,SimpleHTTPRequestHandler; ThreadingHTTPServer(('0.0.0.0',8080),SimpleHTTPRequestHandler).serve_forever()"`
> (실서버 nginx는 문제없습니다.)

## 문서 분류

**핵심(이 폴더 루트)**
- `guide-01-linux-server-setup.md` — 서버 구성 **정본**
- `guide-02-publish-package.md` — 릴리스 퍼블리시
- `guide-03-launcher-usage.md` — 런처 설정/CLI 레퍼런스 + 문제 해결
- `launcher-user-guide.md` — 런처 GUI 화면 사용법
- `launcher-ui-customization.md` — 프로젝트 이미지/목록 등 UI 커스터마이징
- `service-mode.md` — 무인 서버용 자동 업데이트(systemd/NSSM)

**보조·레거시 (`reference/`)**
- `reference/release-publish-scripts.md` — 퍼블리시 스크립트 상세
- `reference/redhat-distribution-server.md`, `reference/company-rhel84-dt-update-server.md`, `reference/offline-linux-update-server-setup.md` — 구 서버 구성 문서(정본은 guide-01)
- `reference/developer-and-general-launcher-usage.md` — 구 통합 사용 가이드
- `reference/netmarble-launcher-analysis.md`, `reference/launcher-refinement-notes.md` — 설계/내부 노트
