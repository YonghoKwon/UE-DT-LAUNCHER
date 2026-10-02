# 실행 중 변경 차단과 수동 복구

2026-09-28 / `0d12957` 기준 현재 가이드. 앱을 자동 종료하거나 다른 버전으로 자동 전환하지 않는 안전성 단계이다. 실제 회사 UE/서비스 계정 검증은 별도이다.

## 사용자에게 달라지는 점

| 상태 | 런처 동작 | 사용자가 할 일 |
|---|---|---|
| 처음 설치·정상 종료 확인 | 설치/업데이트/복구 가능 | 기존 버튼 사용 |
| 실행 중 | 변경·같은 버전 중복 실행 차단 | 프로그램을 정상 종료하고 상태 새로고침 |
| 실행 승인 진행 중 | Pending으로 변경 차단 | 잠시 기다린 뒤 다시 확인 |
| host 장애·기존 PID 기록·추적 불명 | Unknown으로 변경 차단 | 관리자 점검 요청 |
| 구형 런처/Agent | 조회는 유지, 위험 요청은 업그레이드 안내 | 두 프로그램을 함께 갱신 |

GUI를 닫는 것과 DT 앱을 종료하는 것은 다르다. runtime-host는 일반 자식 프로세스가 모두 종료될 때까지 유지된다. Windows Job과 Linux subreaper는 임의 WMI/D-Bus/systemd broker나 악성 동일 사용자 격리를 보장하지 않는다. 직접 EXE/오래된 바로가기를 통한 실행은 지원 경로 밖이다.

관리형 GUI는 실행 후 runtime 상태를 다시 확인합니다. 정보가 누락되거나 실행 상태가 불명확하면 정상 완료로 표시하지 않습니다. 문제 해결도 실행 상태 차단을 유지하며, 단지 실행 중이라는 이유로 rollback을 제안하지 않습니다. [후속 GUI 검증 범위](archive/validation/managed-gui-safety-validation.md)

## 확인 → 종료 → 복구

아래 `<config>`는 실제 설정 파일, 버전/프로젝트는 해당 설치와 일치해야 한다. 관리형은 로컬 관리자만 확인 적용 가능하며 portable은 설치 소유자(또는 관리자)만 가능하다.

```text
UeDtLauncher runtime inspect --config <config> --version 1.2.0
UeDtLauncher runtime recover --config <config> --version 1.2.0 --dry-run
```

모든 해당 프로그램·자식 프로세스를 정상 종료하고 운영자가 확인한 뒤에만:

```text
UeDtLauncher runtime recover --config <config> --version 1.2.0 --confirm-stopped
```

- inspect/dry-run은 파일/폴더를 생성하지 않는다. 누락·중복·손상된 runtime 기록은 Unknown이며 자동 정상화하지 않는다.
- 살아 있는 확인된 host/payload는 확인 적용으로 지우지 않는다. 자동 kill이나 PID 이름 검색은 없다.
- 기존 runtime 기록은 보존 사본을 남긴다. 결과는 `operator-confirmed`로 기록하며 OS가 증명한 종료라고 표시하지 않는다.
- 일반 사용자가 pending 파일·PID 파일을 삭제해서 우회하지 않는다. 관리자 확인 후 기존 UI에서 재시도한다.
- versioned 배포는 정확한 버전이 필요하다. direct manifest 모드는 해당 config의 설치 경로를 사용한다.

## 무인 서비스의 버전 선택

`service-run`은 실행 중·불명·활성 버전 변경이면 작업을 중단한다. health 실패 후에도 자동 종료/rollback/restart하지 않고 backup과 `service-state.json`의 점검 상태를 보존한다. 시작 전에 StartupHealthPending을 기록하고 health 성공 저장 후에만 Ready가 된다. pending 상태로 재시작하면 수동 점검을 요구한다. 같은 버전의 정지 상태에서만 기존 설정에 따라 실행할 수 있다.

운영자가 정지와 복구 방안을 확인하고 **서비스의 다음 버전 선택**을 명시할 때:

```text
UeDtLauncher runtime recover --config <config> --version 1.2.0 --confirm-stopped --service-selection
```

이 명령은 버전 파일을 자동 설치하지 않는다. 선택/수동 확인 후 별도 update 또는 service-run을 수행한다. 이전 활성 설치와 새 대상 모두 정지 상태여야 하며 실행 중/불명이면 선택을 변경하지 않는다. 새 snapshot과 수동 확인을 조정기로 처리하고 중간 실패는 NeedsReview로 남긴다. 이전 snapshot 사본과 legacy active.json/failure.json은 보존하며 기존 파일의 자동 통합은 하지 않는다. 자동 스케줄·handoff는 OPS-07 및 후속 범위이다.

## 기존 설치와 바로가기

- legacy 상태 자동 이전은 정지 확인 전 중단된다. `runtime` 명령은 자동 이전 없이 먼저 점검할 수 있다.
- `import-install --apply`는 source 상태를 확인한 뒤 staging에 복사하고 기존 대상에 병합/덮어쓰기하지 않는다. 원본과 사용자 파일은 삭제하지 않는다.
- `UeDtLauncher.Agent migrate --config <config> --dry-run`은 CanApply=false와 차단 이유를 표시한다. 같은 InstallDir를 공유하는 `--apply`는 단일 설치라도 차단하며 target config/state를 만들지 않는다. 기존 portable 자료를 삭제하거나 이미 공유된 구성을 자동 수정하지 않는다.
- 새 Windows `.lnk`는 사용자 소유의 정확한 버전 설정으로 런처를 호출한다. 기존 `.url/.lnk`를 자동 교체하지 않으므로 관리자가 사용 여부를 정리한다.
- USER-01의 opt-in 사용자/버전별 UE UserDir/abslog는 추가됐지만 세이브 디렉터리 이전·형식 변환·버전 공유·데이터 snapshot 복원은 구현하지 않았다. payload rollback은 사용자 데이터를 되감지 않는다. [설정·제약](guide-03-launcher-usage.md)

## 설치본 제작

```powershell
./scripts/build-windows-installer.ps1 -Version 1.2.0
./scripts/build-windows-installer.ps1 -Version 1.2.0 -OfficialBuild
```

첫 명령은 UNSIGNED-DEV이다. 두 번째는 사용 가능한 코드서명 인증서/개인키/EKU와 timestamp 검증이 없으면 실패한다. 공식 EXE 서명 뒤 MSI를 만들고, 내장 EXE hash/signer 검증 후에만 `runs/<실행ID>/release`를 공개한다. `package-result.json`의 정확한 경로를 사용하고 이전 MSI wildcard를 사용하지 않는다. MSI를 실제 설치한 검증은 별도이다.

[runtime 후속 이력](archive/validation/runtime-safety-completion-validation.md) / [최신 GUI 이력](archive/validation/managed-gui-safety-validation.md) / [현재 개선률](../../IMPROVEMENTS.md)
