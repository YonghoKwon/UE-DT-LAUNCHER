# 사내 HTTP 요청 인증 구현·검증 기록

기준: `edcb8de`, 작업 브랜치 `codex/intranet-request-auth`, 2026-09-28.

HTTP는 암호화된 전송이 아니다. 요청 인증·콘텐츠 무결성과 기밀성·실시간 중계 방어는 구분한다. 회사 운영 승인은 별도이다.

## 1단계: 보호된 PC 키 저장

- schema 3의 명시적 request-signature-v1 계약과 별도 `.keycred` 저장 형식 추가. 기존 `.cred`는 Bearer 전용.
- Linux: 파일 생성부터 0600, 관리형은 uedt 소유, 부모 root:uedt 0750. 파일 descriptor 기준 owner/mode/type/link 검사와 원자 교체.
- Windows: 생성 시 제한된 ACL + DPAPI LocalMachine, 읽기 시 owner/ACL 검사.
- 키 생성·공개 등록 파일 출력·민감정보 없는 status·Linux 명시적 permission repair 제공.
- Windows/WSL 관련 자동화 각각 15개 통과. Windows Release 빌드 경고/오류 0.
- Windows publish CLI의 키 생성과 공개 JSON/보호 파일 생성 확인. 이후 단계에서 최종 publish를 재검증한다.
- 아직 요청 인증 전송 구현 전이며 schema 3 연결은 명시적으로 거부한다. 이 단계는 전체 HTTP 인증 완료가 아니다.
- 서비스 계정/무관 계정 실제 읽기 분리, 최종 E2E·성능·회사 RHEL/UE 검증은 아직 미완료.
