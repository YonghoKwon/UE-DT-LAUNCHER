# 개선 진행 현황과 보완 필요 사항

점검: 2026-10-03 / `codex/launcher-readiness-ux`. USER-02의 준비도 진단·읽기 전용 검사·Agent 선택 결속·조치 안내를 구현하고 Windows/WSL 각587개를 통과했습니다. 합성 GUI 양 모드의 설치·실행·수명·복구·정상 복원·취소·preview 거부·조회 재시도를 직접 확인했습니다. 주 화면 조치 문구를 보강한 뒤 새 게시본의 영향받은 오류 화면을 다시 확인했으며, 이전 후보의 전체 성공을 새 후보 전체 수용으로 합산하지 않습니다. [이번 증거](docs/reference/archive/validation/readiness-validation.md). 실제 UE 데이터·회사 환경은 별도 미완료입니다.

P0=회사 투입 전 검증 조건, P1=초기 운영 안정성, P2=후속 개선. 우선순위는 제안이며 일정·수치 목표는 미정입니다. 미검증과 미구현을 구분합니다.

## 먼저 확인할 진행 현황

2026-10-03 headless 후속 진행: 현재 계약으로 측정 도구를 정상화하고 fe10488 Windows 게시 HTTP 서명 E2E·80MiB 최초/변경 없음/다음 버전/repair·1/10/30 연결을 수행했다. 90% 콘텐츠 재사용 유지. 최종 후보 비교 전 PERF-03/SEC-04 진행률은 유지한다. [현재 검증](docs/reference/archive/validation/headless-operations-validation.md). GUI 수용은 중단 중이다.

2026-10-03 빠른 개선: `4a37061` 문서 정합성, `f215e62` 진단 근거 보존/모순 검증·중복 안내 수정, `9788de1` 준비도 CI 연결을 완료했다. Windows/WSL 각601개, 게시 CLI/Agent 준비도 각8개·요약 계약3개·fixture 계약13개 통과. 세부 체크포인트 완료이며 GUI 중단/원격 CI 조건으로 USER-02·UI-02는75%, OPS-06은50% 유지. [정제 결과와 조건](docs/reference/archive/validation/quick-wins-validation.md)

2026-10-03 최초 연결 진단 후속: USER-02는0%→75%. 최신 소스d665d67 Windows/WSL 각587개와 실제 게시 E2E 통과, 이전 후보의 양 모드/두 프로필 설치·실행·수명·복구·복원·오류/설정 재시도를 직접 확인했다. 일반 오류의 다음 조치를 보강한 새 후보는 양 모드403 화면을 확인했다. 사용자 요청으로 마우스·GUI 자동 검증은 잠시 중단했으며, 새 후보 전체 수용 전까지 USER-02·UI-02는75%다. [게시본별 증거](docs/reference/archive/validation/readiness-validation.md)

2026-10-02 OPS-03 완료(로컬 범위): 승인/승격 분리, revision·감사·schema4 기준선 이전, 현재 권한별 추천·구형 요청 제한, Core/Agent/GUI explicit policy 처리를 검증했다. 후보a8080ea Windows/WSL 각568개·게시본 이력/이전/실행 티켓·Running 불변·HTTP/HTTPS 통과, 실제 일반 GUI의 지정 대기/주 버튼 비활성화 관측. [승격 검증](docs/reference/archive/validation/release-promotion-validation.md)

2026-10-02 후속 `cf99ba1`: 데이터 릴리스/설치 결속·명시적 root 선택·host credential 중첩·Linux 권한·로그 write/flush 보강, Windows/WSL 각552개와 게시 프로세스/HTTP·HTTPS 회귀 통과. 자동 GUI 입력은0x80070005로 거부되어 사용자 직접 조작 협업 대기다. USER-01은 실제 데이터 보존 결과 전까지50%를 유지한다. [후속 기록](docs/reference/archive/validation/runtime-data-acceptance-completion.md)

