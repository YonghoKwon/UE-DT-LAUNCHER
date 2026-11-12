# SPRINT-01 CLI 품질·배포 관리 편의 검증

2026-11-12 KST / Windows / SDK9.0.308 / 실행 계정 ho2708. 회사 RHEL·실제 UE·서비스 설치·운영 계정 인수가 아니다. 원시 fixture/로그/키/DB는 커밋하지 않고 [정제 프로세스 요약](sprint-01-process-evidence.json)과 [현재 자동화 입력](sprint-01-runtime.json)만 보존한다.

## 소스와 검증 범위

기준은 cef9f53c04bebf14f7aa633de6dbed1da733b9da, 기능·서식·UTF-8 보완 후 고정 게시 소스는 **e24aa937073b4e67a88d392f26384681fd19fb2f**이다. 이후 W8은 문서·대장·진행표 생성/집계 도구만 변경하며 제품 소스는 바꾸지 않는다. 게시 당시 Git의 제품/도구 diff는 비어 있었고 sourceDiffSha256은 빈 입력의 SHA-256이다. 게시 파일은 새 고유 경로에 만들었고 과거 후보를 덮어쓰지 않았다. runtime 입력의 productSourceHash는 정렬한 Git 소스 파일 hash 사전에서 계산한 식별값이며 snapshot publication 도구를 실행했다는 뜻은 아니다.

| 역할 | 고정 게시 EXE SHA-256 |
|---|---|
| 일반 CLI/launcher | ec7fec962b283bdf27e2771f15a61aa61c4d8e316ac4bcd8b4fdfe286ebefa87 |
| console Agent | 45592e4846244f34a95cdf02e06da964c03493e96a06aa2f2aa57b2758176b4b |
| DistributionServer | 3b0baa0cf09d03b345d31a30866509c2227ee7df7c6d34bc0f53ba7a71f62f92 |

양 에디션의 headless UI 검사는 기존 테스트의 분기로 수행했다. 이번 Developer EXE의 별도 publish와 GUI 실행은 하지 않았다.

## 기본 게이트

모든 TEMP/TMP는 사용자 기본값이며 DOTNET_CLI_HOME은 미설정이다. telemetry opt-out, 첫 실행 HTTPS 인증서 생성 억제와 Python bytecode 억제만 설정했다.

```text
dotnet restore UeDtLauncher.sln
dotnet build UeDtLauncher.sln -c Release --no-restore
dotnet test src/UeDtLauncher.Tests/UeDtLauncher.Tests.csproj -c Release --no-build
```

기본 build는 추가 속성 없이 오류0/경고0, 전체 테스트는 **890통과/0실패/0skip**(기준849에서41추가)이다. build.yml의 fast contracts Python9개는 모두 종료0이다. 최종 문서·대장·진행표 생성 도구 검사도 별도 수행했다. dotnet format은 이번에 새로 만들거나 분리한25개 파일에만 적용하고 verify-no-changes로 확인했다. 기존108개 파일의 전량 서식 수정은 하지 않았다.

| 묶음 | 추가 검증·의미 |
|---|---|
| W1 | 오류 코드·허용 원인·조치·지원 ID, 명시 --error-details 타입 체인, crash 원문 정제. server IOException 타입명 호환을 유지한 뒤854건 통과 |
| W2 | 401/403/429/stale/서명 오류의 공유 코드·안내, 숫자 Retry-After와 Agent 전달. challenge 재발급은1회 유지.859건 통과 |
| W3 | 값 누락·다음 옵션·중복은 I/O 전 종료2. tools의 config/repair/endpoint/operation 호출 형태를 대조하고 알 수 없는 옵션은 기존 무시 동작 유지. help의 현행/호환 구분, legacy XML 주석·필드 보존.865건 통과 |
| W4 | 정상 순서·6단계별 실패·5종 주입 입력·dry-run 무변경·위험 ZIP·프로필 미덮어쓰기/우선순위15사례. 게시 CLI dry-run도 통과.880건 |
| W5 | 비공개 snapshot 승인/확인 불일치·성공/경합 승격·보호된 정리 후보·EOF/비터미널·비밀 없는 자격·재시도·busy 정리10사례. 게시 console의 리디렉션 거부도 확인.890건 |
| W6 | MainWindow 초기화·필드를 원래 파일에 유지하고 본문을 책임별 partial로 이동. 원래 본문 재구성 hash 일치. 서식 후 최대364줄. 구 레이아웃은 이동만 했고 동작 변경/삭제 없음 |
| W7 | 신규/이동 파일25개 서식 검증과 새 고정 게시본 프로세스 회귀. 한글 stderr 호환 결함을 제품 UTF-8 writer로 수정 후 전체 재실행 |

## 게시 프로세스 회귀

