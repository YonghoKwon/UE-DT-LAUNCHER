# 보완 필요 사항

점검: 2026-09-28 / codex/launcher-performance. PERF 항목은 실제 구현·재현 측정 결과로 갱신했습니다. 그 밖의 항목은 2026-09-22 정적 점검 결과이며 이번 성능 작업에서 해결하지 않았습니다.

P0=회사 투입 전 검증 조건, P1=초기 운영 안정성, P2=후속 개선. 우선순위는 제안이며 일정·수치 목표는 미정입니다. 미검증과 미구현을 구분합니다.

## 확정 목표에 따른 진행 순서

목표는 [회사에서 활용 가능한 DT 배포 시스템](PROJECT_GOALS.md)입니다. 아래는 개발·도입 우선순위이며, 새 패키지를 배포하는 명령 순서는 [기능 지도·실행 안내](docs/reference/feature-workflow.md)에서 확인합니다.

| 순서 | 우선 처리 | 다음 단계로 가는 조건 |
|---:|---|---|
| 1 | OPS-08·09, SEC-03 | 서명·프로세스 식별·서비스 credential 접근 안전성 확보 |
| 2 | OPS-01·02, USER-01·05 | 실제 UE·회사 서버·시험 PC에서 설치/실행과 사용자 데이터 보존 확인 |
| 3 | PERF-03, UI-01~03, USER-02 | 회사 규모 성능·화면·오류 대응 기준 통과 |
| 4 | OPS-03~07, SEC-01·02 | 승인/최신판·보관·복원·토큰·무인 운영 기준과 절차 확보 |
| 5 | 제한된 시범 운영·인수 | 목표 문서 G1~G6 결과와 담당자 확인 후 대상 확대 |

## 운영·배포

| ID/우선 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---|---|
| OPS-01/P0 | [기록](docs/reference/distribution-validation.md)은 WSL·테스트 프로그램, 실제 RHEL/UE 미검증 | 실제 회사 TLS/CA·IP·대형 UE Windows/Linux 패키지로 접수→설치→실행→복구 증거 확보 |
| OPS-02/P0 | MSI/RPM 구성은 있으나 이번 통합의 실기기 수명주기 미검증 | 설치/upgrade/repair/uninstall, 서비스 자동 시작·credential ACL·데이터 보존, 코드서명 gate 검증 |
| OPS-03/P1 | DistributionHttp의 track.Last()가 latest. 과거판 승인 시 역행 가능 | 승인과 운영 승격 분리·변경 이력. 과거판 등록 시 latest 유지 및 명시적 변경 테스트 |
| OPS-04/P1 | StorageMaintenance는 scratch만 정리, 공개판·snapshot 누적 | 보관 기간/용량·참조 보호·dry-run·감사 정리. 사용 중 자료 보존 검증 |
| OPS-05/P1 | 수동 백업·복원, catalog sequence 역행 위험 | 일관된 백업/복원 도구·sequence 보호·복구 훈련. 기존 PC 검증 성공, RPO/RTO 별도 합의 |
| OPS-06/P1 | Linux signed HTTPS E2E가 build.yml에 추가됨. 원격 CI 실행·운영 결과 보관은 미확인 | 실제 PR/배포 CI 성공 확인 및 민감정보 없는 결과 보관 정책 확정 |
| OPS-07/P1 | AgentWorker는 IPC 대기만 하고 service-run은 once=true. CLI는 managed 반복 실행을 거부 | Agent 스케줄러 또는 명시적 외부 스케줄 운영을 확정. 재부팅 후 정기 점검·중복 작업 방지·maintenance window 시험 |
| OPS-08/P0 | release.yml은 MSI 생성 후 payload EXE 서명. Product.wxs는 embedded CAB이므로 뒤늦은 EXE 서명이 MSI 내부에 반영되지 않음 | EXE 서명 → 서명 검증 → MSI 생성 → MSI 서명 순서로 변경. 실제 설치된 GUI/Agent EXE와 MSI의 서명을 각각 검증 |
| OPS-09/P0 | ServiceRunner가 PID 검증에 이름 부분 일치, fallback으로 동명 첫 프로세스를 사용. 다중 버전에서 다른 프로세스 선택 위험 | 실행 경로·시작 시각·설치 식별자로 프로세스 결속. 동명 다른 버전/무관 앱을 종료하지 않는 회귀 테스트 |

