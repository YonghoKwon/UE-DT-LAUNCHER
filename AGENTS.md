# 작업 지침

점검: 2026-10-05 / 작업 기준 codex/client-recovery-acceptance. 저장소 전체에 적용합니다.

## 문서 관리 계약

- 루트 정본은 README.md(현재), AGENTS.md(규칙), IMPROVEMENTS.md(보완), PROJECT_GOALS.md(확정 목표·승인 조건·미정 정책) 4개입니다.
- 현재 상세 가이드 9개와 색인은 docs/reference/에 둡니다. 실행 증거는 docs/reference/archive/validation/, 통합된 중복 안내는 archive/guides/, 초기 자료는 기존 archive/에 보존합니다. archive/README.md에서 현재 대체 문서와 이력을 구분합니다.
- 검증 이력의 아카이브 이동은 증거 폐기를 뜻하지 않습니다. 날짜·환경·과거 미완료 문장을 임의로 최신화하지 말고, 현재 상태는 루트 정본에 반영합니다.
- 기능·설정·보안·CLI·UI 변경 시 관련 정본과 상세 가이드를 함께 갱신합니다. 같은 설정 전문을 중복 관리하지 않습니다.
- 구현됨/과거 테스트됨/이번 검증됨/회사 미검증을 구분하고 날짜·플랫폼·입력을 기록합니다.
- 최상위 목표는 사용자 확정 사항인 **회사에서 안정적으로 활용 가능한 Unreal Engine DT 배포 시스템**입니다. 세부 범위·회사 SLA·운영 정책은 PROJECT_GOALS의 미정 항목을 따릅니다. 제안이나 목표를 구현/운영 완료로 바꾸지 않습니다.
- 문서를 이동하면 상대 링크와 스크립트 내 참조도 확인합니다.
- IMPROVEMENTS는 완료 이력도 보존하는 진행 대장입니다. 항목별 진행률은 문서의 증거 기반 0/25/50/75/100% 단계로 표시하고, 상태·체크포인트·근거·남은 조건·집계를 함께 갱신합니다. 기존 기능이 있다는 이유로 추가 보완에 점수를 주거나 항목 평균을 제품 완성률로 사용하지 않습니다.

## 사용자 안내와 도식 관리

- 상세 기능 지도·명령 순서는 `docs/reference/feature-workflow.md`에서 관리합니다. A=최초 준비, B=새 버전 배포, C=사용자 실행, D=문제 해결 구분을 유지합니다.
- 단계마다 담당자·실행 위치·입력/명령·정상 결과를 적습니다. Windows PowerShell과 Linux 터미널, 예시값과 실제 회사값, 최초 설정과 반복 작업을 구분합니다.
- 도식은 실제 코드 기준으로 작성하며 수동 승인·사용자 클릭·자동 검사를 분리합니다. 없는 관리자 웹 화면·자동 승인·자동 UE 패키징을 그리지 않습니다.
- 관리형은 Agent가 설치하고 GUI/CLI가 사용자 세션에서 실행합니다. 실행 버튼을 오프라인 즉시 실행 보장으로 설명하지 않습니다.
- Mermaid 흐름과 명령 표는 함께 갱신합니다. 그림을 볼 수 없는 환경에서도 표만으로 순서를 따라갈 수 있게 합니다.
- 문서 검증에서는 링크·명령 옵션·노드/연결을 확인합니다. 회사 서버에 안내 명령을 실제 실행한 것으로 보고하지 않습니다.

## 코드 지도