**이 파일은 남은 일만 적은 목록이 아닙니다. 완료 이력도 삭제하지 않고 함께 관리합니다.**

| 완료 | 부분 진행 | 대기 | 전체 |
|---:|---:|---:|---:|
| 6개 | 11개 | 11개 | 28개 |

완료를 제외하면 열린 항목은 **22개**입니다. 각 분야 표에서 `진행률`과 `상태`를 먼저 보면 됩니다.

### 현재 진행 상태 요약

| 구분 | 확인된 상태 | 아직 남은 조건 |
|---|---|---|
| 기본 배포 흐름 | ZIP+외부 JSON → 검사/승인 → 권한 확인 → 설치/실행·repair 구현, 합성 E2E 통과 | 로컬 Windows 실제 UE 최초 흐름 확인. 회사 UE 업데이트/복구·RHEL·규모별 인수는 별도 |
| 성능 | PERF-01/02/04 재현 범위 완료 | PERF-03의 10개 연결 지연 기준 미달 유지 |
| 인증·초기 설정 | USER-05 완료, SEC-03/04 각각 75% | Windows 서비스 계정·WSL proxy/계측 제약·회사 HTTP 위험 수용 |
| 서명·실행 안전성 | OPS-08 50%, OPS-09 75%; 서명 순서·실행 중 변경 차단·장애 시험 구현 | 실제 인증서/설치본, 회사 계정·UE, 남은 GUI 직접 시험 |
| 새 UI | 포스코DX UI·정확한 선택/복원·진단/조치 안내 구현. d665d67 Windows/WSL 각587개·게시 E2E 확인 | UI-01은1920×1080 합의 범위 완료. 최신 후보 전체 GUI·다른 해상도/DPI·음성은 별도 |
| GUI 직접 확인 | readiness의 final 게시본 양 모드/두 프로필 설치·수명·변경 차단·일반 복구·정상 복원/취소·preview 거부·실행 확인·설정/조회 재시도 통과 | final-02는 조치 문구 수정 후 양 모드403만 직접 확인. 사용자 중단 중이며 서로 다른 게시본을 합산하지 않음 |
| 운영 인수 | 현재는 제한된 테스트 활용 단계 | MSI/RPM 설치 수명주기·데이터 보존·백업 복원·운영 정책·G1~G6 |

가까운 다음 작업은 **남은 로컬 GUI 확인 → 회사 자료/권한이 필요한 검증 준비 → 데이터/운영 정책 확정**입니다. 새 기능을 한꺼번에 추가하거나 인증서·회사 계정 미준비 항목을 완료로 올리지 않습니다. 검증 문서가 archive로 이동해도 위 근거와 진행률은 바뀌지 않습니다.

### 퍼센트의 의미

아래 숫자는 **해당 추가 보완 작업의 증거 기반 단계 환산값**입니다. 실제 개발 공수·코드 커버리지·제품 완성도를 측정한 값이 아니며, 75%라고 남은 일이 전체 작업량의 25%라는 뜻은 아닙니다.

| 진행률 | 인정할 증거 |
|---:|---|
| 0% | 그 항목의 추가 보완에 대해 완료로 인정할 체크포인트가 아직 기록되지 않음 |
| 25% | 해당 보완 구현·설정·절차 마련 |
| 50% | 관련 자동화/로컬 검사를 통한 확인까지 완료 |
| 75% | 요구한 실행 환경에서 재현·측정하고 결과를 기록함. 수용 기준 미달은 별도로 유지 |
| 100% | 해당 항목에서 합의한 범위의 수용 기준까지 충족 |

단계는 순서대로 누적합니다. 필요한 증거가 빠지거나 미달 조건이 있으면 100%로 올리지 않습니다. 검증만 필요한 항목도 **그 항목의 환경·자료·검사·수용 조건**에 맞춰 적용합니다.

