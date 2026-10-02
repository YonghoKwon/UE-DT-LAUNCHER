# 아카이브 색인

2026-09-28 정리. 삭제 대신 보존합니다. 현재 절차는 [운영 문서 색인](../README.md), 진행률은 [개선 대장](../../../IMPROVEMENTS.md)을 확인하세요.

## 검증 이력 — 증거는 보존, 현재 절차와 구분

| 기록 | 내용 |
|---|---|
| [빠른 비대화형 개선](validation/quick-wins-validation.md) · [정제 JSON](validation/quick-wins-evidence.json) | 양 OS601개·실제 IPC 근거 누락/구형·진단 합성·준비도 CI·양 OS 각8개·원격/GUI 보류 |
| [최초 연결 진단·오류 해결](validation/readiness-validation.md) · [정제 JSON](validation/readiness-evidence.json) | 양 OS587개·읽기 전용 doctor·선택/승격/구형 호환·게시 E2E·실제 양 모드 설치/복구/복원·게시본별 수용 구분·사용자 GUI 검증 중단 |
| [승인·승격 분리](validation/release-promotion-validation.md) · [정제 JSON](validation/release-promotion-evidence.json) | a8080ea·양 OS568개·권한별 추천·DB 이전·pending/running 불변·실제 지정 대기 GUI |
| [데이터 수용 후속](validation/runtime-data-acceptance-completion.md) | cf99ba1 보강·양 OS552개·scope별 비교·사용자 직접 GUI 조작 대기 |
| [UE 데이터 경로·수용 준비](validation/real-ue-data-safety-validation.md) · [정제 JSON](validation/real-ue-data-safety-evidence.json) | opt-in 경로 보호·Windows/WSL542개·게시 합성 host·HTTP/HTTPS, 실제 UE GUI 입력 제약과 재개 순서 |
| [실제 UE 패키지](validation/real-ue-package-validation.md) | ma0t10_dt Windows 패키징·실제 GUI 설치/실행·수명, 보호 경로와 회사 미검증 범위 |
| [배포 통합](validation/distribution-validation.md) | 초기 ZIP/JSON·권한·Windows/WSL 통합 시험 |
| [성능](validation/performance-validation.md) | PERF-01~04 측정 방법·결과·지연 미달 |
| [인증](validation/intranet-auth-validation.md) | 요청 서명·credential·HTTP/HTTPS·환경 제약 |
| [인증 부하 JSON](validation/intranet-auth-load-results.json) | 위 인증 시험의 구조화 측정 결과 |
| [배포 서명·runtime 1차](validation/deployment-safety-validation.md) | MSI 순서·native 후손·Agent·변경 차단 |
| [runtime 후속](validation/runtime-safety-completion-validation.md) | 상태 저장·서비스 전환·장애 주입 |
| [UI 최종 수용 진행](validation/ui-acceptance-finalization.md) · [정제 JSON](validation/ui-acceptance-finalization-evidence.json) | ab37 게시본 portable 설치/수명/복구·정상 백업, 이후 상태 결함 보완과 최종 재검증 대기 |
| [새 GUI 실제 수용 후속](validation/gui-validation-completion.md) | 이전 설치 표시·정확한 복원·실제 일반 업데이트/개발자 복원·조회 재시도·남은 OS 조합 |
| [포스코DX UI·접근성](validation/poscodx-ui-accessibility-validation.md) | 새 일반/개발자 화면·작업 피드백·설정/접근성·실제/미검증 구분 |
| [관리형 GUI](validation/managed-gui-safety-validation.md) | 일반 설치/실행·창 종료, 개발자 선택·취소·복구·rollback 실제 기록 |

아카이브로 이동한 것은 시험 결과 폐기나 실패 전환이 아닙니다. 최신 상태를 확인하지 않고 과거의 “미완료/다음 단계” 문장을 현재 상태로 인용하지 마세요. 현행 성능 옵션은 [설정 레퍼런스](../guide-03-launcher-usage.md)를 따릅니다.

## 현재 문서에 통합한 입문 가이드

| 보관 문서 | 현재 대체 문서 |
|---|---|
| [서버 준비 입문](guides/guide-01-linux-server-setup.md) | [서버 운영](../distribution-workflow.md), [요청 서명](../intranet-auth.md) |
| [ZIP 게시 입문](guides/guide-02-publish-package.md) | [기능 지도 B: 새 버전 배포](../feature-workflow.md) |

## 초기/구 운영 자료

아래의 공개 static 경로·Basic Auth·직접 압축 해제 게시 절차는 신규 운영 구성에 적용하지 않습니다.

| 자료 | 현재 대신 확인 |
|---|---|
| [RHEL 이전 구성](redhat-distribution-server.md) | [서버 운영](../distribution-workflow.md) |
| [오프라인 Linux 이전 구성](offline-linux-update-server-setup.md) | [서버 운영](../distribution-workflow.md) |
| [회사 RHEL 8.4 이전 기록](company-rhel84-dt-update-server.md) | [설치본·운영 인수](../commercial-deployment.md); 회사 검증 완료의 근거 아님 |
| [구 게시 스크립트](release-publish-scripts.md) | [현행 배포 명령](../feature-workflow.md) |
| [구 일반/개발자 사용법](developer-and-general-launcher-usage.md) | [GUI 안내](../launcher-user-guide.md) |
| [초기 개선 노트](launcher-refinement-notes.md) | [개선 대장](../../../IMPROVEMENTS.md) |
| [초기 외부 런처 분석](netmarble-launcher-analysis.md) | 설계 참고 전용 |

원문은 역사 자료로 보존하고 링크와 보관 안내만 관리합니다. 완료 여부를 변경하려면 새로운 실행 증거와 정본 업데이트가 필요합니다.