## 성능·저장 공간

| ID/우선 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---|---|
| PERF-01/P1 | 재현 환경 완료: bounded 다운로드/해시, 관리형 상태 검사, 실제 네트워크 속도·논리 진행률 구분. 작은 파일 설치 10.96초→5.98초 | `94a4f9e`, `1e29454`; 실제 UE·회사 PC 기준선은 OPS-01에서 계속 확인 |
| PERF-02/P1 | 재현 환경 완료: 인증된 대상 기준 최근 3개 설치에서 독립 복사. 90% 동일 데이터의 전송량 90% 절감 | `417b9ec`, `638028a`; 원본 독립성·손상 fallback·repair 재다운로드 검증. 공용 캐시는 범위 밖 |
| PERF-03/P1 | 부분 완료: 토큰 매 요청 검사, 정책 파싱 재사용, exact 조회, fresh sequence 유지. 30개 연결 p95 833.44→81.23ms·실패32→0 | `10ec1f9`, `d152477`; **10개 연결 혼합 p95 5.43→24.66ms 악화**, 시간 gate 미달. 기본 운영 적용 승인 보류, 혼합 읽기/쓰기 지연 추가 개선 필요 |
| PERF-04/P2 | 재현 환경 완료: 진행/공간 점검, schema 백업, OS 작업 잠금·cleanup 보호·공정 큐. 동시 HTTP 중 4개 ZIP 접수 중앙값 1 worker 5.02초 / 2 workers 2.59초 | `d3f8d68`, `f12a861`, `d152477`; 2 worker 편차·응답 지연도 보고. 기본1 유지, 실환경 자원 기준선 별도 |

상세 조건·반복 횟수·부분 미달 항목은 [성능 검증 기록](docs/reference/performance-validation.md)을 따릅니다. WSL1 nginx의 큰 파일 중단은 직접 API 정상/프록시 경유 실패로 분리 관측했고, 실제 RHEL nginx 대용량 검증은 미완료입니다. 이 제한을 작은 파일 E2E 통과로 대체하지 않습니다.

## 사용자 관점

| ID/우선 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---|---|
| USER-01/P1 | LegacyInstallImport 원본 보존 복사, UE 저장 위치는 프로젝트별 | 세이브·설정 위치 계약·이전 dry-run·백업. 실제 버전 전환/rollback 후 데이터 유지 |
| USER-02/P1 | 서버/Agent/토큰/공개키 초기 설정 단계가 많음 | 관리자 사전 점검·설정 검증, 사용자 오류 코드/조치 안내. 새 PC 최초 실행·비밀정보 미노출 |
| USER-03/P2 | 엔진 취소 토큰과 별개로 일반 UX 취소는 이전 범위에서 제외 | 안전 중단 지점·취소/재개 설계. 다운로드·검증·설치별 중단 후 손상 없음 |
| USER-04/P2 | 오프라인·권한 폐기 후 기설치 실행 최종 정책 미정 | 실행/권한 재확인 규칙 합의. 미설치·기설치·폐기 토큰 수용 테스트 |
| USER-05/P1 | sample-config와 build-rpm.sh의 기본 설정은 정적 catalog 예시. 새 통합 서버 초기 설정과 불일치 | DistributionServer용 설정 생성 및 설치 템플릿 제공. 생성 직후 URL·키·credential 누락 진단과 최초 연결 시험 |

## UI·접근성

