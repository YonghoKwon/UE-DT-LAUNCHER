# 포스코DX UI·접근성 구현 검증

2026-09-28 / 기준 1156a36 / codex/poscodx-ui-accessibility. 단계별 증거이며 실제 OS 배율/내레이터/회사 운영 승인과 구분한다.

## 1. 디자인 기반

- 공식 RGB PNG 원본과 출처/SHA-256 기록, POSCO BLUE #05507D 및 LIGHT BLUE #00A5E5 토큰.
- 사용자별 글자 크기/고대비 preferences와 작업 표시/500줄 로그 모델 추가. 최초 JSON casing 회귀를 수정한 뒤 관련 9개 테스트 통과.
- Windows Release single-file publish와 --version 성공, 실제 설정 없음 GUI 실행 확인. 새 레이아웃·로고 표시는 다음 단계이며 이 단계 화면을 최종 디자인으로 간주하지 않는다.

## 2. 새 작업 공간

- 포스코DX logo header, 일반 밝은 요약 화면/개발자 어두운 작업 공간, 1100/800 DIP 반응형, 하단 작업/진행 영역, 키보드 ListBox 프로젝트 선택 추가.
- 서비스 배지는 전체 재구성하지 않고 갱신하며 진행·로그는 표시 모델에 보존한다. fallback 이니셜의 좁은 이미지 잘림을 실제 화면에서 확인해 수정했다.
- 테스트 전용 내부 생성자로 서비스/타이머를 끈 Headless 시험 추가. 18개 물리 해상도/배율에 대응하는 DIP viewport 등 관련 29개 통과. 이는 실제 OS 배율 시험이 아니다.
- 실제 publish 일반/개발자 화면에서 로고·레이아웃 관측. 개발자 초기 선택/텍스트 이벤트가 갱신을 반복하는 문제를 발견해 동일값 guard와 회귀 추가. 수정 후 UI 응답/접근성 트리 확인.
- RuntimeBlocked에서는 새 화면의 변경 명령이 비활성화된다. 관리형 cache/backup 정리는 Agent에 지원 명령이 없어 권한을 우회하지 않고 사유와 함께 비활성화한다. 별도 운영 기능 구현은 이번 범위 밖.

## 3. 작업 의도·오류·rollback preview

- 조회 재시도가 설치/실행이 되지 않도록 작업 종류·선택 snapshot을 보존한다. 오래된 오류창/선택 변경은 조회로 돌아가며 rollback은 매번 새 preview/확인을 요구한다.
- 오류 유형/HTTP 상태 기반 코드와 correlation 지원 ID 보존, 일반 진행 문구 allowlist, 완료 제목·로그 유지.
- optional IPC v1 rollback-preview, backup ID + metadata fingerprint 확인 후 기존 설치 lease 안에서 복원. legacy CLI 동작은 유지한다.
- 전체 Windows 자동화 422개 통과, 후속 관련 12개 통과. Windows publish GUI 서비스 단절 표시 확인. 실제 console Agent에서 preview 조회·변조 fingerprint 거부·정상 fingerprint 복원과 기존 runtime broker 회귀 통과(`uedt-broker-proof-cqqdgc6y`).
- GUI 실제 설치/복원 버튼 조작과 실제 내레이터/OS 배율은 최종 단계에서 별도 확인한다. 자동화만으로 전체 접근성 완료를 선언하지 않는다.

## 4. 키보드·글자 크기·고대비

- 공통 스크롤 대화창, 취소 기본 포커스/Escape, 호출 버튼 포커스 복원, semantic TabIndex와 프로젝트 방향키 선택 회귀를 추가했다. 200%/고대비를 포함한 관련 33개 자동화 통과.
- 실제 publish 개발자/일반 GUI의 설정과 UIA 이름·닫기 기본 포커스를 확인했다. 설정 적용의 UI-thread 동기 대기 정지를 실제 클릭으로 발견했고 SaveAsync/await로 수정했다. 수정 후 일반 GUI에서 200%+고대비 적용, 사용자 파일 저장, 설정 버튼 포커스 복원을 직접 확인했다.
- 전체 Windows/WSL 각 426개 통과(설정 비동기 수정 직전), 수정 후 관련 33개 통과. WSL 최초 25개 화면 테스트는 fontconfig 부재로 실패했으며 apt 패키지를 /tmp/uedt-ui-deps에 비설치 추출하고 LD_LIBRARY_PATH를 지정한 재실행에서 426개 통과했다. 실패를 생략하거나 화면 시험을 제외하지 않았다.
- Windows/Linux publish CLI/console Agent에서 rollback preview·fingerprint 불일치 거부·실제 복원과 runtime broker 회귀 통과. Windows fixture `uedt-broker-proof-ngw_9hjo`, WSL `uedt-broker-proof-6u_60g8j`.
- UIA 속성과 headless focus 결과는 실제 내레이터 음성 확인이나 실제 OS 18개 DPI 조합의 대체 증거가 아니다.

