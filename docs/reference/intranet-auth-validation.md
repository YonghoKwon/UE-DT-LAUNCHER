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

## 3단계: 관리형 GUI와 Agent 경계

- project-asset은 프로젝트/hero·thumbnail만 입력받아 현재 허용 Catalog에서 이미지를 선택한다. 임의 URL/파일 경로는 받지 않는다.
- Agent에서 검증한 이미지에 대해 64KiB 청크·1MiB IPC frame·20MiB 이미지 한도, 단일 작업 backpressure, 양쪽 길이/해시 확인을 적용했다.
- 관리형 GUI 이미지 다운로드 및 doctor는 Agent 경유. 실패 시 GUI credential 직접 접근으로 후퇴하지 않는다. Agent diagnostics의 재귀 IPC 호출도 방지했다.
- Windows 전체 자동화 **323개 통과**, Release 경고/오류 0. 신규 이미지 검사 8개: 정상 조립, offset/hash/길이/correlation/확장자/용량 거부와 임시 파일 정리.
- publish GUI·Agent·서버 실행(`publish/intranet/e2e-gui-01`): GUI의 credential 이름을 의도적으로 존재하지 않게 설정했는데도 Agent 경유 이미지와 온라인 doctor 성공.
- Computer Use로 일반 GUI의 `업데이트 서비스 정상`, 프로젝트 이미지, 2.0.0 최신 상태와 상태 새로고침 확인. 개발자 GUI의 허용 릴리스 2개와 2.0.0→1.0.0 선택 표시 확인.
- 화면 확인과 상태 조회는 실제 GUI에서 수행했다. 설치·실행·repair는 publish CLI E2E에서 수행했으며 GUI 실행 버튼을 누른 것으로 기록하지 않는다.
- 이번 GUI 테스트는 현재 Windows 사용자로 실행한 console Agent이다. 실제 LocalService ACL 격리나 모든 DPI 조합의 검증을 대체하지 않는다.

## 4단계: 초기 설정과 설치 예제

- sample-config 기본을 DistributionServer/schema 3으로 변경. OS·profile·인증·credential·배포 공개키 지정과 명시적 --force만 덮어쓰기 제공. 기존 정적 모드는 --mode legacy-catalog로 보존.
- --storage portable은 사용자 LocalApplicationData 저장소를 사용하며 관리형 저장소와 분리한다. 로컬 키 삭제는 서버 폐기와 분리한다.
- doctor는 키 형식/접근과 공개키 파싱을 확인하고 빈 trust 설정·인증 실패·권한 거부·정상 빈 목록을 구분한다.
- MSI에는 활성 파일이 아닌 launcher.config.example.json만 추가. RPM은 새 distribution-agent-linux 예제를 사용하되 %config(noreplace)는 유지.
- 설정/진단/패키징 관련 Windows 테스트 13개 통과. publish 생성 설정으로 온라인 doctor 및 전체 CLI E2E 성공(`publish/intranet/e2e-generated-01`). Linux 및 최종 installer artifact 확인은 5단계에서 수행.
- 자세한 최초 등록/반복 실행 명령은 [사내 HTTP 안내](intranet-auth.md)를 따른다.
