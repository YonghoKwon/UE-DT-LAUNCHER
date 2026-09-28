# 배포 서명·실행 수명 안전성 검증

2026-09-28 / 기준 `548cab8`, 작업 브랜치 `codex/launcher-deployment-safety`. 회사 운영 승인과 별도이다.

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
