# SPRINT-01 CLI 품질·배포 관리 편의 검증

## fix-01 추가 검증 — 2026-11-12~13 KST

아래는 최초890건/e24aa93 및 1차 리뷰926건/35dde8a와 구분한 후속이다. 기존 본문의 날짜·수치·실패 이력을 덮어쓰지 않았다. 시작 HEAD는35dde8a6b02e891bf627125ca093646b67330a27이며, 수정 제품 입력은 **2e4b8f9d917079808ad406988d5430dd359837ff**다. U6 문서는 제품 입력을 변경하지 않는다. Windows·SDK9.0.308·기본 TEMP/TMP·DOTNET_CLI_HOME 미설정에서 전체 **966통과/0실패/0skip**(리뷰926+신규40), 기본 build0오류/0경고, Python fast contracts9개를 확인했다. 실제 SSH 성공·GUI·회사 인수는 별도다.

### 보존한 1차 리뷰7커밋

| 커밋 | 보완 |
|---|---|
| eb9a3ca | 끝 개행 ID 거부, sidecar atomic no-replace 및 경합 단일 승자 |
| 1531fe2 | 입력/예외 원문 출력 제거, 완전한 인증 헤더·escaped secret·PEM 정제 |
| 5731026 | 값 위치의 짧은 -h를 누락 오류2로 거부 |
| 2399869 | 확인 불일치/EOF·프롬프트 lease·제한된 인증 재시도 회귀 |
| 526e061 | 관리형 CLI의 rate-limited/stale/Retry-After typed 안내 보존 |
| 827633a | 전체 자동화/GUI 범위와 현행29항목 표기 정정 |
| 35dde8a | 정제 후 한 줄 JSON 성능 메시지 형식 보존; 리뷰 중923/3 실패와 최종926/0 구분 |

이번 기준선도 같은35dde8a에서926/0/0을 재확인했다. 리뷰 수정·기존 테스트의 안전 기준을 되돌리지 않았다.

### U1~U6 변경과 검증

| 항목 | 변경·근거 |
|---|---|
| U1 | 기본 한국어 요약/6단계 상태·stderr 시작 안내·읽기 쉬운 크기. --format json 기존 필드 유지. 잘못된 port는 정제된 인수2 |
| U2 | 종료255·mkdir·전송·실행 파일 없음의 원인 후보/조치. check는 실행 파일 존재 후 true/test-d/test-w만 호출. strict host key·키 인증·BatchMode 유지, 호스트 키 자동 갱신/로컬 명령/forwarding/control socket 생성 차단 |
| U3 | ZIP 크기+64 MB 여유 공간 검사, 원본/사본 digest 비교. 성공 사본은 확인된 작업 루트의 소유 두 파일과 빈 폴더만 제거; 실패/--keep-work/예상 밖 자료는 보존. 정리 실패를 원격 업로드 실패로 바꾸지 않음 |
| U4 | 한국어 표·현재 목록 번호 또는 전체 ID·재조회·승인/승격 버전·거절/재시도 ID8 확인. 계속되는 메뉴·실시간 요약·다음 조치·트랙별 추천 표시. EOF/비터미널/lease/revision 경계 유지 |
| U5 | 목록과 계획이 같은 SelectTargets 보호 판정을 사용. 목록은 본문 hash 없이 크기/신원 조회; 선택 후 한 번의 계획과 적용 시 전체 재검증 유지. 시작 전 잠금과 중간 I/O의 같은 계획 재개 안내 |
| U6 | README/기능 지도/운영 가이드/도식·표/대장/생성 진행표 갱신. OPS-10과 DEV-01은50% 유지. 기존 증거는 별도 보존 |

기존 출력 테스트에서 JSON 버전 키 검사를 사람이 읽는 버전 행으로, 승인 입력을 ID에서 요청된 버전으로, 계획 ID 추출을 새 문구로 바꿨다. 비공개 snapshot 사용·무변경·확인·비밀 미출력·lease/revision assertion은 유지했다. 성공 사본 검증 테스트는 --keep-work를 명시하여 동일한 payload/hash 검사를 유지했다. 새 U3 경로 검사 최초1실패(953/1)는 기존 프로필 경로 정제와의 기대값 차이였고, 파일 보존 assertion을 유지한 채 정제된 경로와 정확한 폴더명을 확인하도록 정정했다. 실패 로그를 보존했다.