- **0%는 기존 기능이 없다는 뜻이 아닙니다.** 예를 들어 UI-04의0%는 기존 GUI가 없다는 뜻이 아닙니다.
- PERF-01/02/04의 100%는 **합의한 재현 환경 범위**입니다. 실제 회사 UE/RHEL 검증과 운영 승인은 OPS-01 및 [목표 문서의 G1~G6](PROJECT_GOALS.md)에 남아 있습니다.
- UI-01의100%는 사용자와 합의한1920×1080·OS 배율100%·앱 글자100%·고대비 끔 범위입니다. 다른 해상도/DPI를 시험했다고 뜻하지 않습니다.
- 항목마다 범위·위험·공수가 다르므로 퍼센트를 단순 평균해 회사용 제품의 전체 완료율로 쓰지 않습니다.

### 완료·부분 진행 항목의 체크포인트

`확인`은 증거가 있는 단계, `미달/미확인`은 아직 완료로 인정하지 않은 단계입니다.

| 항목 | ① 구현/절차 | ② 로컬 검사 | ③ 요구 환경 실행/측정 | ④ 수용 기준 | 진행률 |
|---|---|---|---|---|---:|
| PERF-01 | 병렬 다운로드·해시·상태 검사 확인 | 상한·취소·재시도 회귀 확인 | publish 실행·반복 측정 확인 | 재현 범위 충족 | 100% |
| PERF-02 | 검증 후 독립 복사 확인 | 원본 독립·손상 fallback 회귀 확인 | 실제 설치·전송량 측정 확인 | 90% 절감 재현 조건 충족 | 100% |
| PERF-04 | 작업 잠금·큐·진행·공간 점검 확인 | 복구·충돌·cleanup 회귀 확인 | worker1/2 및 API 동시 측정 확인 | 기본1 유지, 선택2의 재현 범위 충족 | 100% |
| PERF-03 | 권한 유지·조회/SQL 조정 확인 | 동시 요청·권한/서명 회귀 확인 | 1/10/30개 연결 측정 확인 | **10개 연결 지연 미달** | 75% |
| OPS-01 | 실제 UE 패키징/접수 준비 | 관련 Editor29건/도구6건·Manifest328파일 검증 | 로컬 Windows GUI 설치/실행·UE 수명/정상 종료 확인 | 회사망·Linux/RHEL·업데이트/복구 gate 미완료 | 50% |
| OPS-03 | approve/promote·revision·감사·기준선 이전·권한별 Catalog/클라이언트 선택 | Windows/WSL 각568개, 동시/저장 실패/구형/명시적 추천 회귀 | 양 OS 게시 CLI/API·HTTP 서명·HTTPS/Bearer·pending/running 불변·실제 GUI 대기 안내 확인 | 합의한 로컬 승격 범위 충족. 회사 인수는 별도 | 100% |
| USER-01 | schema3 opt-in·릴리스/설치/owner 결속·host 경로/권한/write 보강 | 후속 Windows/WSL 각552개·Root 누락/다른 릴리스/로그 접근 거부 회귀 | 게시 runtime-host 합성 v1/v2·HTTP/HTTPS 확인, 새 실제 UE fixture 사용자 조작 대기 | 실제 UE GUI 보존·앱 CustomLogs·명시적 이전·회사 계정은 미완료 | 50% |
| OPS-06 | HTTPS 및 양 OS readiness CI·정제 요약7일 보관 연결 | 동일 readiness 명령 양 OS 각8개·실패 요약/허용필드 확인 | **현재 변경의 원격 CI 증거 미확인** | 원격 CI 실행·운영 인수 필요 | 50% |
| USER-05 | schema 3 생성기·doctor·설치 예제 확인 | 관련 자동화 통과 | Windows/Linux 생성 설정 연결·실제 MSI/RPM 내용 확인 | 설정 예제 보완 범위 충족, 회사 설치 승인은 별도 | 100% |
| USER-02 | 진단 상태/대상/조치·근거 보존·모순 검증·중복 안내 수정 | Windows/WSL 각601개·실제 IPC의 구형/누락·headless 중복 회귀 | 이번 게시 readiness 양 OS 각8개, 이전 GUI는 게시본별 이력 | GUI 중단 유지. 최신 후보 전체 실제 수용은 재개 후 확인 | 75% |
| SEC-03 | 생성 시 ACL/mode/owner·명시적 repair | Windows/WSL 권한 회귀 통과 | uedt 읽기 성공·nobody 거부 확인 | Windows LocalService 실제 실행 미확인 | 75% |
| SEC-04 | 요청 서명·nonce·Catalog 결속·Agent 이미지 | 변조/replay/만료 갱신 회귀 통과 | Windows GUI/Agent·Linux HTTP/HTTPS 및 부하 실행 | WSL proxy 간헐 timeout·메모리 계측 공백 남음 | 75% |
| OPS-08 | 공식 preflight·EXE 선서명·MSI payload gate | 계약 11개·개발 MSI 추출/hash 확인 | 실제 회사 서명 인증서 미확인 | 인증서·설치본 서명 검증 대기 | 50% |
| OPS-09 | 엄격 runtime 기록·서비스 snapshot/barrier·공통 변경 조정기 | 기존 안전성 Windows/WSL 회귀 각 393개·집계 검사 | 저장 경계 강제 종료 각 36개·health 실패·정확한 서비스 대상·CLI E2E 및 게시본별 GUI 이력 | 이번 portable 실행 중 버튼 차단/수명 확인을 추가. 모든 변경 진입점의 실제 전수 검증과 회사 UE/계정은 남음 | 75% |
| UI-01 | 포스코DX·반응형·주 버튼 고정·제목 배율 | 작은 DIP/200%·포커스 회귀 | 고정8ce5060,1920×1080·OS100%·글자100%·고대비 끔, 양 모드/두 프로필 실제 관측 | 2026-09-29 사용자 합의의 제한 범위 충족. 다른 해상도/DPI 후속 | 100% |
| UI-02 | 선택/runtime·복원·재시도·진단/조치 안내 구현 확인 | d665d67 Windows/WSL 각587개·IPC/HTTP/HTTPS 확인 | readiness final의 양 모드/두 프로필 실제 설치·수명·복구·복원·확인/오류/설정 재시도 이력 | final-02 전체 GUI는 사용자 중단 중. 작은 환경/음성은 별도 | 75% |
| UI-03 | 제목 배율·Local Tab·대화창 고대비/focus·실제 framework peer | 관련 headless 72개, 연결된 peer의 이름 변경·byte tick 억제 | 사전 게시본 Windows 글자200%/Tab 관측, 사용자 prefs 원복 확인 | 최종 키보드/OS 고대비 전체 흐름 대기. 내레이터 음성 후속 보류로 75% 유지 | 75% |

