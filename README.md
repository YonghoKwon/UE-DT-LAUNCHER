# UE-DT Launcher

2차 안전성 구현: MSI payload 선서명·검증과 runtime-host 기반 실행 중 변경 차단을 추가했습니다. [종료 후 재시도·수동 복구](docs/reference/runtime-safety.md), [실제 검증과 남은 조건](docs/reference/deployment-safety-validation.md)을 확인하세요. OPS-08은 50%, OPS-09는 75%이며 회사 운영 승인은 별도입니다.

추가 구현: 사내 HTTP 요청 서명·PC 인증키 보호·Agent 이미지 전달·설정 생성기를 제공합니다. [최초 등록 명령](docs/reference/intranet-auth.md)과 [검증·남은 조건](docs/reference/intranet-auth-validation.md)을 확인하세요. HTTP는 암호화되지 않으며 회사 운영 승인은 별도입니다.

Unreal Engine Windows/Linux 패키징 프로그램을 사내 서버에 등록하고, 허용된 PC에서 설치·업데이트·실행하는 .NET 8 / Avalonia 런처입니다.

문서 점검: **2026-09-28**, 구현 기준: `codex/launcher-deployment-safety` (시작점 `548cab8`). 로컬 브랜치 기준이며 main 반영·운영 배포 완료를 뜻하지 않습니다.

## 프로젝트 목표와 처음 읽을 안내

최종 목표는 **Unreal Engine DT 프로그램의 패키징 결과를 안전하게 배포하고, 회사에서 안정적으로 설치·업데이트·실행·복구할 수 있는 배포 시스템**입니다. 목표는 확정됐지만 회사 운영 승인 조건을 모두 충족한 상태는 아닙니다.

```mermaid
flowchart LR
    package["개발자: ZIP와 외부 JSON"] --> inspect["서버: 자동 검사"]
    inspect --> approve["관리자: 승인"]
    approve --> publish["서버: 서명과 공개"]
    publish --> launch["허용된 PC: 설치와 실행"]
```

**어떤 명령을 어디서 실행하는지 알고 싶다면 [기능 지도·단계별 실행 안내](docs/reference/feature-workflow.md)부터 읽으세요.** 최초 준비 A, 새 버전 배포 B, 사용자 실행 C, 문제 해결 D로 나누고 각 단계의 담당자·명령·정상 결과를 제공합니다. 배포 서버가 사용자 PC에 자동 설치를 밀어 넣는 방식은 아닙니다.

## 관리 문서 4개

| 문서 | 관리 내용 |
|---|---|
| [README](README.md) | 현재 기능·사용 흐름·시작 방법 |
| [AGENTS](AGENTS.md) | 개발·검증·커밋·문서 갱신 규칙 |
| [개선 진행 현황](IMPROVEMENTS.md) | 완료 이력·항목별 진행률·남은 성능/사용자/UI/보안/운영 보완 |
| [최종 프로젝트 목표](PROJECT_GOALS.md) | 확정한 DT 배포 시스템 목표·회사 운영 승인 조건·미정 세부 정책 |

개선 문서는 남은 항목만 모은 것이 아니라 완료·부분 진행·대기를 함께 관리합니다. 퍼센트는 **추가 보완의 체크포인트 진척**이며, 기존 기능 구현도나 회사 운영 승인율이 아닙니다. 최신 집계는 해당 문서 한 곳에서 확인합니다.

그 외 자료는 [참고 문서 모음](docs/reference/README.md)에 있습니다. 상세 명령·설정·검증 기록은 참고 문서에, 과거 자료는 그 아래 `archive/`에 보존합니다. 과거 서버 절차를 신규 설치 지침으로 사용하지 않습니다.

## 현재 구현

최신 안전성 작업: Windows/WSL 자동화 각 347개, MSI 실패 gate 계약 11개 통과. publish된 일반/개발자 GUI와 CLI·console Agent, 부모 선종료/자식 유지·host 장애·Agent 재시작을 확인했습니다. GUI에서는 상태 표시를, 실제 설치·실행·repair는 CLI E2E로 확인했습니다. [안전성 결과](docs/reference/deployment-safety-validation.md). 이전 인증 부하/WSL nginx·메모리 제약과 실제 LocalService·회사 RHEL/UE 미검증은 [인증 기록](docs/reference/intranet-auth-validation.md)에 남아 있습니다.