build.yml과 같은 Windows `-r win-x64 --self-contained true -p:PublishSingleFile=true` 형식으로 세 역할을 publish했다. 서버에는 workflow와 같은 `-p:IncludeNativeLibrariesForSelfExtract=true`를 유지했다. 공통 CLI의 기존 native 포함 설정도 유지했다.

```text
python tools/run-readiness-regression.py --launcher <고정게시본>/launcher/UeDtLauncher.exe --agent <고정게시본>/agent/UeDtLauncher.Agent.exe --server <고정게시본>/server/UeDtLauncher.DistributionServer.exe --output <고정게시본>/readiness-summary.json
python tools/run-headless-operations.py --launcher <고정게시본>/launcher/UeDtLauncher.exe --agent <고정게시본>/agent/UeDtLauncher.Agent.exe --server <고정게시본>/server/UeDtLauncher.DistributionServer.exe --output <고정게시본>/headless-summary.json
```

| 고정 후보 | readiness | headless | 판정 |
|---|---|---|---|
| 185a1b7 | 8통과/0실패 | 두 시도 모두1통과/1실패/1미실행 | offline-maintenance의 UTF-8 해독 실패. 한글 stderr가 Windows 기본 인코딩으로 전달됨. 재실행 실패도 보존 |
| e24aa93 | **8통과/0실패/0미실행** | **3통과/0실패/0미실행** | 제품의 리디렉션 출력만 UTF-8, OS code page·fixture·기대값은 변경하지 않음. 서로 다른 후보 성공을 합산하지 않음 |

최종 readiness는20.782초, headless는52.250초의 로컬 wall time이며 성능 gate가 아니다. 실제 HTTP 요청 서명·portable/managed-agent·설치/실행/repair·폐기·작업/예약·offline maintenance·Catalog restart 경로가 포함된다. console Agent이므로 Windows 서비스/서비스 계정 성공으로 확대하지 않는다.

## 실패·환경·원격 근거

최초 W0의22건 ACL 실패는 실행 helper의 TEMP 재지정(샌드박스 계정 소유 폴더)이 원인으로 정정됐다. 같은 cef9f53에서 기본 TEMP로 다시 실행하자849건 전부 통과했다. 소유자/Modify 권한 설명은 사용자 검수 근거이며 이번 직접 확인은 같은 소스·기본 환경의 성공이다. 기존 artifacts의 .dotnet-home/.tmp는 사용자의 재개 지시에 따라 손대지 않았다. 보안 검사·ACL·기대값을 완화하지 않았다.

W1 최초 회귀의 server IOException 타입명 계약 실패는 타입명을 유지해 해결했다. W4 최초 build는 새 테스트의 임시 폴더 helper 누락으로6개 컴파일 오류가 났고 helper를 추가했다. 그 실패 build 뒤 실행된 과거 DLL의865건 결과는 새 코드 근거로 채택하지 않았다(한 건은 distribution.db 삭제 시 사용 중 오류). 이후 helper는 build 실패 시 no-build 테스트를 실행하지 않도록 바꿨다. 새 build 이후880/890건 통과와 새 고정 게시본의 성공만 최종 근거로 채택한다.

읽기 전용 gh list/view로 확인한 [build38359989748](https://github.com/YonghoKwon/UE-DT-LAUNCHER/actions/runs/38359989748)은 **cef9f53**의 2026-10-13 07:00:23~07:07:48 UTC 실행이다. Ubuntu job115171648244와 Windows job115171648511 모두 success다. 이번 스프린트 커밋의 원격 CI는 실행하지 않았고 push/PR도 하지 않았다. 더 최근 다른 SHA의 실행을 이번 결과로 사용하지 않았다.

## 남은 수용

- 실제 SSH 대상 서버가 없어 upload의 실제 왕복은 미실행이다. 단위 가짜 실행기는 테스트 프로젝트 안에만 있고 공개 CLI/환경 변수로 선택할 수 없다. 실제 known-host·업로드 계정·서비스 읽기 ACL·대용량 전송은 후속 인수다.
- 실제 운영자 터미널·회사 서버에서의 console 수용은 별도다. 비터미널은 변경 없이 종료하며, busy 정리는 plan/정확한 apply 명령 안내까지만 검증했다.
- 이번 실제 GUI/Developer EXE 별도 publish/OS DPI·음성/실제 UE/RHEL/서비스·설치본/회사 인증서는 미실행이다. [기존 고정 GUI 진행표](../history/local-completion-progress-pre-sprint-01.json)와 [과거 runtime 결과](local-completion-runtime.json)는 보존하며 새 게시본 GUI 통과로 합산하지 않는다.
- OPS-10은 자동화까지50%, DEV-01도 실제 GUI 수용 전50%를 유지한다. 회사 G1~G6는 완료 처리하지 않는다.