| 위치 | 책임 |
|---|---|
| src/UeDtLauncher/Program.cs | GUI/CLI 명령 |
| src/UeDtLauncher/Gui/ | programmatic Avalonia View·ViewModel·시각 토큰 |
| src/UeDtLauncher.Core/ | 공용 assembly, Distribution 메타데이터·경로·이미지 |
| src/UeDtLauncher.Core/*.cs | 엔진·보안·transaction·IPC 소스. GUI는 Core project reference만 사용 |
| src/UeDtLauncher.Agent/ | Windows Service/Linux systemd Agent |
| src/UeDtLauncher.DistributionServer/ | SQLite 접수·승인·서명 게시·인증 API |
| src/UeDtLauncher.Tests/, tools/test-distribution-e2e.sh | 자동화·실제 프로세스 E2E |
| installer/windows/, packaging/linux/, .github/workflows/ | 설치본·nginx·서비스·CI |

## 보존할 경계

- 일반/개발자 GUI는 LauncherBuildInfo의 컴파일 에디션으로 결정한다. config/env/실행 옵션/파일명으로 바꾸지 않는다. Core clientProfile는 CLI/legacy 선택 호환용이며 서버 권한과 혼용하지 않는다.
- General/Developer GUI의 obj/bin·assembly를 분리하고 Developer의 브랜드 URI는 실제 assembly로 해석한다. 공통 runtime-host UeDtLauncher는 developer ZIP/설치본에도 포함한다. 런처 self-update 자동 교체는 사용하지 않고 원본 설정/pending을 보존한다.
- 일반 MSI는 일반/Agent, 개발자 MSI는 일반/개발자/Agent이며 같은 제품 계열의 동일 버전 중복을 차단한다. RPM developer addon은 exact base version 의존성과 별도 파일 소유권을 가진다. 실제 설치/계정은 별도 인수다.
- 2026-10-03 사용자가 GUI 마우스 검증 재개를 승인했다. 새 합성 fixture에만 적용한다. 초기 Disc/GetCursorPos0x80070005 환경 제약과 재연결 후 실제 클릭 통과를 구분하며 CLI 설치로 GUI 시험을 대신하지 않는다.

- Catalog sequence reservation은 exclusive 인증 서버 수명 안에서 최대64개 high-water를 commit한 뒤 발급한다. 실패/재시작으로 번호를 재사용하지 않고 overflow/잘못된 DB형식은 차단한다. 정상 예약은 복원 순번 우회가 아니며 Catalog/권한 캐시와 혼용하지 않는다. 비교의 느려진1연결·누락 지표를 숨기지 않는다.
- 복원 활성화는 원본 일치만으로 충분하지 않다. 릴리스 tuple·Manifest 서명 ID/알고리즘·전체 파일 hash/inventory를 별도 검사한다. 확인할 수 없는 이전 signing identity는 staged 점검 대상으로 남긴다.
- RPM은 고유 실행 폴더·정확한 파일 경로·data-only newc 검사·payload hash/권한 확인을 지킨다. stale wildcard/미검증 publish 생략을 금지한다. stable은 같은 commit functional gate와 signer/fingerprint 검증 후에만 공개하며 실제 인증서/설치 인수와 구분한다.
- 회사 인수 준비 도구는 미실행 계획만 생성한다. 현재 호스트의 install/service/account/reboot를 수행하지 않는다. Git 없는 mirror의 source 상태를 clean/0으로 보충하지 않으며 출처 hash·사후 copy 대조와 build attestation을 구분한다.

- Doctor selected release는 target/권한 확인을 거친 optional 정보다. schedule 진단/설치 상태에 같은 선택을 사용하고 default config로 다시 선택하지 않는다. Healthy와 preparation/status를 혼동하지 않는다.

- limit 시간/HTTP file barrier는 내부 시험 seam만 사용한다. 정상 동시성·429·슬롯 반환·만료/audit 원자성을 actual HTTP로 검증하고 검사 우회 CLI를 만들지 않는다.

- stage 전체 exclusive target reservation, live lease 이후 staged fence 검사, source/target canonical lock ordering을 유지한다. 필수 source/snapshot/active/release의 양방향 inventory/hash와 policy를 검증한다. incomplete retention backup·staged authority·기록 없는 누락은 거부하고 fence 제거는 마지막이다.

- schema6 전환 전 일관된 DB backup/서버 중지를 지킨다. incomplete legacy retention journal을 자동 채택하지 않는다. candidate directory identity와 durable move/delete intents를 유지하고 이동 후 source 재수집을 금지한다. 잔여 승인 파일만 검사·삭제하며 완료 삭제 ledger/audit를 보존한다.

- resume cache는 신뢰된 실제 requester context를 사용한다. service identity로 보충하거나 클라이언트 owner 주장을 신뢰하지 않는다. content quota는 partial/verified/new 예약을 포함하고 owned temporary만 정리한다. discard는 비활성 archive/ID 재사용 거부이며 active lock inode를 삭제하지 않는다.

- operation registration ACK 이전 취소 의도를 유실하지 않는다. commit 완료와 조회/실행 취소를 분리한다. launch-abort는 원래 OS requester/attempt/ticket·Host 미부착에만 허용하며 started/unknown을 해제하지 않는다. Backend token/result 계약을 실행/문제 해결에 동일 적용한다.

- 후속 fixture는 새 비공개 root의 owner/ACL·containment·config/binary hash를 검증한다. 디렉터리 이름만으로 시험 대상을 승인하지 않는다. 비밀 provisioning stdout은 비보관이며 incomplete/failed/timeout 결과도 atomic summary로 보존한다.

- headless CI는 고유 private fixture의 published 프로세스만 사용하고 allowlist summary JSON 하나를7일 보관한다. 실환경 권한 없는 테스트를 서비스/계정/인증서 인수로 바꾸지 않는다. 새 경로 보호·환경 검증이 빠진 checkpoint는 별도로 열린 상태를 유지한다.

- phase 계측의 일부 시간은 겹친다. additive aggregate를 latency 합계나 native SQLite wait로 해석하지 않는다. 프로토콜/후보를 혼합하지 않으며 느려진 결과도 기록한다. 이번 PERF-03 미달과 unadopted signer pool을 실제 개선으로 보고하지 않는다.

- operation의 bounded record 읽기는 atomic replace를 허용하고 기록 잠금 순서를 유지한다. Manifest digest 재결속을 금지한다. IPC correlation은 기존 arbitrary string 계약이며 internal operation ID를 분리해 구 클라이언트를 거부하지 않는다. cancel 이후 CLI는 아직 시작하지 않은 앱 실행을 생략한다. TLS/runtime fixture를 무인증으로 낮춰 제품 정책을 우회하지 않는다.

- scheduled-check는 기본 disabled/주기 명시/중복 skip이며 조회만 한다. SelfUpdateManager·service-run·engine apply·launch를 예약 경로에서 호출하지 않는다. template은 비활성이고 task/service/account를 현재 호스트에 등록하지 않는다. 신뢰 기록/진단 로그 외 payload/state/backup/runtime 변경을 금지한다.

- retention은 명시 선택 failed/rejected·참조 없는 processing 자식만 허용한다. public/모든 promotion/pending/active 보호, 고유 plan/fingerprint·참조·전체 hash 재검사, quarantine/journal을 유지한다. 완료 계획은 재생성 자료에 재사용하지 않는다. 링크/audit/history를 삭제 대상으로 확대하지 않는다.

- serve/watch/긴 파일 작업/DB 변경과 offline backup/restore/정리는 동일 maintenance OS 잠금 계약을 따른다. 프로세스를 도구가 종료하지 않는다. 복원은 새 빈 root·같은 origin/signer·최신 생존 source 대조이며 staged fence 제거가 마지막이다. 순번을 임의로 올리거나 client trust 삭제, 과거 폐기/정책 재활성화는 금지한다. private key를 백업 번들에 넣지 않는다.

- DB schema5 이전 전 snapshot 백업과 구 서버 중지를 지킨다. token 관리 ID와 비밀을 분리한다. legacy 무기한을 보존하고 만료는 명시 지정한다. 수명 변경·최초 만료 latch·audit은 한 transaction이며 시계 역행으로 latch를 풀지 않는다. 권한 허용 캐시는 금지한다. 요청 한도는 opt-in, 초과 시 메모리 큐 없이429다.

- 작업 control은 실제 OS owner/session 또는 관리자만 허용한다. IPC 단절은 취소가 아니다. OS 작업 잠금과 원자적 기록을 유지하며 active 작업을 discard하지 않는다. commit 이후 취소는 완료 상태이며 시작 전 launch만 생략한다. cache는 명시 예산·서명된 Manifest digest·정확한 release/owner에 결속하며 완료 파일도 재검증한다. 새 launch는 온라인 권한 확인 없이 runtime ticket을 만들지 않는다.

- 과거 headless 작업의 GUI 중단은2026-10-03 합성 fixture 마우스 시험 재개 요청으로 해제됐다. 실제 입력 환경 제약은 별도 기록한다. 측정은 현재 인증/명시 승격으로 수행하고 실패/누락을 보존한다. 새 실행은 온라인 확인, 예약은 조회만, 정리는 공개/승격/활성 보호·확인형, 만료는 명시 지정이다. 회사 설치/계정/인증서와 push/PR은 별도 요청이다.

- 오프라인 doctor의 전체 호출 경로는 설정·설치·state·backup·runtime·legacy 파일/디렉터리/잠금을 생성·변경하지 않는다. 검사 전용 설정 로딩은 legacy migration을 수행하지 않으며 credential 경로 조회는 순수 계산이다.
- 온라인 점검은 기존 인증/서명/요청 결속/sequence 검증과 anti-replay 신뢰 기록을 유지한다. 점검과 조회 재시도가 설치·실행·복원으로 바뀌지 않게 한다.
- DoctorCheck의 passed/failed/waiting/deferred/not-applicable과 준비도 요약을 구분한다. Healthy/종료0을 설치 가능이나 사용자 쓰기 검증 성공으로 해석하지 않는다. 구형 상세 필드는 미검증이며 read-only-doctor-v1 없는 Agent에 기존 doctor를 우회 호출하지 않는다.
- 관리형 진단은 표시 설정과 Agent 보호 설정의 주체를 구분하고 GUI가 credential/보호 state를 직접 읽지 않는다. portable은 Agent 요청을 하지 않는다. 진단 대상에는 모드·프로젝트·트랙·OS·정확한 버전 정책만 전달하며 임의 URL/경로는 받지 않는다.
- 진단 결과와 재시도는 요청 당시 선택에 결속한다. 늦은 결과를 버리고 일반 오류에는 원인·다음 조치·지원 ID를 제공한다. Support ZIP의 JSON/로그에서 비밀과 사용자 경로를 제거한다.
- DoctorResponse의 envelope/검사/target 검증과 합성을 사용한다. 요청한 target은 Agent가 확인한 target이 아니다. 누락된 target/준비도/state 및 알 수 없는 state의 미검증 근거는 deferred 검사로 보존하고, 상위 Complete로 정상 승격하지 않는다. null 검사 목록/항목·대상 불일치·성공 모순은 정제된 진단 오류다.
- readiness CI는 고유한 임시 root/endpoint의 CLI·console Agent만 사용한다. 보관은 run-readiness-regression의 허용 필드 요약 JSON 한 파일·7일이며 private root/키/설정/원시 로그/예외 전문 업로드를 금지한다. 로컬 동일 명령 통과는 원격 CI 성공이 아니다.
- GUI 입력 도구의 캐시/캡처 오류는 제품 실패와 구분한다. 창을 새로 선택·활성화하고 화면과 대상이 일치한 뒤 조작한다. 다른 프로그램의 화면이 캡처되면 그 좌표를 사용하지 않는다.

- approve는 게시만 한다. 첫 버전도 명시적 promote가 필요하며 PC별 추천은 현재 허용된 승격 이력의 마지막 판이다. 프로젝트/환경/채널/플랫폼을 섞거나 미승격판으로 fallback하지 않는다.
- 승격/감사 기록은 한 transaction, expected revision 재검사를 사용한다. same-target/current-revision은 no-op이며 승인/서버 재시작은 이력을 변경하지 않는다. 승격은 다운로드 grant·설치·실행 티켓을 변경하지 않는다.
- Catalog의 release/promotion은 동일 DB snapshot에서 읽는다. 새 selectionPolicy 요청은 전체 허용 목록, 구형 요청은 승격된 허용 목록만 제공한다. 인증·권한·fresh sequence·서명·요청 결속을 유지한다.
- 기존 DB migration dry-run은 IntakeStore를 만들지 않고 read-only DB만 읽는다. apply는 서버 중지·백업·상태 재검사 후 legacy-baseline을 정확히 한 번 기록한다. DB schema4와 구 서버의 동시 사용을 금지한다.

- launch-begin이 확정한 선택을 host session에 고정한다. 데이터 plan의 ReleaseId/설치 경로/attempt/owner 관계를 검사하며 rootDirectory 누락을 명시적 null(기본 루트)로 보충하지 않는다.
- Linux sticky 예외는 root 소유 공용 상위 컨테이너에만 적용한다. 실제 사용자 데이터 namespace는 공용 쓰기를 거부하고 사용자 데이터/로그 양쪽의 write/flush를 확인한다.
- 자동 GUI 입력이 막히면 합의한 사용자 직접 조작 협업을 사용한다. 버튼/정확한 fixture를 안내하고 결과를 파일·runtime 증거로 확인하며 사용자 조작임을 기록한다. 적용 결과가 없으면 미실행으로 유지한다.
- snapshot protected는 Manifest 관리 파일과 metadata/backup/journal을 비교하고 runtime-state/update.lock은 별도 관측한다. data는 선택 버전 UserDir만 비교하고 실행 로그는 따로 기록한다. all은 원시 전체 inventory다. scope가 다른 snapshot은 성공 비교로 합산하지 않는다.

- 이번 USER-01 작업은 런처만 수정한다. UE·DTCore 재패키징/수정은 금지하며 다른 작업에서 발생한 소스 변경을 stage·되돌리지 않는다. 기존 실제 UE 패키지와 현재 UE 소스 revision을 동일하다고 기록하지 않는다.
- runtimeData는 opt-in schema 3·unreal-engine·per-user-per-release만 지원한다. GUI/IPC 입력으로 임의 경로/인수를 받지 않는다. Agent의 보호 설정으로 만든 plan을 실행 attempt·installation·실제 owner에 결속한다.
- 실행 사용자 host만 데이터 폴더를 준비한다. Agent 계정의 LocalAppData를 사용자 데이터로 쓰지 않는다. 링크·비보호 권한·설치/state/backup/credential 중첩과 기존 UserDir/abslog 충돌을 거부하며 오류 시 설치 경로로 fallback하지 않는다.
- 사용자 데이터는 payload backup/prune/rollback 대상이 아니다. 자동 버전 간 복사·공유·데이터 복원을 추가하지 않는다. 새 모드 capability 누락은 실행을 거부하고 구 설정은 그대로 유지한다.
- 실제 UE 포함 파일-delta fixture는 UE 바이너리가 동일한 두 릴리스다. 별도 UE 빌드 간 호환성으로 보고하지 않는다. 원본 inventory 전후와 ZIP member hash를 확인하고 시험 비실행 파일만 변경한다.
- 정상 backup 증거는 정상 상태에서 force repair 후 모든 Manifest 파일 hash로 확인한다. subset/빈 backup을 전체 정상 복원 증거로 사용하지 않는다. 알려진 비Manifest CustomLogs는 별도 기록하고 Logs/Saved 전체 해시 제외를 금지한다.
- 고정 cohort의 제품 출처와 시험 도구 출처를 분리한다. GUI 입력/캡처 오류는 미실행으로 기록하고 설치를 CLI로 대신해 GUI 통과 처리하지 않는다.

- ZIP + 외부 release.json을 비공개 snapshot으로 검사하고 관리자 승인 후 게시합니다. 업로드 프로그램을 서버에서 실행하지 않습니다.
- .uploading/한 파일만 도착한 상태를 게시하지 않습니다. 동일 릴리스 식별자 덮어쓰기를 허용하지 않습니다.
- 실제 IP/CIDR + PC 인증키(기존 HTTPS/Bearer는 토큰)를 목록·Manifest·이미지·파일·Range 모두에 적용합니다. 공개 정적 경로 우회를 만들지 않습니다.
- 요청 서명은 schema 3·별도 .keycred만 사용합니다. 개인키를 GUI에 공유하거나 임의 URL 서명 IPC를 만들지 않습니다. challenge는 monotonic 만료·재시작 무효화, 살아 있는 nonce는 용량 확보를 위해 제거하지 않습니다.
- Catalog 요청 결속은 실제 성공한 송신 요청과 비교한 후 해석/sequence 저장합니다. nginx 이중 인증과 권한 허용 캐시는 금지합니다. 같은 root의 인증 서버는 하나만 실행합니다.
- 기존 HTTPS/Bearer를 유지합니다. 사용자 승인한 schema 3 사내 HTTP는 요청 서명·재사용 차단·Catalog 요청 결속을 모두 요구하며 Bearer/무인증으로 후퇴하지 않습니다. Metadata 서명·해시·경로 검사를 완화하지 않습니다. 개인키·토큰·Authorization·Signature·challenge를 로그나 Git에 넣지 않습니다.
- GUI 프로필은 화면 정책입니다. Agent의 보호된 운영 설정·credential·서버 권한과 분리합니다. 정확한 선택을 다른 버전으로 몰래 대체하지 않습니다.
- 설치·상태·잠금·PID는 프로젝트/환경/채널/버전/OS별 격리입니다. 기존 설치·사용자 데이터는 승인 없이 삭제하지 않습니다.
- 일반 GUI는 자동 점검만 합니다. 설치는 사용자 동작, rollback은 확인 후 실행합니다. 무인 서비스와 구분합니다.
- IPC v1 framing·조회·streaming 의미를 보존합니다. runtime-supervision-v1 capability 없는 변경 요청은 업그레이드 안내로 거부합니다.
- 실행 시작과 모든 설치 변경은 InstallationMutationLease로 직렬화합니다. Prepare의 복구보다 먼저 검사하고 Running/LaunchPending/Unknown에서는 설치·state·backup·journal을 변경하지 않습니다.
- runtime 기록의 필수 필드·중복·상태별 관계를 검사합니다. 누락 필드를 enum 기본값 Quiescent로 인정하지 않습니다. inspect/dry-run과 거부된 복구 요청은 파일을 생성/변경하지 않습니다.
- 서비스 잠금 → 설치 ID 정렬 잠금 순서를 지킵니다. 단일 service snapshot에 시작 전 barrier를 기록하고 health 성공 기록 전에는 안전 상태로 풀지 않습니다. 기존 active/failure 기록은 명시적 확인 전 자동 통합하지 않습니다.
- 공유 InstallDir의 portable→관리형 migrate apply를 재활성화하지 않습니다. import는 잠금 후 metadata를 다시 읽고 staging을 재검증합니다. 중단 transaction이 있으면 직접 실행하지 않습니다.
- 테스트 전용 fault harness는 설치/공식 publish graph에 넣지 않습니다. 공개 CLI/환경변수로 장애 주입·가짜 종료·검사 우회를 제공하지 않습니다.
- 이름/PID만 보고 종료하거나 host 소멸/timeout을 Quiescent로 바꾸지 않습니다. runtime-host가 표준 후손 종료를 확인해야 하며 추적 불가 플랫폼에서 우회 실행하지 않습니다.
- 관리형 실행 기록은 Agent 소유입니다. 티켓은 private pipe로 전달하고 실제 OS peer/생성 식별자/시도에 결속합니다. GUI에 보호 state 쓰기 권한이나 임의 원격 실행 IPC를 추가하지 않습니다.
- service-run의 자동 kill·버전 handoff·health 실패 자동 rollback을 금지합니다. 수동 정지 확인은 관리자/portable 소유자 권한과 보존 기록을 요구하며 OS 종료 증거로 표시하지 않습니다.
- runtime-host는 악성 동일 사용자 격리 경계가 아닙니다. 외부 실행 broker·수동 EXE는 보장 범위 밖입니다. 테스트 정리는 직접 생성해 보유한 handle만 사용합니다.
- 병렬 작업 실패 시 형제 작업을 취소하고 모두 종료한 뒤 반환합니다. transaction 적용은 직렬로 유지합니다.
- 파일 재사용의 로컬 기록은 힌트이며, 인증된 대상 Manifest와 복사 결과 해시가 신뢰 기준입니다. 원본 수정·hard link를 금지합니다.
- DB schema 변경 전 백업, 작업별 OS 잠금, active_work 보호를 유지합니다. 잠금 파일을 삭제하거나 긴 ZIP I/O를 공용 잠금 안에 넣지 않습니다.

## UI 규칙

- 복구/복원 적용 완료와 후속 조회 실패를 구분한다. 완료 후 조회 실패/파일 이상은 Check 재시도이며 복구 실패의 rollback 제안으로 되돌리지 않는다. 같은 선택의 재개 힌트는 적용 성공 시 해제하고 조회/확인 취소/적용 전 실패에는 유지한다. 복원 단계는 다운로드 취소 컨트롤을 종료한다.
- fixture 변경은 기존 설치 잠금과 제품의 엄격한 runtime/operation 관측으로 보호한다. payload/backup의 *.lock도 비교하고 이미 끝난 synthetic 시도를 release 성공으로 인정하지 않는다. 프록시 세션별 계측을 보존하며 도구 preflight는 인증 not-checked를 정상으로 보충하지 않는다.
- 2026-10-05 물리 ESC로 Computer Use가 중단됐다. 이후 이 턴의 앱 입력을 멈추고 미실행을 기록한다. 초기 GUI 진단1920×1080과 계획1440×900을 구분하며 다음 재개 시 실제값을 확인한다.

- 작업 수명은 공통 start/finish 경계를 사용한다. 실제 시작 즉시 취소 표시, 취소 요청 중 중복 입력 차단, 종료 상태 해제 후 화면 갱신 순서를 지킨다. 진행 콜백 생성과 UI 큐 처리 양쪽에서 세대 ID를 검사해 종료/이전 작업 이벤트를 버린다.
- 재개 표시와 적용은 같은 선택·Manifest digest·재개 가능한 phase·runtime 상태 판정을 사용한다. operation-resume의 선택은 기존 서버의 exact 검증과 일치시키며 다른 operation 제어 명령에 임의 선택을 허용하지 않는다. 정상 Running은 안내 상태이고 변경은 차단하되 새 오류 지원 ID를 만들지 않는다.
- 빈 Catalog 종료에서는 이전 프로젝트/설치/재개 표시를 제거한다. 빈 화면의 다시 확인도 Catalog만 갱신하지 않고 정확한 선택의 설치 상태까지 읽기 전용으로 확인한다.
- 2026-10-03 사용자 합의로 이번 게시본 화면/기능 시험은 원격 세션의 실제1440×900·100%로 기록한다. 이전 UI-01의1920×1080 한정 수용과 합산하거나 실제 OS 배율 시험으로 확장하지 않는다.

- 실제 GUI 입력은 시험 동안 활성·잠금 해제된 데스크톱 연결이 필요하다. 2026-10-03 재연결의 초기 조회2건/Disc 이력과 이후 관리형 v1 설치·v2 업데이트/실행·개발자 exact 확인 취소 통과를 구분한다. 완료 후 cancel 버튼 잔존은 별도 결함이며 수정 후 영향 시험을 다시 수행한다. 관측1440×900/scale1을1920×1080 시험으로 계산하지 않는다.

- 최신 GUI 수용의 남은 목록은 guide-03-launcher-usage의 단일 체크리스트를 정본으로 사용한다. readiness final/final-02 및 이전8ce5060 이력을 섞지 않으며 현재 요약/항목표/체크포인트는 최신 검증 범위를 함께 반영한다.

- programmatic View와 LauncherVisualTokens·ViewModel을 사용합니다. 현행 레이아웃은 MainWindowEnterprise, 대화창은 MainWindowAccessibility, 피드백은 MainWindowFeedback입니다.
- 일반은 밝은 POSCO DX 업무 화면, 개발자는 같은 브랜드의 다크 화면을 유지합니다. 공식 로고 원본/출처를 보존하며 임의 CI 재가공을 하지 않습니다.
- 글자 배율·앱 고대비는 사용자별 ui-preferences.json에만 저장합니다. UI 스레드에서 비동기 파일 저장을 동기 대기하지 않습니다. OS 고대비 요청을 우선합니다.
- GUI 시험은 새 fixture에 같은 소스의 GUI/Agent/서버/합성 앱을 게시하고 바이너리 hash·HEAD·소스 diff hash를 기록합니다. 기존 fixture와 설치를 덮어쓰지 않습니다.
- console Agent의 합성 시험 성공은 실제 Windows 서비스 설치·서비스 계정 권한 검증이 아닙니다. 두 검증 범위를 별도로 기록합니다.
- 최종 수용은 고정된 같은 게시본 묶음으로 수행합니다. 수정 후에는 영향받는 시험을 새 게시본에서 다시 실행하고 이전 후보 성공을 합산하지 않습니다. Portable fixture는 Agent 없이 별도 credential/설치/state를 사용합니다. 준비·재개·제어 도구에서 게시본 해시와 실제 readiness를 확인합니다.
- 실행 중 차단 시험은 살아 있는 합성 marker와 Running을 먼저 확인하고 종료 전에 보호 snapshot을 비교합니다. 정상 종료가 기록하는 runtime 변경을 설치 변경으로 오인하거나, 제한시간 종료 뒤 비활성 버튼을 실행 중 차단 증거로 사용하지 않습니다.
- 손상 repair 직전 파일은 손상 상태로 백업될 수 있습니다. 정상 rollback 시험은 repair 성공 후 정상 상태에서 다시 repair하여 최신 backup 전체 hash를 확인합니다.
- OS 해상도·배율·고대비는 사용자 협업으로 변경/원복하고 앱 진단의 screen/work area·RenderScaling·DIP·글자 배율로 대조합니다. 사용자 prefs는 원본을 보존하고 중간 사용자 변경이 감지되면 복원 덮어쓰기를 거부합니다.
- PreviousInstallation은 인증된 동일 track의 bounded 설치 기록 표시 힌트입니다. 현재 선택의 IsInstalled/InstalledVersion/HasBackup 의미, 실행 권한·실행 대상·백업 대상을 바꾸지 않습니다.
- 조회 오류의 재시도를 설치/실행으로 바꾸지 않습니다. IPC 연결 대기와 연결 후 작업 제한을 분리하고 외부 취소를 서비스 장애로 바꾸지 않습니다. 작업·릴리스 snapshot이 달라지면 조회하고 rollback은 새 preview/확인을 받습니다. backup ID/fingerprint를 설치 lease 안에서 재검증합니다.
- 관리형·portable의 작업 context/result는 같은 모드와 정확한 선택에 결속합니다. Portable은 실제 버전 경로와 로컬 runtime 상태를 표시하고 Agent 상태로 대체하지 않습니다. 문제 해결은 미설치/새 버전을 설치하지 않으며 정상 설치는 점검만, 설치된 손상 대상만 repair합니다. 복원 후 상태 재확인 실패를 복원 실패나 무조건 Ready로 오인하지 않습니다.
- 관리형 정리에 필요한 Agent 명령이 없으면 보호 디렉터리 직접 쓰기로 우회하지 않습니다. 기존 IPC와 신규 rollback-preview-v1 capability를 구분합니다.
- headless DIP viewport/UIA 이름은 실제 DPI·내레이터 음성 증거가 아닙니다. 기본 GUI 시험은 합성 앱에 한정하고 적용 직전 필요한 확인을 받습니다. 2026-09-29 사용자 요청의 ma0t10_dt 실제 UE 패키지는 별도 격리 fixture에서만 예외적으로 시험하며, 원본 프로젝트 보호·로컬 통신/저장 경로 확인·실행 직전 확인을 유지합니다. 다른 실제 프로그램이나 회사 서버로 범위를 자동 확대하지 않습니다.
- GUI 조작 주체를 구분합니다. 사용자 조작 후 결과만 관측했다면 에이전트가 버튼을 눌렀다고 기록하지 않습니다. 원래 OS 설정/원복을 확인하지 못했다면 미확인으로 남깁니다. 2026-09-29 사용자 합의로 UI-01은 1920×1080의 현재 검증 환경(OS 배율100%·앱 글자100%·고대비 끔, 양 모드/두 프로필) 범위에서 100%로 수용합니다. 다른 해상도 및 미시험 DPI/큰 글자/고대비 조건은 ‘추가 테스트 필요’ 주석으로 유지하며 통과했다고 기록하지 않습니다. 종전 18개 조합 기준은 이력으로 보존합니다. UI-02는 남은 기능 수용을 별도로 유지하고, 음성 보류 중 UI-03은 75%입니다.
- 관리형 실행 성공을 Ready로 단정하지 않습니다. runtime 관측의 Running/Pending/Unknown/누락은 차단 상태이며, 문제 해결에서 typed runtime 실패를 일반 예외로 잃거나 rollback 제안으로 바꾸지 않습니다.
- GUI 시험용 앱은 셸 없이 자기 자식·marker만 사용하며 공식 배포물에 포함하지 않습니다. 미설치 GUI 시험을 CLI 선설치로 대체하지 않습니다. 실제 버튼 조작과 단위/CLI 검증을 별도로 기록합니다. 일반 설치/실행·창 종료 통과를 모든 개발자 경로 통과로 확대하지 않습니다. rollback 증거는 실제 정상 backup과 복원 후 hash로 확인하며, 같은 설치의 backup 복원을 다른 버전 경로로의 전환이라고 표현하지 않습니다.
- 일반 화면은 한 버튼·친화적 오류, Agent 대신 업데이트 서비스로 표기합니다. 기술 예외·내부 경로·비밀정보를 기본 화면에 표시하지 않습니다.
- 개발자 명령을 보존하되 서버 권한을 확대하지 않습니다. 배포 서버 모드 GUI는 현재 OS용 릴리스를 선택합니다.
- 이미지 누락·손상·과대 파일은 브랜드 fallback으로 처리합니다. 키보드·focus·스크린리더·DPI를 확인합니다.
- GUI 설정 탐색은 explicit --config → 실행 파일 옆 → 관리 설정입니다. 관리형 운영 값은 Agent 설정을 사용합니다.
- single-file 네이티브 라이브러리 포함 옵션과 XAML의 &amp; escaping을 유지합니다.

## 작업·검증·커밋

- 공식 MSI는 payload EXE 서명/검증 후 생성한다. 내장 CAB EXE hash/signer 검사 전 artifact를 공개하지 않는다. 실행별 WiX intermediate를 사용하고 stale MSI wildcard를 금지한다.

1. git status와 지침을 확인하고 사용자 변경을 보존합니다. 브랜치·remote를 임의로 교체하지 않습니다.
2. 의미 단위 구현 후 관련 테스트를 실행합니다. --no-restore 전 restore가 필요합니다.
3. 기능 변경은 publish된 GUI/Agent/CLI/서버로 실행 검증합니다. Unreal Editor 프로젝트가 아닙니다.
4. Windows/WSL 결과를 회사 RHEL/실제 UE 결과로 보고하지 않습니다.
5. 관련 파일만 git add로 선별 stage하고 staged diff 확인 후 부분별 커밋합니다. git add -A는 사용하지 않습니다.
6. 최종 git diff --check, 링크, 커밋 범위, worktree와 tools/check-documentation.py의 구조/링크 검사, tools/check-improvement-ledger.py의 집계 검사를 확인합니다. push/PR은 요청 범위에 따릅니다.

기본 빌드·테스트 명령은 README를 따릅니다. 문서 전용 수정은 소스·명령·링크 대조와 diff 검사로 검증 가능하며 GUI 실행·전체 테스트를 수행한 것처럼 보고하지 않습니다.

publish/bin/obj, 테스트 logs·DB·인증서·토큰, 패키지·설치 데이터는 커밋하지 않습니다. 새 테스트 없이 과거 검증 날짜·결과를 덮어쓰지 않습니다.

성능 변경은 [재현 절차](docs/reference/archive/validation/performance-validation.md)를 따릅니다. 준비 1회/측정 3회, 같은 publish 형식·데이터·호스트 조건으로 비교합니다. CLI 시작 비용·OS 캐시·측정 프록시 영향과 누락 지표를 구분합니다. 실패한 요청을 지연 통계에서 숨기지 않고, 악화된 시나리오를 유리한 평균으로 덮지 않습니다. 공유 CI의 시간 수치를 성능 합격 gate로 사용하지 않습니다.