근거: [성능 검증 기록](docs/reference/archive/validation/performance-validation.md), [CI 구성](.github/workflows/build.yml), 각 항목의 커밋·미완료 조건. 다른 항목의 기존 기반 기능이나 문서 작성만으로 추가 보완 진척을 자동 가산하지 않았습니다.

## 확정 목표에 따른 진행 순서

목표는 [회사에서 활용 가능한 DT 배포 시스템](PROJECT_GOALS.md)입니다. 아래는 개발·도입 우선순위이며, 새 패키지를 배포하는 명령 순서는 [기능 지도·실행 안내](docs/reference/feature-workflow.md)에서 확인합니다.

| 순서 | 우선 처리 | 다음 단계로 가는 조건 |
|---:|---|---|
| 1 | SEC-03·04 잔여 확인, USER-05 완료 유지 | HTTP 인증·credential·초기 설정 1차 작업. 남은 환경 제약은 별도 기록 |
| 2 | OPS-08·09, USER-01 | MSI payload 서명·프로세스 오인·사용자 데이터 보존 보완 |
| 3 | PERF-03, UI-01~03, USER-02 | 회사 규모 성능·화면·오류 대응 기준 통과 |
| 4 | OPS-03~07, SEC-01·02 | 승인/최신판·보관·복원·토큰·무인 운영 기준과 절차 확보 |
| 5 | OPS-01·02, 제한된 시범 운영·인수 | 실제 UE/RHEL·설치본 수명주기·G1~G6 결과와 담당자 확인 후 확대 |