| 영역 | 내용 |
|---|---|
| 접수 | 업로드 폴더별 ZIP + 외부 release.json, 크기·SHA-256·안전한 ZIP 검사, SQLite 작업 기록 |
| 게시 | 관리자 승인, 디렉터리 자동 생성, Manifest·서명, 완료 전 비공개, 중단 게시 재개 |
| 권한 | 실제 IP/CIDR + PC 요청 서명(기존 HTTPS는 Bearer), 프로젝트·환경·채널·선택적 버전 제한, 기본 거부 |
| 전송 | 인증된 목록·Manifest·이미지·파일·Range, 서명·해시 검증, 재시도·이어받기 |
| 설치 | 정확한 릴리스, 버전별 설치·상태·잠금 분리, 실행 중/불명 상태의 update·repair·rollback·transaction 복구 차단 |
| 실행 | 사용자 세션 runtime-host, Windows Job / Linux x64 subreaper, 표준 후손 종료까지 추적, 자동 kill 없음 |
| 일반 화면 | 자동 상태 확인, 상태별 실행 버튼, 친화적 오류·문제 해결, 이미지/fallback |
| 개발자 화면 | 해당 PC에 허용된 배포 선택, 상세 진행·진단·유지보수 |
| 운영 | Windows/Linux Agent·IPC·CLI, 진단 내보내기, 무인 서비스 모드, MSI/RPM 제작 구성 |

GUI의 general/developer는 표시 정책이지 다운로드 권한이 아닙니다. 기존 HTTPS/Bearer와 명시적인 schema 3 사내 HTTP/요청 서명을 지원하며, 둘 다 Metadata 서명·해시·권한 검증을 유지합니다. HTTP/Bearer나 무인증으로 자동 후퇴하지 않습니다. nginx 뒤 API는 loopback에만 바인딩하고 공개 정적 경로와 혼합하지 않습니다.

일반 GUI는 자동 점검만 하며 설치는 사용자 클릭 후 수행합니다. 무인 서비스 자동 업데이트와 구분합니다. 관리형 런처 자체 갱신은 MSI/RPM, 게임 콘텐츠 갱신은 Agent 책임입니다.

## 실행 중 변경과 구형 클라이언트

프로그램이 실행 중이면 정상 종료 후 런처에서 다시 확인하세요. GUI를 닫아도 프로그램은 종료되지 않습니다. 추적 불명 상태는 관리자 점검과 명시적 정지 확인이 필요하며 PID 파일 삭제로 우회하지 않습니다. 런처와 Agent를 함께 갱신하세요. 구형 IPC v1의 조회는 유지하지만 실행 추적 capability 없는 변경 요청은 거부합니다. 기존 직접 EXE 바로가기는 관리자가 이전하고, 새 바로가기는 정확한 버전을 선택한 런처를 호출합니다. [상태별 명령](docs/reference/runtime-safety.md)

## 처음 준비할 것

1. Linux 서버에 DistributionServer·nginx·systemd 설정. 신규 사내 HTTP는 request-signature-v1을 명시합니다.
2. 서버 서명 개인키와 PC별 IP/배포 권한 등록. 공개키만 PC에 배포.
3. PC별 개인키를 보호 저장하고 공개키만 서버에 등록합니다. 기존 HTTPS/Bearer 환경은 기존 토큰 절차를 유지합니다.
4. PC에 런처·Agent 설치, sample-config로 보호된 운영 설정을 생성하고 doctor로 확인합니다.
5. 런처 옆 설정에서 general/developer 화면 선택.

신규 사내 HTTP 명령은 [요청 서명 안내](docs/reference/intranet-auth.md), 기존 HTTPS/Bearer와 공통 게시 과정은 [통합 운영 가이드](docs/reference/distribution-workflow.md)를 따릅니다. 예시 IP·계정·공개키를 실제 값으로 바꾸세요.

## 새 버전 배포

| 순서 | 담당 | 작업 |
|---:|---|---|
| 1 | 개발자 | Windows/Linux ZIP을 각각 준비 |
| 2 | 개발자 | release-metadata로 ZIP 옆 외부 JSON 생성 |
| 3 | 개발자 | incoming/새업로드ID/에 두 파일을 .uploading 이름으로 전송 후 최종 이름으로 변경 |
| 4 | 서버 | 파일 쌍·해시·ZIP 검사 후 승인 대기 |
| 5 | 관리자 | list / inspect 확인 후 approve |
| 6 | 서버 | 서명·디렉터리 생성 후 배포 목록 공개 |
| 7 | 사용자 | 런처에서 설치/업데이트 후 실행 |

```text
UeDtLauncher release-metadata --zip Windows.zip --project-id demo --version 1.2.0 --platform windows-x64 --payload-root Windows --entry-point Demo.exe --output release.json
```

payloadRoot는 ZIP 내부 프로그램 루트, entryPoint는 그 기준 경로입니다. ZIP을 변경하면 JSON도 다시 생성합니다. [업로드·승인 상세](docs/reference/guide-02-publish-package.md)

서버 등록 위치는 `releases/<project>/<environment>/<channel>/<version>/<platform>/`입니다. 클라이언트는 필요한 개별 파일을 받습니다. 새 버전은 별도 설치 경로를 유지하면서 같은 배포 구분의 최근 설치 파일을 검증 후 복사할 수 있습니다. 원본을 공유하는 hard link나 공용 콘텐츠 캐시는 사용하지 않습니다.