### U5 조사 결론

| 질문 | 결론·코드 근거 |
|---|---|
| 기존 PlanAsync의 부작용 | DB read-only snapshot과 파일 hash를 읽어 plan ID/fingerprint를 메모리에서 만들며, 호출 자체는 DB/계획/journal 파일을 기록하지 않는다. RetentionMaintenance.cs:141~152, DistributionBackup.cs:138~142 |
| 목록 비용 | 기존 후보별 PlanAsync는 모든 파일 본문의 SHA-256을 계산했다. 새 InspectCandidates(:112~138)는 크기와 디렉터리 신원만 읽고, 공유 SelectTargets(:79~109)가 공개/승격/pending/active·다른 작업 참조를 동일하게 거부한다. 본문을 배타적으로 연 파일도 목록에 나오고 DB audit/파일 목록/계획/journal이 불변인 시험으로 확인했다. 정량 성능 개선율은 주장하지 않는다 |
| 계획 파일 위치 | root의 console-retention-ID.json을 유지한다. 백업은 지정4컨테이너와 DB만 수집(DistributionBackup.cs:99~115), restore는 manifest/해당 snapshot·release inventory만 대조(:132,:206,:228,:247), 중단 journal 검색은 retention-*.json(:92), intake scan은 incoming 자식만 순회(IntakeStore.cs:320,:328)한다. 접수/backup inventory/plan 참조 hash에 영향 없음은 시험으로도 확인했다 |
| 적용 후 정리 | OperationsConsole.cs:221~235에서 배타적 계획 게시 후 기존 ApplyAsync 호출. 콘솔 적용 성공이면 자신의 계획 파일만 삭제하고, 보류/실패는 보존한다. 성공 journal/audit를 삭제하지 않는다. 보류 뒤 별도 CLI로 수동 적용한 입력 계획 파일은 CLI의 기존 보존 계약을 유지하므로 운영자가 이후 보관/정리를 결정한다 |
| I/O 구분 | RetentionMaintenance.cs:155~172가 시작 전 두 잠금의 IOException에 내부 표시만 붙인다(타입/보호/동작 동일). 콘솔:224~232는 잠금 전 실패와 일부 처리 가능 I/O를 구분한다. 중간 장애 뒤 같은 plan/journal의 명시적 재개가 완료되는 회귀를 확인했다 |

업로드 전체 hash는 원본2회·사본2회(GenerateAsync + ValidateArchiveAsync)의 기존 독립 검사를 유지했다. 공용 검증 API의 검사 생략이나 trust 플래그를 만들지 않았고, 원본과 사본 digest 비교를 추가했다. 중복을 줄이기 위한 API 재설계·대용량 정량 성능 수용은 이번 범위에서 하지 않았다.

### 콘솔 표 출력 예시

다음은 OperationsConsoleTests가 고정 예제 데이터로 캡처한 실제 표 렌더러 출력이다. 회사 운영 터미널/직접 GUI 수용으로 계산하지 않는다.

```text
번호 | 상태(한국어) | 프로젝트 | 버전 | 플랫폼 | 크기 | 진행 | 작업 ID 앞 8자
   1 | 승인 대기     | demo            | 1.2.0        | windows-x64  |      1 KB | 100%           | 01234567
```

문서 정리 전 고정 게시본과 최종 문서 HEAD 게시본은 각각의 source/binary hash로 묶어 별도 기록한다. [fix-01 정제 프로세스](sprint-01-fix-01-process-evidence.json)와 [자동화 입력](sprint-01-fix-01-runtime.json)은 명시한 제품 입력의 근거다. 최종 HEAD의 G5 및 추가 게시 CLI smoke는 별도 로컬 SPRINT-01-fix-01-result.md에 기록하며, 과거/서로 다른 게시본의 성공을 합산하지 않는다. 회사 G1~G6·실제 SSH 성공·운영 계정·GUI/접근성·현재 HEAD 원격 CI는 수용 완료로 바꾸지 않는다.

## 최초 스프린트 기록 — 아래 원문 보존

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
