# 현재 운영 문서 색인

정리: 2026-10-03 / 작업 기준 `codex/operations-hardening-closure`. 상시 관리 정본은 루트 4개이며, 이 폴더에는 **현재 사용하는 상세 가이드 9개와 이 색인**만 둡니다.

| 루트 정본 | 역할 |
|---|---|
| [README](../../README.md) | 구현된 기능·현재 상태·시작 방법 |
| [AGENTS](../../AGENTS.md) | 개발·검증·문서 관리 규칙 |
| [IMPROVEMENTS](../../IMPROVEMENTS.md) | 28개 보완 항목의 진행률·증거·남은 조건 |
| [PROJECT_GOALS](../../PROJECT_GOALS.md) | 회사용 DT 배포 시스템 목표·운영 인수 조건 |

## 무엇을 읽으면 되나요?

| 필요한 작업 | 현재 문서 |
|---|---|
| 처음부터 업로드·승인·설치·실행 순서 확인 | [기능 지도·실행 명령](feature-workflow.md) |
| Linux 서버·IP/배포 권한·접수/승인·보관 운영 | [배포 서버 운영](distribution-workflow.md) |
| 사내 HTTP 요청 서명·PC 공개키 등록·초기 설정 | [인증·초기 준비](intranet-auth.md) |
| GUI/Agent 설정 역할·CLI·성능 옵션 | [설정·CLI 레퍼런스](guide-03-launcher-usage.md) |
| 일반/개발자 버튼·복구·rollback 사용 | [GUI 사용자 안내](launcher-user-guide.md) |
| 이름·이미지·표시 설정 | [UI 커스터마이징](launcher-ui-customization.md) |
| 실행 중 변경 차단·Unknown·수동 정지 확인·이전 제한 | [실행 안전성](runtime-safety.md) |
| 무인 service 한 회차·health 실패·수동 전환 | [서비스 모드](service-mode.md) |
| MSI/RPM 제작·서명 gate·설치 전 체크 | [설치본·상용 배포](commercial-deployment.md) |

처음에는 **기능 지도 → 인증·초기 준비 → 서버 운영 → 설정/GUI 안내** 순서로 읽으세요. HTTPS/Bearer 예제의 URL만 HTTP로 바꾸지 않습니다. HTTP는 요청 서명을 사용해도 암호화되지 않습니다.

## 검증 증거와 과거 자료

최신 추가: [운영 안전성·게시 프로그램 검증](archive/validation/operations-closure-validation.md). 취소/캐시/정리/복원/인증/점검·설치본과 실제 GUI/회사 미검증을 구분합니다. 이전 [UE 데이터 경로](archive/validation/real-ue-data-safety-validation.md)는 당시 후보의 이력입니다.

[아카이브 색인](archive/README.md)에서 찾습니다. **보관된 검증 결과는 유효한 이력**이지만 해당 날짜·환경·범위를 넘는 보장은 아닙니다.

- `archive/validation/`: 배포·성능·인증·runtime·GUI 실행 기록과 관련 측정 JSON.
- `archive/guides/`: 현재 가이드에 통합한 중복 입문 문서.
- `archive/`의 기존 자료: 초기 설계·구 정적 서버·이전 게시 방식.

현재 기능/진행률은 루트 정본, 현재 명령은 위 가이드, 실제 과거 결과는 검증 이력에서 확인합니다. 이 세 가지를 섞어 완료 여부를 판단하지 않습니다.

## 관리 규칙

- 기능과 명령의 현재 설명을 검증 기록에만 두지 않습니다. 가이드에 반영하고 이력은 보존합니다.
- 과거 실행 날짜·수치를 새 문서 점검 날짜나 재시험 결과로 바꾸지 않습니다.
- 이동 시 Markdown 상대 링크와 코드/스크립트의 문서 경로 참조를 함께 확인합니다.
- `python tools/check-documentation.py`와 `python tools/check-improvement-ledger.py`로 구조·링크·집계를 검사합니다.
- 이번 UI 변경은 `ab37fcb`의 각500개·portable 실제 적용과 추가 상태 보완 소스의 각510개·미설치 재연결 사전 시험을 확인했습니다. 새 최종 후보 전체 수용은 대기입니다. [최신 증거·미완료 조건](archive/validation/ui-acceptance-finalization.md)을 따르며 과거 게시본 성공을 합산하지 않습니다.
