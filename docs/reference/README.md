# 참고 문서 모음

정리: 2026-09-22. 상시 관리 정본은 루트의 [README](../../README.md), [AGENTS](../../AGENTS.md), [보완 목록](../../IMPROVEMENTS.md), [목표](../../PROJECT_GOALS.md) 4개입니다. 이 폴더는 상세 명령·사용법·과거 증거를 모아 관리합니다.

## 현재 사용 절차

- [사내 HTTP 인증 단계별 검증](intranet-auth-validation.md): 현재 진행 작업과 미완료 조건.

| 문서 | 용도 |
|---|---|
| [기능 지도·단계별 실행 안내](feature-workflow.md) | 처음 읽는 사람용 구성도·순서도·담당자·명령어·정상 결과 |
| [통합 배포 운영](distribution-workflow.md) | 서버·서명·IP/토큰·권한·클라이언트·보관/복원 설정의 상세 기준 |
| [서버 준비](guide-01-linux-server-setup.md) | nginx/서비스 신뢰 경계와 구성 입구 |
| [ZIP 업로드·승인](guide-02-publish-package.md) | 외부 JSON 생성부터 승인 공개까지 |
| [클라이언트 설정·CLI](guide-03-launcher-usage.md) | GUI/Agent 설정 분리와 명령 |
| [화면 사용](launcher-user-guide.md) | 일반·개발자 버튼·상태·오류 |
| [UI 커스터마이징](launcher-ui-customization.md) | 프로필·이미지·브랜드 fallback |
| [무인 서비스](service-mode.md) | Agent와 앱 감시 서비스의 차이·운영 주의 |
| [설치본·상용 배포](commercial-deployment.md) | MSI/RPM·자격 증명·운영 gate |
| [2026-09-12 검증 기록](distribution-validation.md) | 당시 테스트 환경·결과·회사 미검증 범위 |
| [성능 설정·2026-09-28 검증](performance-validation.md) | 병렬 처리·파일 재사용·접수 worker·실측 결과와 미달 항목 |

처음 읽을 때는 기능 지도·실행 안내 → 통합 운영 → ZIP 업로드 → 클라이언트 순서입니다. 실제 회사 RHEL/UE 검증과 테스트용 Windows/WSL 실행은 구분합니다. 과거 검증 날짜를 문서 점검 날짜로 덮어쓰지 않습니다.

## 과거 참고 자료

아래는 삭제하지 않고 보존한 역사 자료입니다. HTTP/public static 경로, Basic Auth, 직접 압축 해제·게시, 과거 UI 설명은 **신규 운영 구성에 적용하지 않습니다**. 현재 대응 절차는 위 문서를 따릅니다.

| 보존 자료 | 현재 대신 볼 문서 |
|---|---|
| [RHEL 서버 이전 가이드](archive/redhat-distribution-server.md) | 통합 배포 운영·서버 준비 |
| [오프라인 Linux 이전 구성](archive/offline-linux-update-server-setup.md) | 통합 배포 운영·설치본 배포 |
| [회사 RHEL 8.4 이전 기록](archive/company-rhel84-dt-update-server.md) | 서버 준비; 현재 회사 적용 완료의 증거가 아님 |
| [구 게시 스크립트](archive/release-publish-scripts.md) | ZIP 업로드·승인; publish-wizard는 현재 ingest 래퍼 |
| [구 일반/개발자 사용법](archive/developer-and-general-launcher-usage.md) | 클라이언트·화면 사용 |
| [초기 개선 노트](archive/launcher-refinement-notes.md) | 루트 보완 목록·목표 |
| [Netmarble 초기 분석](archive/netmarble-launcher-analysis.md) | 설계 참고만; 현행 구현 명세가 아님 |

## 갱신 규칙

- 현재 가이드: 명령·설정 변경 시 소스와 함께 갱신하고 점검 기준을 적습니다.
- 실행 기록: 실제 재시험할 때 날짜·환경·결과를 별도로 추가합니다.
- archive: 과거 내용은 보존하고 폐기 상태·현재 대체 문서만 갱신합니다. 보안상 구 절차를 현재 권장으로 복원하지 않습니다.
- 최종 기능·계획·우선순위를 이 색인에 중복 작성하지 않습니다.

## 2026-09-22 문서 점검 기록

- Markdown 21개: 루트 관리 문서 4개 + 참고/보존 문서 17개.
- 상대 로컬 링크 108개 존재 확인, 현재 가이드 JSON 예시 6개 파싱 성공.
- 루트 문서 4개 계약 및 docs 루트에 흩어진 Markdown 없음 확인.
- 소스·CLI·서비스·설치 스크립트와 대조, git diff --check 통과.
- 문서만 수정했으므로 전체 .NET 테스트·GUI/서버 실행·성능 측정은 재수행하지 않음. 과거 실제 실행 결과는 기존 날짜로 보존.
- 확인된 기능 보완은 루트 보완 문서에 미해결 상태로 기록. 문서 갱신이 해당 코드 문제 수정 완료를 의미하지 않음.
