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

구현/검증 대기. Windows Job Object와 Linux subreaper의 실제 자식 수명 시험 후에만 변경 경로에 연결한다. API 존재만으로 완료로 표시하지 않는다.
