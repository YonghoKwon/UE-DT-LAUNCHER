> 검증 이력 보관: 실제 실행 날짜·환경·결과를 보존합니다. 아카이브 이동은 증거 폐기를 뜻하지 않습니다. 현재 진행률은 [개선 대장](../../../../IMPROVEMENTS.md), 현재 사용 절차는 [문서 색인](../../README.md)을 따릅니다.

# 배포 서명·실행 수명 안전성 검증

2026-09-28 / 기준 `548cab8`, 작업 브랜치 `codex/launcher-deployment-safety`. 회사 운영 승인과 별도이다.

1~5절은 각 부분 커밋 당시의 단계별 기록이다. 중간 단계의 ‘아직 연결하지 않음’은 해당 시점의 범위이며 최종 상태는 6절을 따른다.

## 1. MSI 서명 순서

- 공식 진입점에서 서명 환경 검사 → publish → EXE 서명/검증 → MSI 생성/서명 → 내장 EXE 추출/hash/signer 검증 순서를 강제한다.
- 개발 빌드는 UNSIGNED-DEV이며 인증서 환경변수가 있어도 자동 공식 승격하지 않는다. 이번 실행은 개발 빌드만 수행했다.
- 빌드마다 payload/output/WiX intermediate를 분리한다. 검증 성공 후 `runs/<id>/release`에 정확한 파일만 공개하며 CI는 `UE_DT_PACKAGE_DIR`만 사용한다.
- 첫 실제 추출에서 이전 WiX CAB 재사용으로 EXE hash 불일치를 발견했고 gate가 차단했다. intermediate까지 분리한 재실행에서 두 EXE hash가 일치했다.
- `tools/test-windows-packaging.ps1` 계약 11개 통과: 인증서 없음 사전 차단, 순서, 단계별 실패 전파, signer/timestamp gate. 모의 검증은 실제 서명 증거가 아니다.
- 관련 .NET PackagingContractTests 4개 통과. 실제 개발 MSI 빌드 경고/오류 0, 비설치 CAB 추출·두 EXE 비교 성공. publish CLI --version 및 Agent --probe 실제 실행 성공.
- 실행 산출물: `artifacts/deployment-safety/runs/72ed687cc57f49b0804fd017a2dad7ae/release/package-result.json`. 바이너리/추출 파일은 커밋하지 않는다.
- 실제 회사 인증서·timestamp 서비스·설치된 EXE 검증은 미완료: OPS-08 최대 50%.

## 2. 실행 수명 추적

- 기존 EXE의 별도 runtime-host capability 경로를 GUI/self-update보다 먼저 분기했다. 아직 일반 실행/변경 경로에는 연결하지 않았다.
- Windows: suspended 생성 → Job 연결 → resume, breakaway/kill-on-close 없음, active count=0 확인. 아직 resume하지 않은 이번 생성 프로세스만 초기화 실패 시 보유 handle로 정리한다.
- Linux x64: subreaper set/get 확인, 독립 session, native posix_spawn/waitpid. managed Process.Start/wait를 사용하지 않으며 opaque spawn 구조체 크기를 가정하지 않는다.
- 단위 검사 5개 통과. publish Windows 실제 3개 case(direct, 부모 선종료/자식 유지, host crash 후 자식 생존) 통과: `publish/safety/proof-win-01`.
- publish WSL 실제 4개 case(위 3개 + double-fork/setsid 손자 유지) 통과: `/tmp/uedt-runtime-proof-a_c5rcvo`.
- 임의 외부 broker/WMI/D-Bus/systemd spawn은 보장 범위 밖이다. 이 결과는 실행 수명 primitive 증거이며 Agent 등록/설치 변경 차단 완료가 아니다.

## 3. Agent 실행 등록