## 운영·배포

| ID/우선 | 진행률 | 상태 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---:|---|---|---|
| OPS-01/P0 | 50% | 부분 · 로컬 Windows UE 확인 | 실제ma0t10_dt UE5.3 Development 328파일 패키징·ZIP/외부 JSON 서명 게시·GUI 설치/실행·수명/종료, Manifest 해시 확인. [실제 UE 기록](docs/reference/archive/validation/real-ue-package-validation.md) | 실제 UE 업데이트/복구·Shipping/Linux/RHEL·회사 TLS/CA·IP·서비스 계정 검증은 남음. 로컬 성공은 회사 승인 아님 |
| OPS-02/P0 | 0% | 대기 · 추가 보완 | MSI/RPM 구성은 있으나 이번 통합의 실기기 수명주기 미검증 | 설치/upgrade/repair/uninstall, 서비스 자동 시작·credential ACL·데이터 보존, 코드서명 gate 검증 |
| OPS-03/P1 | 100% | 완료 · 로컬 승격 범위 | `cbb5ba7`·`9350e57`·`a8080ea`: 승인/승격 분리, revision/감사/legacy-baseline, 권한별 추천·구형 목록 제한·Core/GUI fallback 차단. 양 OS568개·게시 프로세스/서명/이전·티켓/Running 불변 통과. [증거](docs/reference/archive/validation/release-promotion-validation.md) | 회사 인수·예약 공개·삭제·자동 앱 전환은 별도. 기존 DB는 offline migration 후 운영 |
| OPS-04/P1 | 0% | 대기 · 추가 보완 | StorageMaintenance는 scratch만 정리, 공개판·snapshot 누적 | 보관 기간/용량·참조 보호·dry-run·감사 정리. 사용 중 자료 보존 검증 |
| OPS-05/P1 | 0% | 대기 · 추가 보완 | 수동 백업·복원, catalog sequence 역행 위험 | 일관된 백업/복원 도구·sequence 보호·복구 훈련. 기존 PC 검증 성공, RPO/RTO 별도 합의 |
| OPS-06/P1 | 50% | 부분 · 원격 검증 대기 | HTTPS E2E + Windows/Linux readiness CI·요약1파일7일 보관 구성. 같은 명령 로컬 양 OS 각8개와 요약 허용 필드 검증. [증거](docs/reference/archive/validation/quick-wins-validation.md) | 실제 원격 CI 실행·운영 인수 결과 확인. 공유 runner 시간을 성능 gate로 사용하지 않음 |
| OPS-07/P1 | 0% | 대기 · 추가 보완 | AgentWorker는 IPC 대기만 하고 service-run은 once=true. CLI는 managed 반복 실행을 거부 | Agent 스케줄러 또는 명시적 외부 스케줄 운영을 확정. 재부팅 후 정기 점검·중복 작업 방지·maintenance window 시험 |
| OPS-08/P0 | 50% | 부분 · 실제 인증서 대기 | 공식 사전 gate·EXE 선서명·MSI 내장 payload 검증·실행별 WiX intermediate 구현. 계약 11개 및 개발 MSI 추출/hash 비교 통과 | 실제 회사 인증서와 설치된 EXE 서명 검증은 미완료. [기록](docs/reference/archive/validation/deployment-safety-validation.md) |
| OPS-09/P0 | 75% | 부분 · GUI/현장 검증 대기 | 누락 기록 fail-closed, 서비스 snapshot/barrier·이전/대상 잠금, 위험 migration apply 차단. 기존 각 393개·저장 경계 종료·health/대상 분리·CLI E2E 통과. 이번 portable 자식 수명·실행 중 변경 버튼 차단 추가 | 이전 관리형 복구/rollback과 이번 portable 증거는 게시본별 이력. 새 후보 양 모드 전체 GUI 수용, 모든 진입점/상태 조합의 실제 전수 검증과 회사 UE/계정·원격 CI는 미완료. [기존 기록](docs/reference/archive/validation/managed-gui-safety-validation.md), [이번 기록](docs/reference/archive/validation/ui-acceptance-finalization.md) |