## 5. 최종 회귀·남은 실제 수용

| 확인 | 결과 |
|---|---|
| Windows/WSL 전체 회귀 | 각각 428개 통과, skip 0. 최종 화면 회귀 26개 포함 |
| 빌드/publish | Windows Release solution 경고 0·오류 0, Windows GUI/Agent 및 Linux CLI/Agent publish·버전 실행 |
| Windows HTTP 요청 서명·Agent E2E | PASS, `uedt-intranet-33wcxyja` (CLI 시험이며 GUI 설치 대체 증거 아님) |
| Linux HTTPS/Bearer+nginx E2E | PASS, `/tmp/uedt-distribution-e2e.LlEDJQ`: sidecar 승인·IP/header 거부·Range·token 폐기·두 버전/권한/repair. nginx 기본 로그 경로 alert는 있었지만 임시 설정 시험/전체 E2E는 성공 |
| 새 GUI 합성 환경 | `publish/posco/gui`: 수동 승인된 두 버전·console Agent·HTTP 서버, 일반 화면 자동 연결/목록/미설치/설치 후 실행 버튼 확인. 회사 서버 미사용 |
| 설정 직접 조작 | 일반/개발자 설정, 기본 닫기 포커스, 일반 200%+앱 고대비 저장·적용·다시 열기, Escape와 호출 버튼 focus 복원 |
| 상태 보존 | resize 후 유지보수 펼침/상세 탭 유지 회귀, compact 선택 글자 배율, 점검 완료 단계 표시, 일반 runtime 내부 메시지 차단 |

실제 실행에서 설정 동기 저장의 멈춤과 초기 선택 이벤트 재진입을 찾아 수정했다. runtime 안내를 안전한 정적 문구로 전환하면서 기존 문구 계약 테스트 1개가 실패했고 기존 Running 문구는 보존하여 전체 재통과했다.

### 실행 명령 (저장소 루트)

| 용도 | 명령 |
|---|---|
| 전체 자동화 | `dotnet test src/UeDtLauncher.Tests -c Release` |
| 화면만 재검증 | `dotnet test src/UeDtLauncher.Tests -c Release --filter FullyQualifiedName~EnterpriseLayoutTests` |
| Windows GUI 게시본 | `publish/posco/final/UeDtLauncher.exe --gui --config <격리된 GUI 설정>` |
| 문서 구조/집계 | `python tools/check-documentation.py` / `python tools/check-improvement-ledger.py` |

Linux headless CI는 fontconfig/freetype/fonts 패키지를 명시적으로 준비한다. 로컬 WSL은 시스템 설치 대신 `/tmp/uedt-ui-deps/root/usr/lib/x86_64-linux-gnu`를 LD_LIBRARY_PATH로 제공했다. 테스트 root·원시 로그·키·실행 파일은 Git에서 제외한다.

### 미완료 — 성공으로 표시하지 않음

- 새 GUI의 설치/실행 적용은 Computer Use 실행 직전 확인 카드 승인 대기. 실제 GUI의 복구·rollback 적용/취소·서비스 재연결 전수는 미완료. 기존 GUI 적용 이력과 이번 Agent CLI 복원은 별도 증거이다.
- 1280×720/1366×768/1920×1080 × OS 100/125/150% × 두 프로필의 실제 전체 캡처와 내레이터 음성·OS 고대비 변경은 미완료. 대응 DIP 시험만 통과했다.
- 회사 CI 최종 승인, 실제 UE·회사 서비스 계정/RHEL·원격 CI는 미검증. UI-01/02/03은 75%, 다른 운영/보안/성능 항목은 기존 진행률 유지. UI-04 트레이/알림 제외.
- 작은 합성 HTTPS 시험 성공으로 기존 WSL 대용량 proxy 제한이나 PERF-03 성능 미달을 종료하지 않는다.