| ID/우선 | 현재 상태·영향 | 보완 방향·완료 조건 |
|---|---|---|
| UI-01/P1 | 반응형·fallback 기초 구현, 최신 통합의 전체 조건 재검증 필요 | 긴 한글명·오류·여러 버전·빈 권한 목록, 1280×720/1366×768/1920×1080 및 100/125/150%에서 핵심 버튼·진행률 확인 |
| UI-02/P1 | 빈 목록·서비스 단절·권한 오류 원인이 다름 | 401/403/검사 실패 문구·재시도 일관화. 친화적 행동 안내와 개발자 진단에 같은 오류 ID |
| UI-03/P2 | 키보드·접근성 속성 기초 존재 | 실제 스크린리더·Tab/Shift+Tab·고대비 검증. 마우스 없이 핵심 흐름 수행 |
| UI-04/P2 | 트레이·완료 알림은 이전 범위에서 제외 | 필요성 합의 후 opt-in 구현. 닫기/종료 의미·알림 설정 명확화 |

## 보안·확장·유지보수

| ID/우선 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---|---|
| SEC-01/P1 | DistributionTokens 발급·PC 단위 전체 폐기, 만료/개별 관리 없음 | 만료·토큰별 폐기·순환·감사 설계. 비밀 노출 없는 교체/만료/폐기 시험 |
| SEC-02/P1 | loopback nginx 신뢰와 직접 사내 IP 전제 | NAT/추가 proxy의 실제 IP 계약·rate limit 검토. 위조 헤더 차단과 회사 망 경로별 검증 |
| SEC-03/P0 | CommercialSecurity는 Linux credential을 0600으로 저장하지만 서비스 계정 소유권을 설정하지 않음. root 등록 시 uedt가 못 읽을 수 있음 | 등록 계정/서비스 계정별 소유권 검증·안전한 provisioning. 서비스 실제 읽기 성공 및 무관 계정 읽기 실패 시험 |
| EXT-01/P2 | IAccessPolicyProvider 파일 구현만 존재 | 회사 API 합의 후 timeout/cache TTL/기본 거부. 장애·취소·오래된 응답에서 권한 확대 없음 |
| DEV-01/P2 | Core 링크 컴파일, MainWindow 동작 코드 잔존 | 기능 변경과 분리한 물리 폴더·ViewModel 정리. API/CLI/IPC 회귀 없음 |

근거 소스: [서버 API](src/UeDtLauncher.DistributionServer/DistributionHttp.cs), [저장 관리](src/UeDtLauncher.DistributionServer/StorageMaintenance.cs), [권한](src/UeDtLauncher.DistributionServer/AccessPolicy.cs), [엔진](src/UeDtLauncher/LauncherEngine.cs), [버전 경로](src/UeDtLauncher.Core/Distribution/VersionedReleasePaths.cs), [CI](.github/workflows/build.yml).

추가 교차 확인: [AgentWorker](src/UeDtLauncher.Agent/Program.cs), [Agent 작업](src/UeDtLauncher.Agent/AgentIpcHostedService.cs), [서명 순서](.github/workflows/release.yml), [MSI CAB](installer/windows/Product.wxs), [RPM 설정](scripts/build-rpm.sh), [credential 저장](src/UeDtLauncher/CommercialSecurity.cs), [프로세스 식별](src/UeDtLauncher/ServiceRunner.cs). 위 항목은 코드에서 확인한 차이이며 이번에 수정한 것은 문서뿐입니다.

## 처리 제안과 상태 관리

1. OPS-08/09·SEC-03: 설치 payload 서명·프로세스 오인·credential 접근 문제 우선 보완. 이어 OPS-01/02·USER-01의 회사 환경·실제 패키지·데이터 보존 검증.
2. OPS-03/04/05·SEC-01: 최신판·보관·복원·토큰 운영 사고 예방.
3. PERF 기준선·OPS-06: 측정 후 개선, 자동 회귀 검증.
4. USER/UI·API 범위는 [목표 문서](PROJECT_GOALS.md)에서 확정 후 진행.

PERF-01/02/04는 위 재현 범위에서 완료, PERF-03은 부분 완료입니다. 그 외 항목은 열림 상태를 유지합니다. OPS-06은 이번에 기능 E2E CI 단계만 추가했으며 원격 CI 실행은 푸시 후 확인해야 합니다. 완료 시 ID에 구현 커밋·환경·검증 링크를 기록하고 README·참고 가이드를 갱신합니다. 빌드 성공만으로 완료하지 않습니다.