## 성능 설정과 현재 결과

- 다운로드·해시 기본 동시 처리 수는 각각 2개입니다. 설정으로 순차 처리(1개)로 되돌릴 수 있습니다.
- 서명된 배포의 새 버전 설치는 최근 3개 설치 후보에서 동일 파일을 재사용합니다. repair는 기존 다운로드 복구를 유지합니다.
- 서버 정책은 매 요청 다시 읽고 토큰도 매 요청 검사합니다. Catalog 서명 응답을 캐시하지 않습니다.
- 접수 worker는 기본 1개, 선택적으로 2개입니다. 작업별 잠금·활성 임시 폴더 보호·진행/디스크 예상량을 제공합니다.

Windows 재현 시험에서 작은 파일 최초 설치는 중앙값 10.96초→5.98초, 다음 버전 설치는 11.01초→3.29초였고 콘텐츠 전송량은 90% 줄었습니다. 서버 30개 연결의 혼합 API p95는 833.44ms→81.23ms, 측정 요청 실패는 32→0이었습니다. **10개 연결의 혼합 p95는 5.43ms→24.66ms로 악화되어 PERF-03은 부분 완료**입니다. 회사 성능 보장이나 운영 배포 승인이 아닙니다. [설정·재현·한계](docs/reference/performance-validation.md)

## 빌드·검증

.NET 8 SDK, 저장소 루트 기준:

```text
dotnet restore UeDtLauncher.sln
dotnet build UeDtLauncher.sln -c Release --no-restore
dotnet test src/UeDtLauncher.Tests/UeDtLauncher.Tests.csproj -c Release --no-build
dotnet publish src/UeDtLauncher/UeDtLauncher.csproj -c Release -r win-x64 --self-contained true -o publish/client-win-x64
dotnet publish src/UeDtLauncher.Agent/UeDtLauncher.Agent.csproj -c Release -r win-x64 --self-contained true -o publish/agent-win-x64
dotnet publish src/UeDtLauncher.DistributionServer -c Release -r linux-x64 --self-contained true -o publish/distribution-server
```

Linux 클라이언트/Agent는 `-r linux-x64`로 생성합니다. 네이티브 의존성이 있으므로 출력 폴더 전체를 배치합니다. 설치·실행은 [클라이언트 가이드](docs/reference/guide-03-launcher-usage.md)를 따릅니다.

## 검증 범위와 제약

2026-09-12 [기존 실행 기록](docs/reference/distribution-validation.md): Windows 210/210, WSL Ubuntu 210/210, Release 경고·오류 0. 테스트 프로그램으로 HTTPS 배포와 Windows GUI/Agent·Linux CLI 설치·실행을 확인했습니다.

2026-09-28 성능 작업: Windows/Linux 각각 전체 293개 테스트 통과, Python 측정 도구 계약 테스트 16개 통과. Release build/publish 및 실제 Windows 일반·개발자 GUI/Agent, Linux CLI/Agent 실행을 확인했습니다. Linux nginx HTTPS E2E의 작은 파일은 통과했지만 WSL1 nginx의 1MiB 응답 중단이 관측되어 큰 파일 GUI 검증은 격리된 Windows HTTPS 프록시로 분리했습니다. 실제 회사 RHEL·UE 패키지·IP/CA·설치본 수명주기는 별도 검증해야 합니다. 코드서명 없는 개발 산출물을 운영용 서명 제품으로 배포하지 않습니다.

- latest는 같은 환경/채널/OS에서 마지막 승인된 판이며 최대 버전 번호가 아닙니다.
- 신규 프로젝트 게시가 PC 권한을 자동 부여하지 않습니다.
- cleanup은 임시 작업 폴더 대상이며 공개 버전·참조 원본은 삭제하지 않습니다.
- 기존 설치 이전은 명시적 import-install입니다. UE 사용자 데이터는 실제 저장 경로에 맞춰 별도 보존합니다.
- 회사 백엔드 API는 인터페이스만 있고 현재는 파일 정책 구현입니다.
- 기설치 앱 원격 삭제·실행 금지는 범위 밖입니다.
- Agent 설치만으로 정기 업데이트가 시작되지는 않습니다. 현재 managed service-run은 한 회차 실행이며 주기 운영은 추가 설계가 필요합니다.

운영 전 우선 보완: MSI 내부 EXE 서명 순서, 동명 프로세스 식별, Linux credential 소유권. [보완 목록](IMPROVEMENTS.md)의 OPS-08/09·SEC-03을 확인하세요. 기존 테스트 통과만으로 이 항목들이 해결됐다고 판단하지 않습니다.
