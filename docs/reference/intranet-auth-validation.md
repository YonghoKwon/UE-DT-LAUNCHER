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
- 1단계 시점에는 전송을 명시적으로 차단했다. 이후 2단계에서 전송을 연결했다.
- 서비스 계정/무관 계정 실제 읽기 분리, 최종 E2E·성능·회사 RHEL/UE 검증은 아직 미완료.

## 2단계: 서명된 요청과 Catalog 응답

- PC 공개키 등록/조회/폐기, DB v3 및 migration 전 SQLite 백업 추가. key ID는 재등록·재배정하지 않는다.
- 제한된 RFC 9421 GET/HEAD 프로필, 60초 monotonic challenge, 프로세스 재시작 무효화, 키별/전체 bounded nonce와 단일 인증 프로세스 잠금 구현.
- 키/정책/IP를 매 요청 확인하고 인증 허용 결과는 캐시하지 않는다. HTTP Bearer와 인증 방식 자동 후퇴는 거부한다.
- Catalog 원본 바이트 SHA-256과 실제 송신 요청을 별도 서버 서명으로 결속한다. 해석/sequence 저장 전에 검증한다.
- HTTP nginx 예제는 backend 인증을 한 번만 거친다. nginx 실제 경유 검증은 최종 단계에서 별도 수행한다.
- Windows 전체 자동화 **315개 통과**, 경고/오류 0. 신규 인증 테스트 13개: 변조·replay·키 폐기·정책/IP·Range·nonce 한도·UTC 시계 변경·재시작.
- `tools/test-intranet-auth.py`로 publish된 **Windows 서버/CLI 및 Linux 서버/CLI** 실제 실행: 외부 ZIP/JSON 접수·승인, 1.0.0/2.0.0 설치·각 실행 marker·손상 복구·키 폐기 후 거부 통과.
- 입력은 Windows의 로컬 cmd.exe 복사본과 Linux marker script이다. Unreal 패키지 검증이 아니다. 직접 API 검사이며 nginx 검증으로 간주하지 않는다.
- Windows 증거: `publish/intranet/e2e-win-01/summary.json`; WSL 증거: `/tmp/uedt-intranet-cwrbilji/summary.json`. 키/원시 로그는 커밋하지 않는다.
- 관리형 GUI 이미지·초기 설정 도구·최종 부하 검증은 다음 단계에 남아 있다.
