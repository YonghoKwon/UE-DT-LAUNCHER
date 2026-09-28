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
