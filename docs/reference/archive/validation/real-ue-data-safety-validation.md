# 사용자·버전별 UE 데이터 경로와 실제 UE 수용 준비

검증일: 2026-10-02 / 브랜치 `codex/real-ue-data-safety` / 시작 `ad8a6fb`.

**구현·자동화·게시 프로세스 시험은 완료했지만 새 후보의 실제 UE GUI 적용 수용은 미실행이다.** Windows Computer Use에서 화면 캡처가 `0x80070057`, 입력이 `GetCursorPos 0x80070005`로 실패했다. 창 접근성 텍스트의 업데이트 서비스 정상·미설치·v1·설치 후 실행은 확인했으나 클릭/키보드 적용을 실행했다고 기록하지 않는다. 사용자에게 데스크톱 조작 환경 확인을 요청했고 실제 설치를 CLI로 대신하지 않았다.

## 1. 제품·입력 고정

| 대상 | 근거 |
|---|---|
| 제품 소스 | `64c4ad0b49fa24f66065adcd3b891513ac71ec8c`, productSourceDirty=false |
| 제품 source inventory SHA-256 | `c110d118030da9da3adf6e17e5e1315ae6552b5855dac841eb60a557c34c64e1` |
| Windows GUI SHA-256 | `9fbc3830e370d56998b2f5523fe9811e5673b7b25fea4c29148dde61eafe35ea` |
| Windows Agent SHA-256 | `aa5efd225eff921d23952de341ae83bb05711a2e9d034b3712e7787ab4df8020` |
| Windows server SHA-256 | `9a08c3cbac96f482bb6013a3354e5d9fbb38a79cf67d0c1ca328789e1cef74e9` |
| 원본 UE 패키지 | 기존 2026-09-29 ma0t10_dt Windows Development, 328파일/878409923bytes. [빌드 출처](real-ue-package-validation.md) |
| 원본 inventory SHA-256 | `955c6eb1d367a4eb1b5aa1783a6ac73f89390b80ee462877030c915efaddf4db` |
| 시험 릴리스 | `0.1.0-ue-test.v1`, `0.1.0-ue-test.v2`, 각330 Manifest 파일. UE 실행 파일은 동일하고 FixtureAcceptance 텍스트만 변경/추가/삭제 |
| 관리형 ZIP SHA-256 | v1 `a70690ec17c424ba7245402b014327376e84c99949a4ca595362d6bac2242cbb`, v2 `93b58737581416dc664b5b663517fea777c96d734dcb119e9c0c697a680312d2` |

ZIP timestamps가 다른 fixture의 ZIP hash 차이를 실제 UE 빌드 차이로 해석하지 않는다. 원본 파일 hash는 아카이빙 전후 및 ZIP 각 멤버와 대조한다. 현재 UE checkout/DTCore는 다른 작업 중 변경될 수 있으며 원본 패키지의 과거 빌드 출처와 같다고 가정하지 않는다. 이번 작업은 UE 저장소를 수정/커밋하지 않았다.

## 2. 결과와 시행착오