## 성능·저장 공간

| ID/우선 | 진행률 | 상태 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---:|---|---|---|
| PERF-01/P1 | 100% | 완료 · 재현 범위 | 재현 환경 완료: bounded 다운로드/해시, 관리형 상태 검사, 실제 네트워크 속도·논리 진행률 구분. 작은 파일 설치 10.96초→5.98초 | `94a4f9e`, `1e29454`; 실제 UE·회사 PC 기준선은 OPS-01에서 계속 확인 |
| PERF-02/P1 | 100% | 완료 · 재현 범위 | 재현 환경 완료: 인증된 대상 기준 최근 3개 설치에서 독립 복사. 90% 동일 데이터의 전송량 90% 절감 | `417b9ec`, `638028a`; 원본 독립성·손상 fallback·repair 재다운로드 검증. 공용 캐시는 범위 밖 |
| PERF-03/P1 | 75% | 부분 · 기준 미달 | 부분 완료: 토큰 매 요청 검사, 정책 파싱 재사용, exact 조회, fresh sequence 유지. 30개 연결 p95 833.44→81.23ms·실패32→0 | `10ec1f9`, `d152477`; **10개 연결 혼합 p95 5.43→24.66ms 악화**, 시간 gate 미달. 기본 운영 적용 승인 보류, 혼합 읽기/쓰기 지연 추가 개선 필요 |
| PERF-04/P2 | 100% | 완료 · 재현 범위 | 재현 환경 완료: 진행/공간 점검, schema 백업, OS 작업 잠금·cleanup 보호·공정 큐. 동시 HTTP 중 4개 ZIP 접수 중앙값 1 worker 5.02초 / 2 workers 2.59초 | `d3f8d68`, `f12a861`, `d152477`; 2 worker 편차·응답 지연도 보고. 기본1 유지, 실환경 자원 기준선 별도 |

상세 조건·반복 횟수·부분 미달 항목은 [성능 검증 기록](docs/reference/archive/validation/performance-validation.md)을 따릅니다. WSL1 nginx의 큰 파일 중단은 직접 API 정상/프록시 경유 실패로 분리 관측했고, 실제 RHEL nginx 대용량 검증은 미완료입니다. 이 제한을 작은 파일 E2E 통과로 대체하지 않습니다.

## 사용자 관점