- OS peer PID/owner/creation identity를 사용하고 설치·시도·token hash·동일 runtime-host를 결속했다. Windows enabled administrator 그룹은 deny-only를 제외하고 검사한다. Linux는 native SO_PEERCRED를 사용한다.
- 관리형 CLI의 Catalog 선택을 Agent로 이동했다. GUI/CLI가 PID를 보호 상태에 직접 쓰지 않으며 티켓은 private stdin pipe로 전달한다.
- RuntimeStore/기존 IPC 관련 Windows 10개, WSL runtime 관련 8개 통과. publish console Agent 등록 시험은 Windows `uedt-broker-proof-m6xd8zgq`, Linux `/tmp/uedt-broker-proof-kt4jq5aq`에서 통과.
- 같은 사용자라도 Python peer는 host 등록/완료를 위조할 수 없었고, 실행 중 두 번째 launch를 거부했으며 정상 후손 종료 후 Quiescent로 전환했다.
- Windows LocalService 실제 계정과 전체 변경 경로 연결은 아직 검증되지 않았다. 변경 차단·구형 클라이언트 gate는 다음 단계이다.

## 4. 설치 변경 조정기

- prepare의 첫 파일 변경 전에 lease/Quiescent 검사를 수행하고, transaction 복구·rollback·CLI/GUI/Agent 복원도 같은 조정기를 사용한다. lock 파일 inode를 삭제하지 않는다.
- 기존 PID/name 기반 종료와 직접 payload 실행 코드를 제거했다. service-run은 활성 버전 변경을 차단하며 health 실패 시 backup을 보존한 manual-recovery 상태를 남긴다.
- 기존 설치 import는 source 실행 상태를 확인하고 별도 staging에서 복사 후 non-overwrite rename한다. 다중 설치 전체 state migration은 검토 없이 자동 적용하지 않는다.
- legacy 상태의 자동 이전은 명시적인 정지 확인 전에는 차단한다. 기존 가짜 파일 테스트는 프로세스가 없다는 fixture 사실을 명시했으며 production 검사를 완화하지 않았다.
- Running/Pending/Unknown의 prepare·rollback·recovery 차단 전후 파일/manifest/state/backup/journal hash 보존 회귀 추가.
- publish Windows Agent에서 실행 중 update/repair 거부 및 파일 불변 확인(`uedt-broker-proof-beyrsvf2`). publish 전체 HTTP E2E의 두 버전 실행·종료/repair 통과(`publish/safety/e2e-win-02`).
- cmd /c 인수 시험에서 불필요한 옵션 인용을 수정했다. payload stdout은 control channel과 분리하므로 실행 증거는 fixture marker 파일로 확인한다.

## 5. 진입점과 사용자 안내

- 구형 capability가 없는 변경 요청은 client-upgrade-required로 거부한다. status/catalog 등 조회와 IPC v1 framing은 유지하며 구형 응답에 capability를 자동 채우지 않는다.
- 일반 GUI는 실행 중 안내와 비활성 기본 버튼을 표시한다. 실제 화면 점검에서 SetBusy(false)가 버튼을 다시 활성화하는 문제를 찾아 수정하고 접근성 트리의 disabled 상태까지 재확인했다.
- publish 일반 GUI 실행 중/종료 후 상태와 개발자 GUI의 기존 명령·버전 표시를 Computer Use로 확인했다(`publish/safety/gui-01`). 앱의 설치/실행 자체는 CLI fixture에서 수행했다.
- 새 .lnk는 정확한 버전의 사용자 소유 설정을 통해 런처를 실행한다. 기존 .url/.lnk는 덮어쓰거나 삭제하지 않는다. COM .lnk 생성과 설정 pinning 테스트 통과.
- host control stream은 payload와 분리하고 CLI stderr 상속으로 부모가 payload 종료까지 기다리는 문제를 수정했다. Linux 보존 FD는 close-on-exec로 설정한다.
- runtime recover는 관리형 관리자/portable 소유자 확인이며 서비스 버전 선택은 --service-selection으로 별도 명시한다. 수동 확인을 OS 종료 증거로 기록하지 않는다.

## 6. 통합 결과와 잔여 검증