| 검증 | 결과 | 범위/제약 |
|---|---|---|
| Windows 전체 회귀 | 542통과, skip0 | Release, 신규32개 포함 |
| WSL 전체 회귀 | 542통과, skip0 | Ubuntu22.04, 기존 SDK8.0.424, 시험 전용 native library 경로 |
| 제품 publish | Windows/Linux GUI/Agent/server 성공, 빌드 경고/오류0 | 개발 산출물, 서비스 설치/코드서명 없음 |
| Python UE fixture 회귀 | 11통과 | 원본 불변·버전/경로·손상 제한·cohort 변경/dirty 거부 |
| 게시 runtime-host 데이터 smoke | Windows/Linux 합성 v1/v2 모두통과 | 실제 native spawn, 서로 다른 UserDir/실행별 로그 사용, 정상 Quiescent. 실제 UE 세이브 검증 아님 |
| HTTP 요청 서명 E2E | Windows/WSL 통과 | 합성 설치/실행/repair·개별 키 폐기·정확한 서비스 선택·Agent/doctor. Windows 루트 `publish/real-ue-data/http-e2e-01` |
| HTTPS/Bearer/nginx E2E | WSL 통과 | 두 버전·Range·권한/IP 헤더 거부·토큰 폐기·실행 권한·repair. RHEL 또는 대용량 nginx 인수 아님 |
| 실제 UE 포함 접수/승인 준비 | 관리형/portable v1 게시·v2 pending·미설치 확인 | GUI 수용 시작 전이며 UE 실행하지 않음 |
| 일반 GUI 표시 | 접근성 텍스트 확인 | 후보64c4ad0 관리형 정상 서비스/v1/미설치 표시. 스크린샷·적용 미검증 |
| 실제 UE 설치/업데이트/복구/복원 | **미실행: 입력 환경 제약** | 이전 후보 성공 합산 금지, CLI 설치 대체 없음 |
| 실제 UE SaveGame/사용자 설정 보존 | **미실행** | 합성 sentinel/host 성공으로 대체하지 않음 |

시행착오를 제외한 성공만 합산하지 않았다.

- 첫 Windows 전체527개 중 기존 교차 프로세스 잠금 시험1건이15초 timeout이었다. 같은 시험 단독/전체 재실행 모두 통과했고 최종542개도 통과했다.
- Windows temp 폴더의 unrelated writable ACL은 새 보호 검사에 의해 거부됐다. 제품 검사를 낮추지 않고 새 시험 namespace에만 제한 ACL을 적용했다.
- WSL 최초542개는 fontconfig 누락으로 UI93건 실패했다. 다음 실행은 시험 mirror에 examples/installer/CI 파일이 빠져6건 실패했다. 네이티브 라이브러리를 새 private 폴더에 추출하고 mirror 구성을 수정한 최종542개는 통과했다. apt install/서비스/계정/전역 library 경로는 변경하지 않았다.
- 한 WSL mirror 명령에서 shell 변수 전달이 실패해 루트 디렉터리 복사를 시도했으나 모든 관련 쓰기는 권한 거부됐다. 검증에 사용하지 않았고 고정된 스크립트·검증된 /tmp namespace로 대체했다.
- portable 첫 준비의 디렉터리 move가 한 번 거부돼 publishing journal이 남았다. 동일 작업의 정상 `approve` 재실행으로 게시가 복구됐으며, 별도 `accept-portable-02`는 전체 준비에 성공했다. 원인은 확정하지 않았으며 무오류 결과로 계산하지 않는다.
- WSL nginx config test는 기본 `/var/log/nginx/error.log` 부재 alert를 출력했으나 전용 설정 syntax/E2E는 통과했다. 전역 로그 폴더를 생성하지 않았다. 시험용 추출 nginx/라이브러리 버전은 회사 배포용 보안 승인이나 권장 버전이 아니다.

## 3. 구현 계약

- schema3의 runtimeData opt-in만 지원하고 기존 설정은 변경하지 않는다.
- Agent의 보호 설정 → attempt/installation/OS owner에 결속된 plan → 인증된 사용자 host가 외부 데이터 준비 → native spawn 순서다.
- 기본 사용자 루트 또는 명시적 absolute root 아래 owner hash/ReleaseId를 사용한다. 링크·설치/backup/credential 중첩·unsafe 권한·기존 UE 인수 충돌은 거부하고 fallback하지 않는다.
- Windows ACL/Linux0700은 새 데이터 폴더에 적용한다. 기존 사용자 폴더 ACL을 재귀 수정하지 않는다.
- Running/Pending/Unknown은 기존 공통 변경 차단을 유지한다. 데이터는 payload rollback/recovery/prune 대상이 아니며 자동 이전/공유/데이터 복원은 없다.
- 기존 DTCore의 LaunchDir/Logs/CustomLogs 쓰기는 남는다. UserDir만으로 해결했다고 표시하지 않는다.
- 상태 기록의 runtimeData가 불완전하거나 attempt/installation 관계가 틀리면 Unknown이다. 실제 서비스 계정의 데이터/credential 접근은 별도 검증이다.