| ID/우선 | 진행률 | 상태 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---:|---|---|---|
| USER-01/P1 | 50% | 부분 · GUI 협업 대기 | `cf99ba1`: 릴리스/설치 결속·명시적 root·user host credential 보호·Linux 권한·UserDir/log write 보강. Windows/WSL 각552개·게시 합성 host 통과. [후속 증거](docs/reference/archive/validation/runtime-data-acceptance-completion.md) | 실제 UE GUI 설치/업데이트/복원·SaveGame 보존 사용자 직접 조작 대기. CustomLogs·데이터 이전·회사 계정 미완료. 실제 수용 전75%로 올리지 않음 |
| USER-02/P1 | 75% | 부분 · 로컬 진단/GUI 확인 | `8e42536`·`cc7ff02`·`5e3a247`: 읽기 전용 진단·mode/target/capability·준비도·조치 안내·지원 ZIP. Windows/WSL 각587개, 게시 HTTP/HTTPS·구형 Agent·양 모드 실제 오류/재시도·설정 수정 확인. [증거](docs/reference/archive/validation/readiness-validation.md) | 마지막 조치 문구 보강의 영향받은 오류 화면을 새 후보로 확인. 최신 후보 전체 GUI 수용·실제 회사 새 PC는 별도 |
| USER-03/P2 | 0% | 대기 · 추가 보완 | 엔진 취소 토큰과 별개로 일반 UX 취소는 이전 범위에서 제외 | 안전 중단 지점·취소/재개 설계. 다운로드·검증·설치별 중단 후 손상 없음 |
| USER-04/P2 | 0% | 대기 · 추가 보완 | 오프라인·권한 폐기 후 기설치 실행 최종 정책 미정 | 실행/권한 재확인 규칙 합의. 미설치·기설치·폐기 토큰 수용 테스트 |
| USER-05/P1 | 100% | 완료 · 설정 보완 범위 | schema 3 생성기·doctor·MSI 예제/RPM 설정, Windows/Linux publish 생성 설정 E2E와 실제 artifact 내용 확인 | `24cc451` 및 최종 검증. MSI/RPM 실제 설치 수명주기는 OPS-02로 유지. [기록](docs/reference/archive/validation/intranet-auth-validation.md) |

## UI·접근성

