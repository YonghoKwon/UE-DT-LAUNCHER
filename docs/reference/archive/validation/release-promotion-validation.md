# 승인·최신 추천 승격 분리 — 로컬 수용 결과

검증일2026-10-02 / `codex/release-promotion-control`, 시작2e8e813. OPS-03은 합의한 로컬 기능 범위100%로 판단한다. 회사 인수·실제 UE 데이터 수용·다른 OPS/SEC/UI 항목은 별도이다.

## 제품 출처

고정 후보 `a8080eaee5dd604cccac507be8e6aa65d5ac4ae9`, productSourceDirty=false, source inventory SHA-256 `9c0cd33f0b6f908fb141d9ba7286357c1d64720e0052ebd0ee51810946deb39c`.

| Windows 산출물 | SHA-256 |
|---|---|
| Launcher | a089fb0e2d03d4eced76283bc714a9963ecaec18116e9769bb52a08ea568c666 |
| Agent | ce6e620428f090eeaac167eac8b84b01de601f56a7d94bb56dcb803ac70b4652 |
| DistributionServer | 82136b03ad0c372dc86d8328ab446c110efebb0202cb754e059be3eabd468532 |

## 실행·자동화 결과

| 시험 | 결과/증거 |
|---|---|
| Windows/WSL Release 전체 | 각각568통과·skip0, 기존552+신규16. 빌드/publish warning/error0 |
| fixture Python | 12통과, YAML 구조/문서 검사 별도 통과 |
| 승격 저장 | 첫 승인 추천 없음, 높은/과거판 승인 후 추천 유지, same-target no-op, stale revision 거부, 감사/이력 restart 확인 |
| 장애·동시성 | 자동화에서 commit 직전 장애 시 history/audit 롤백, 동시 동일 revision은 한 승격만 성공 |
| 권한별 Catalog | 신규 요청은 허용 승인판 전체, legacy 요청은 승격된 허용판만. v2/v1 승격 후 PC grant에 따라 이전 허용판 추천 |
| migration | 양 OS 게시 CLI로 schema3 copy→dry-run DB hash 불변→apply backup/legacy-baseline→반복 apply 중복0 |
| 미등록/다른 구분 | published exact ID 없으면 승격 거부, 이력/감사 불변 |
| Windows HTTP 서명·Agent | `publish/promotion/final-http`, 두 버전 install/run/repair·PC 키 폐기·asset/doctor·서비스 구분·pending/running 불변 통과 |
| Linux HTTP 서명·Agent | `/tmp/uedt-intranet-2aj1skz8`, 위 합성 흐름 통과 |
| Linux HTTPS/Bearer/nginx | `/tmp/uedt-distribution-e2e.Tb7Vid`, Range·IP/header 거부·토큰 폐기·두 버전·repair 통과 |
| 별도 게시 Catalog/migration | Windows `publish/promotion/final-migration`, Linux `/tmp/uedt-data-safety.2Lwycf/promotion-proof` 통과 |
| 실제 Windows 일반 GUI | `publish/promotion/gui-unassigned-02` 게시본 실행 후 UIA 관측: ‘관리자가 실행 버전을 지정하지 않았습니다.’, primary-action 비활성, 미설치 확인 |

pending/running 시험은 실제 Agent IPC의 launch-begin으로2.0.0 티켓을 얻고,1.0.0 승격 후 기록 hash 불변을 확인했다. 해당 티켓을 private stdin으로 실제 runtime-host에 전달한 뒤 합성 프로세스 Running 상태에서2.0.0을 승격해 기록/대상이 유지됨을 확인하고 자연 종료/Quiescent를 기다렸다. 제품에 장애/종료 우회 설정을 추가하지 않았고 이름으로 사용자 프로세스를 종료하지 않았다.

GUI는 표시 상태와 disabled 버튼을 읽은 관측이며 클릭 설치/전체 화면 수용이 아니다. 기존 USER-01 실제 UE GUI 협업과 UI-02 잔여 수용을 대체하지 않는다. 이미지/사용자 선호 설정·시험 설치·키·원시 로그는 Git에서 제외했다.

초기 게시 CLI 시험은 도구 업로드가 incoming 바로 아래가 아니라 정상 거부됐다. 도구 경로를 수정한 proof-02 및 최종 후보 시험이 통과했다. GUI 준비 첫 실행은 합성 실행 파일 이름 입력 오류로 실패했고 올바른 SyntheticGuiApp.exe를 쓴 새 fixture가 준비됐다. WSL nginx는 기존 기본 log 부재 alert를 출력했지만 전용 설정/E2E는 통과했으며 전역 경로를 생성하지 않았다. 이런 시행착오를 제품 무오류 운영 보장으로 숨기지 않는다.

## 정책·호환과 이전

- 모든 새 승인판은 추천을 자동 변경하지 않는다. 첫 버전도 명시적 promote 필요.
- PC별 추천은 현재 인증/IP/grant로 허용된 승격 이력의 마지막 판이다. 단순 최고 버전 또는 마지막 승인이 아니다.
- 명시적 이전판 승격은 가능하다. 승격은 grant·설치 파일·기존 실행 티켓·실행 중 앱을 변경하지 않는다.
- source release/promotion을 같은 SQLite snapshot에서 읽고 Catalog sequence/expiry/signature/HTTP request binding을 유지한다.
- query `selectionPolicy=explicit-promotion-v1`이면 허용 승인판 전체와 정책을 제공한다. 구형 query 없는 요청은 승격 이력이 있는 허용판만 제공한다.
- Core·Agent·GUI에서 explicit latest fallback을 금지하며 exact 선택은 미승격 허용판도 가능. 구 서버/정적 Catalog의 정책 필드가 없으면 기존 선택 유지.
- schema4 이전 전에 backup을 만들고 old/new server 동시 사용을 금지한다. dry-run은 read-only이며 apply는 기존 공개 순서를 legacy-baseline으로 정확히 한 번 등록한다. 실제 manual 이력과 구분한다.
- DB 변경/기준선 apply는 HTTP 서버 실행 lease와 경합하면 거부한다. unready 기존 DB는 serve/promote 전에 migrate가 필요하다.

현재 CLI와 실행 순서는 [배포 운영](../../distribution-workflow.md)·[기능 지도 B6](../../feature-workflow.md)·[설정](../../guide-03-launcher-usage.md)을 따른다. [정제 결과](release-promotion-evidence.json). 테스트 임시 루트 전체를 공개 artifact로 업로드하지 않는다.

## 재현

```powershell
dotnet test src/UeDtLauncher.Tests/UeDtLauncher.Tests.csproj -c Release
python tools/publish_runtime_cohort.py --output publish/promotion/new-cohort --rid win-x64
python tools/test-release-promotion.py --root publish/promotion/new-proof --server publish/promotion/new-cohort/server/UeDtLauncher.DistributionServer.exe --launcher publish/promotion/new-cohort/launcher/UeDtLauncher.exe --catalog-proof
python tools/test-intranet-auth.py --root publish/promotion/new-http --server publish/promotion/new-cohort/server/UeDtLauncher.DistributionServer.exe --launcher publish/promotion/new-cohort/launcher/UeDtLauncher.exe --agent publish/promotion/new-cohort/agent/UeDtLauncher.Agent.exe --service-proof --promotion-proof
```

CI에는 승격 CLI/API/migration과 HTTP managed pending/running proof를 연결했다. 이번 작업에서 원격 Actions 실행 성공은 확인하지 않았으므로 OPS-06을 자동 완료하지 않는다.