| 검증 | 결과 / 증거 |
|---|---|
| Windows / WSL 전체 .NET Release 회귀 | 각각 347개 통과, 빌드·publish 경고/오류 0 |
| MSI 사전 검사·순서·실패 전파 | PowerShell 계약 11개 통과. 실제 서명 인증서 증거 아님 |
| 개발 MSI | 실제 생성, 비설치 CAB 추출, GUI/Agent payload hash 일치. 설치/custom action 실행 없음 |
| Windows native family | direct·부모 선종료/자식 생존·host crash 자식 생존, `publish/safety/proof-win-final` |
| WSL Linux x64 native family | 위 시험 + double-fork/setsid 후손, `/tmp/uedt-runtime-proof-8r8ee5la` |
| 최종 publish Agent broker | Windows `uedt-broker-proof-22pdgmc4`, Linux `/tmp/uedt-broker-proof-noz0tkn8`: 실제 peer 거부, 중복 실행·구형 변경 차단, 실행 중 update/repair 파일 불변, Agent 재시작, 정상 종료, host 장애 후 Unknown 유지 |
| signed HTTP 실제 두 버전 설치·실행·repair | Windows `publish/safety/e2e-win-02`, Linux `/tmp/uedt-intranet-_e00ddqd` |
| HTTPS/Bearer 호환 E2E | 최종 `/tmp/uedt-distribution-e2e.vGCqWV` 통과. 정상 종료 관측 후 repair하고 기존 marker를 제거해 새 실행을 확인. nginx 기본 error-log 경로 경고는 있었으나 isolated config/전송 성공. 기존 대용량 제약 해결을 뜻하지 않음 |
| 일반/개발자 GUI | publish 실제 화면, 실행 중 친화적 안내·기본 버튼 disabled, 종료 후 상태, 개발자 명령 표시 확인. 설치·실행 동작 자체는 CLI로 검증 |
| CI 연결 | 양 OS native family/broker 및 Windows packaging failure gate 추가. 원격 CI는 미실행 |

마지막 보강은 수동 확인 시 살아 있는 정확한 payload 신원도 차단하고, dry-run은 변경 없이 상태를 반환하는 것이다. 두 플랫폼 전체 회귀와 재publish broker 시험을 다시 통과했다. 임시 fixture 경로는 로컬 증거 식별자이며 원시 로그·키·바이너리는 커밋하지 않는다.

### 재현 명령

아래는 저장소 루트에서 실행한다. 각 proof는 자기 임시 폴더와 합성 프로세스를 사용하며 OS 서비스/계정을 만들지 않는다.

```powershell
dotnet test src/UeDtLauncher.Tests -c Release
./tools/test-windows-packaging.ps1
python tools/test-runtime-family.py --launcher publish/safety/win/UeDtLauncher.exe
python tools/test-runtime-broker.py --launcher publish/safety/win/UeDtLauncher.exe --agent publish/safety/win-agent/UeDtLauncher.Agent.exe
```

```bash
dotnet test src/UeDtLauncher.Tests -c Release --artifacts-path publish/safety/linux-artifacts
python3 tools/test-runtime-family.py --launcher publish/safety/linux/UeDtLauncher
python3 tools/test-runtime-broker.py --launcher publish/safety/linux/UeDtLauncher --agent publish/safety/linux-agent/UeDtLauncher.Agent
```

입력은 각 OS의 self-contained single-file publish 결과이다. 실행 파일 하나만 복사하는 broker 시험에 framework-dependent DLL 빌드 경로를 넘기지 않는다.

### 완료로 보지 않은 조건

- OPS-08 **50%**: 실제 회사 인증서·RFC3161 서비스 성공·설치된 EXE 검증은 없다. mock과 개발 MSI로 대체하지 않는다.
- OPS-09 **75%**: 실제 실행 수명·장애·차단 확인은 있지만 모든 상태 저장 실패 지점, 모든 변경 진입점의 publish 장애 주입, service health 실패의 실제 프로세스 시험은 남는다. 현재 guard/서비스 선택 회귀를 이 전체 시험의 완료로 대체하지 않는다.
- Windows LocalService·Linux uedt 실제 설치 서비스·회사 UE/RHEL, GUI 설치/rollback 직접 조작·GUI 종료 중 payload 유지의 별도 화면 시험은 미완료다. native host 부모 분리 시험과 GUI 상태 관측은 구분한다.
- 수동 EXE 실행·외부 WMI/D-Bus/systemd broker·특수 clone은 지원 보장 밖이다. runtime-host는 악성 동일 사용자 격리 경계가 아니다.
- SEC-03/04와 PERF-03의 기존 미달은 유지한다. 세이브 데이터 이전, 자동 서비스 handoff/스케줄, 새 계정·회사 서버 변경, push/PR은 하지 않았다.

[정상/차단/수동 복구 명령](../../runtime-safety.md) / [개선 진행률](../../../../IMPROVEMENTS.md)