## 4. 재개 명령과 실제 GUI 순서

저장소 루트 PowerShell, 동시에 같은 fixture harness를 두 개 시작하지 않는다. 준비된 fixture는 이미 v1 승인/v2 대기이며 CLI 선설치가 없다. `resume`은 기록된 cohort·설정·loopback origin을 다시 검증한 뒤 서버/sink와 관리형 Agent를 시작한다. portable에는 Agent가 없다.

```powershell
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 resume
# 위 프로세스는 유지하고 별도 터미널에서:
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 open-general
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 status
```

| 단계 | 담당/입력 | 확인할 결과 |
|---:|---|---|
| 1 | 조작 가능한 Windows 환경 확보, 필요한 적용 직전 확인 | 원래 OS 설정 유지, 격리 fixture만 대상 |
| 2 | 일반 GUI 설치 후 실행 | 실제 UE v1·Manifest330개·UserDir/로그 위치·Running |
| 3 | 런처 종료/재실행, UE 정상 종료 | 동일 자식 유지, 이후 supervisor-completed/Quiescent |
| 4 | 관리자 도구 `approve-v2`, GUI 상태 확인 | 기존v1/최신v2/업데이트 안내, 조회 자동 설치 없음 |
| 5 | 일반 GUI 업데이트 후 실행 | v2 경로·데이터 분리, v1 보존, 실행 중 변경 버튼 차단 |
| 6 | UE 정상 종료 → `damage --version 0.1.0-ue-test.v2` → 개발자 GUI 복구 | 지정 시험 텍스트만 손상/복구, 데이터·v1 보존 |
| 7 | 정상 상태에서 GUI force repair → `verify-backup` | 최신 backup의 모든 Manifest 파일 정상 |
| 8 | 재손상 → 복원 취소/새 확인/적용 | 취소 불변, 정확한 v2 backup 복원, 외부 사용자 데이터 유지 |
| 9 | 확인창 후 `invalidate-preview` → 적용 시도 | 거부·파일 불변·새 확인 필수 |
| 10 | 개발자 v1 재선택/실행, portable에서 동일 흐름 | v1은 별도 데이터 사용. same-v2 backup 복원과 v1 선택 전환을 구분 |

```powershell
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 approve-v2
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 verify --version 0.1.0-ue-test.v2
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 snapshot --version 0.1.0-ue-test.v2 --name before-restore
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 compare --version 0.1.0-ue-test.v2 --name before-restore
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 verify-backup --version 0.1.0-ue-test.v2
python tools/prepare_versioned_real_ue_fixture.py --root publish/real-ue-data/accept-managed-01 stop-services
```

portable은 위 root를 `publish/real-ue-data/accept-portable-02`로 바꾼다. 미설치이거나 runtime 기록이 없으면 damage/preview 변경을 거부한다. 도구는 Windows 설치 lock을 잡은 상태에서 정지 재확인과 시험 변경을 한다. snapshot은 관리 파일·state·외부 data·비Manifest 파일을 분리 기록하며 전체 equality는 런타임 로그가 계속 쓰이는 중에는 달라질 수 있다. 차이를 숨기기 위해 Logs/Saved 전체를 제외하지 않는다.

## 5. 판정

USER-01은50%(런처 정책/구현/회귀/게시 합성 실행), OPS-01은50% 유지. UI·OPS·SEC·PERF의 다른 진행률은 자동 변경하지 않는다. 회사 UE/RHEL/서비스 계정, 실제 다른 UE 빌드, 자동 데이터 이전, 앱 custom log 분리, 실제 GUI 적용은 남는다. 실제 회사 운영 승인 또는 제품 완성 선언이 아니다.

[정제 JSON](real-ue-data-safety-evidence.json) · [현재 설정](../../guide-03-launcher-usage.md) · [개선 대장](../../../../IMPROVEMENTS.md)
