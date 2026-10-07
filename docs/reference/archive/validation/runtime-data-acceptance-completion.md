# 데이터 계획 검증과 실제 UE 직접 조작 수용 후속

2026-10-02 / `codex/runtime-data-acceptance-completion`, 시작57add9b.

## 확인된 결과

제품 후보 `cf99ba1353ae32659a1e636a4e97bc624c454b44`의 productSourceDirty=false, source inventory SHA-256 `274981093e49423f740b0330c8b24709be4f2a4c42da356ccf917085ed7360a0`.

| 검증 | 결과 |
|---|---|
| Windows/WSL 전체 회귀 | 각552통과·skip0, 기존542+신규10 |
| 관련 runtime data 테스트 | 42통과 |
| fixture Python 회귀 | 12통과 |
| Release/publish | 양 OS GUI/Agent/server, warning/error0 |
| 실제 게시 runtime-host | 합성 v1/v2의 UserDir·로그·Quiescent 확인, 실제 UE save 증거 아님 |
| HTTP 서명 | Windows `publish/data-acceptance/http-e2e-01`, WSL `/tmp/uedt-intranet-5uulo16z` 통과 |
| HTTPS/Bearer/nginx | WSL `/tmp/uedt-distribution-e2e.ecVGts` 통과. 기본 log 부재 alert는 기존과 동일, 전역 폴더 변경 없음 |
| 관리형/portable 준비 | 새 managed-01/portable-01, v1 공개·v2 pending, CLI 선설치 없음 |
| 실제 관리형 일반 화면 읽기 | 업데이트 서비스 정상, 프로젝트 `MA0T10 DT — managed managed-01`, 미설치/v1/설치 후 실행 확인 |
| 실제 GUI 적용·SaveGame | **사용자 직접 조작 결과 대기 / 미실행** |

Windows binary SHA-256: launcher `3d30e84322089115fc1f30125fc4142f5a466b18a8a8590476d34d518ebfa897`, Agent `116250fb6400ceaeaccec85b46985171a64e38d22873f69fc6fc67b460914856`, server `e2af0e81ff49835ccffac17334695672c29ef68b85cb8fe97570419c19152fb6`.

## 수정과 보호

- Agent begin 응답의 exact selection을 host 세션에 고정. 최신 목록 변경이 기존 티켓 선택을 변경하지 않는다.
- runtime plan을 installation ID뿐 아니라 실제 설치 경로와 ReleaseId에 대조. legacy restore context도 설치 경로의 다섯 릴리스 구성요소로 확인한다.
- rootDirectory 필드 누락/잘못된 JSON kind를 거부. 명시적 null 기본 루트와 구형 runtimeData 없는 정상 기록은 지원한다.
- Agent profile과 user host profile의 credential 경로 모두 보호한다.
- Linux sticky 예외는 root 소유 공용 상위 ancestor에만 허용. 실제 데이터 namespace의 group/other write는 거부한다.
- UserDir·log directory에서 각각 create/write/flush 검사. 실패하면 payload spawn 전 종료하고 pending/unknown 자동 정상화는 없다.

## 직접 조작 협업과 관측

자동 입력 사전 Tab 시험이 `GetCursorPos failed 0x80070005`로 거부됐다. 합의에 따라 사용자에게 위 정확한 managed-01 창에서 설치 후 실행을 누르고 UE 창을 유지하도록 안내했다. 사용자 응답 전에는 설치·실행 성공으로 기록하지 않는다. 앞선 후보/CLI 결과를 GUI로 합산하지 않는다.

다음 실제 순서: v1 설치/실행 → UE 패널 설정 저장/정상 종료·재실행 → 런처 종료 수명 → v2 승인/업데이트 → v2 독립 설정 → Running 변경 차단 → 정상 종료/시험 텍스트 손상/GUI repair → 정상 상태 추가 force repair/전체 backup hash → 재손상/복원 취소·적용 → preview 변경 거부 → v1/v2 재선택. portable에서도 동일 흐름을 확인한다.

기존 패키지의 바이너리는 동일하고 FixtureAcceptance 비실행 파일만 다른 두 릴리스다. SaveGame이 실제 생기거나 복원되는 증거가 없으면 미검증으로 남긴다. DTCore CustomLogs의 설치 내부 쓰기는 미해결이며 이번 UE 소스 수정은 없다.

저장소 루트 PowerShell, 이미 harness가 실행 중이면 resume을 중복 실행하지 않는다:

```powershell
python tools/prepare_versioned_real_ue_fixture.py --root publish/data-acceptance/managed-01 observe --version 0.1.0-ue-test.v1
python tools/prepare_versioned_real_ue_fixture.py --root publish/data-acceptance/managed-01 snapshot --version 0.1.0-ue-test.v1 --name v1-data --scope data
python tools/prepare_versioned_real_ue_fixture.py --root publish/data-acceptance/managed-01 compare --version 0.1.0-ue-test.v1 --name v1-data --scope data
python tools/prepare_versioned_real_ue_fixture.py --root publish/data-acceptance/managed-01 approve-v2
```

`observe`는 publish CLI의 runtime 상태와 Manifest/비Manifest/data/log 목록을 private evidence로 저장한다. `protected`는 관리 파일·설치 metadata·backup/journal, `data`는 선택 버전 UserDir만 비교한다. `all`은 runtime JSON/실행 로그를 포함하므로 정상 실행에도 달라질 수 있다. Manifest의 Logs 파일을 전체 제외하지 않는다. 사용자 조작/agent 조작 주체는 각 실제 case에 기록한다.

## 남은 상태

코드 보강/자동화/게시 프로세스 확인 완료, 실제 GUI·데이터 수용 대기. USER-01은50%, OPS-01은50%, UI 진행률은 기존 합의를 유지한다. 사용자 직접 조작 협업 완료 후 실제 범위에 맞춰 USER-01을75%로 갱신할 수 있으나 회사 운영/다른 UE 빌드/데이터 이전 완료는 아니다.

[이전 데이터 경로 기록](real-ue-data-safety-validation.md) · [설정 안내](../../guide-03-launcher-usage.md) · [개선 대장](../../../../IMPROVEMENTS.md)