| ID/우선 | 진행률 | 상태 | 현재 상태·영향 | 보완 방향·완료 조건 |
|---|---:|---|---|---|
| UI-01/P1 | 100% | 완료 · 1920×1080 범위 한정 | 고정8ce5060 양 모드/두 프로필 실제 GUI 및 화면 진단 확인:1920×1080·OS100%·글자100%·고대비 끔. 2026-09-29 사용자 합의로 수용 범위 조정 | **추가 테스트 필요:**1366×768·1280×720 등 다른 해상도, OS125/150%·큰 글자/고대비 최악 조건. 미검증을 지원 보장으로 표현하지 않음. [증거·범위 변경](docs/reference/archive/validation/ui-acceptance-finalization.md) |
| UI-02/P1 | 75% | 부분 · 최신 후보 GUI 중단 | d665d67 각587개 및 readiness final 양 모드/두 프로필 실제 전체 기능 이력. final-02 조치 문구 수정 후 양 모드403 확인. [게시본별 증거](docs/reference/archive/validation/readiness-validation.md) | 최신 후보 전체 수용은 [단일 체크리스트](docs/reference/guide-03-launcher-usage.md#보류-중인-gui-수용-체크리스트)를 따라 재개 후 확인. 이력 합산 금지, 작은 환경은 후속 |
| UI-03/P2 | 75% | 부분 · 음성 보류/OS 대기 | 제목 배율·Tab/focus·대화창 고대비, 연결된 framework peer 이름 변경·byte tick 억제 회귀. 사전 게시본 실제 글자200%/Tab·prefs 원복 확인 | 새 후보 키보드 전체 흐름·OS 고대비 확인. 내레이터 실행/녹음/청취는 후속 보류하며 코드/UIA 통과로 대체하지 않음 |
| UI-04/P2 | 0% | 대기 · 추가 보완 | 트레이·완료 알림은 이전 범위에서 제외 | 필요성 합의 후 opt-in 구현. 닫기/종료 의미·알림 설정 명확화 |

## 보안·확장·유지보수

| ID/우선 | 진행률 | 상태 | 현재 근거·영향 | 보완 방향·완료 조건 |
|---|---:|---|---|---|
| SEC-01/P1 | 0% | 대기 · 추가 보완 | DistributionTokens 발급·PC 단위 전체 폐기, 만료/개별 관리 없음 | 만료·토큰별 폐기·순환·감사 설계. 비밀 노출 없는 교체/만료/폐기 시험 |
| SEC-02/P1 | 0% | 대기 · 추가 보완 | loopback nginx 신뢰와 직접 사내 IP 전제 | NAT/추가 proxy의 실제 IP 계약·rate limit 검토. 위조 헤더 차단과 회사 망 경로별 검증 |
| SEC-03/P0 | 75% | 부분 · Windows 서비스 실행 대기 | `e22a908` 이후 생성 시 ACL/mode/owner 검증. Linux uedt 실제 읽기·nobody 거부 통과 | Windows LocalService 실제 설치 계정 읽기와 기존 credential 이전 현장 확인 남음. [기록](docs/reference/archive/validation/intranet-auth-validation.md) |
| SEC-04/P0 | 75% | 부분 · 환경 제약 남음 | `babb88e`, `10d4063`: 요청 서명·challenge/nonce·Catalog 결속·Agent 이미지, Windows/Linux E2E·1/10/30 연결 실패 0 | WSL proxy의 간헐 handshake/startup timeout과 메모리 지표 미수집을 보존. 실제 RHEL 대용량·회사 HTTP 위험 수용 별도. [기록](docs/reference/archive/validation/intranet-auth-validation.md) |
| EXT-01/P2 | 0% | 대기 · 추가 보완 | IAccessPolicyProvider 파일 구현만 존재 | 회사 API 합의 후 timeout/cache TTL/기본 거부. 장애·취소·오래된 응답에서 권한 확대 없음 |
| DEV-01/P2 | 0% | 대기 · 추가 보완 | Core 링크 컴파일, MainWindow 동작 코드 잔존 | 기능 변경과 분리한 물리 폴더·ViewModel 정리. API/CLI/IPC 회귀 없음 |

근거 소스: [서버 API](src/UeDtLauncher.DistributionServer/DistributionHttp.cs), [저장 관리](src/UeDtLauncher.DistributionServer/StorageMaintenance.cs), [권한](src/UeDtLauncher.DistributionServer/AccessPolicy.cs), [엔진](src/UeDtLauncher/LauncherEngine.cs), [버전 경로](src/UeDtLauncher.Core/Distribution/VersionedReleasePaths.cs), [CI](.github/workflows/build.yml).

추가 교차 확인: [AgentWorker](src/UeDtLauncher.Agent/Program.cs), [Agent 작업](src/UeDtLauncher.Agent/AgentIpcHostedService.cs), [서명 순서](.github/workflows/release.yml), [MSI CAB](installer/windows/Product.wxs), [RPM 설정](scripts/build-rpm.sh), [credential 저장](src/UeDtLauncher/CommercialSecurity.cs), [프로세스 식별](src/UeDtLauncher/ServiceRunner.cs). 위 링크는 점검 출처입니다. 이후 실제 수정과 검증은 각 항목의 최신 기록을 따르며 과거 문서 전용 점검과 구분합니다.

## 처리 제안과 상태 관리

1. OPS-08/09·SEC-03: 설치 payload 서명·프로세스 오인·credential 접근 문제 우선 보완. 이어 OPS-01/02·USER-01의 회사 환경·실제 패키지·데이터 보존 검증.
2. OPS-03/04/05·SEC-01: 최신판·보관·복원·토큰 운영 사고 예방.
3. PERF 기준선·OPS-06: 측정 후 개선, 자동 회귀 검증.
4. USER/UI·API 범위는 [목표 문서](PROJECT_GOALS.md)에서 확정 후 진행.

PERF-01/02/04·USER-05·UI-01·OPS-03은 100%(UI-01은1920×1080 한정, OPS-03은로컬 승격 범위), PERF-03·SEC-03·SEC-04·OPS-09·UI-02·UI-03·USER-02는 75%, OPS-01·OPS-06·OPS-08·USER-01은 50%(각 남은 조건 참조)입니다. 나머지 11개는 0%이며 기존 기능 부재를 의미하지 않습니다.

진행률을 변경할 때는 상태·체크포인트·커밋/검증 링크·남은 조건·상단 집계를 함께 갱신합니다. 100% 항목은 완료 이력으로 남기고, 새 미달 조건이나 범위 변경이 확인되면 이유를 기록해 다시 열 수 있습니다. 빌드 성공만으로 완료하지 않습니다.
