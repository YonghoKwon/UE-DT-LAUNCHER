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
- 1단계 시점에는 계정별 실제 읽기·최종 E2E가 미완료였다. 이후 결과와 계속 남은 회사 RHEL/UE 조건은 5단계를 따른다.

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

## 5단계: 통합 결과와 남은 조건

| 검사 | 이번 결과 |
|---|---|
| Windows/Linux 전체 자동화 | 각각 331개 통과, .NET Release 경고/오류 0 |
| Python 기존 도구 회귀 | 16개 통과 |
| 최신 Windows publish E2E | `publish/intranet/e2e-final-win`: 생성 설정·두 버전 실행·repair·Agent 이미지/doctor·키 폐기·로컬 삭제 성공 |
| Linux publish + nginx + Agent | `/tmp/uedt-intranet-jq6bytsm`, `/tmp/uedt-intranet-rfr_yl8m` 통과 |
| 기존 HTTPS/Bearer | `/tmp/uedt-distribution-e2e.ImPtFt`: 설치/실행·repair·IP 위조·Range·토큰 폐기 통과 |
| 실제 Linux 계정 권한 | 기존 uedt 계정 읽기 성공, nobody 읽기 거부, uedt:uedt 0600 확인. 새 계정/서비스 생성 없음, 테스트 키 정리 |
| MSI 구성 | UNSIGNED-DEV 빌드 성공. 실제 File table에서 예제 JSON만 확인, 활성 설정 없음. 설치하지 않음 |
| RPM 구성 | 빌드 성공, 실제 패키지에서 schema 3 관리 설정 확인. 설치하지 않음. 기존 apphost duplicate build-id 경고는 남음 |
| CI | 기존 HTTPS와 새 HTTP/Agent E2E 연결. 푸시하지 않아 원격 CI 결과 미확인 |
| 진단 ZIP | publish CLI export 성공, 항목 4개에서 private PEM/privateKeyPem/Signature-Input/challenge 원문 패턴 0건. redaction 회귀에 JSON 이스케이프 사용자 경로도 포함 |

### 재현 부하

준비 1회 + 측정 3회, client별 회당 20개 요청(새 Catalog와 3-byte Range 교대). 숫자는 회별 p50/p95의 중앙값이며 실패 요청도 수집한다. [JSON 결과](intranet-auth-load-results.json)

| 연결 | 직접 API p50 / p95(ms) | nginx p50 / p95(ms) | 측정 요청 수(각 경로) | 실패(각 경로) |
|---:|---:|---:|---:|---:|
| 1 | 2.84 / 4.47 | 4.47 / 8.41 | 60 | 0 |
| 10 | 9.33 / 26.45 | 14.94 / 44.20 | 600 | 0 |
| 30 | 11.64 / 90.77 | 41.15 / 125.90 | 1,800 | 0 |

- 클라이언트 ECDSA 생성·응답 검증 시간을 포함한 왕복 측정이다. 기존 PERF-03과 프로토콜/워크로드가 달라 개선율을 계산하지 않는다. PERF-03은 75%로 유지한다.
- 직접 API server CPU 증가 10.52초, nginx 경유 12.75초. 직접 API 인증 누적 7,686.66ms/3,302회(키 조회·서명/challenge 검증, 정책/응답 서명 제외).
- DB 누적 5,546.70ms(직접)/6,757.21ms(nginx), sequence 대기 누적 91,006.48/98,766.48ms, SQLite busy 0. sequence 대기는 애플리케이션 큐이며 순수 SQLite lock 대기와 같지 않다. 준비·E2E 요청을 포함한 process 누적값이다.
- WSL1 VmHWM이 0으로 제공되어 최대 메모리는 **미수집/null**이다. 메모리 사용량 0으로 해석하지 않는다.
- 별도 반복에서 nginx의 challenge upstream timeout 1회와 startup timeout을 관측했다. 작은 challenge 응답에 Content-Length를 명시한 뒤 부하가 통과했지만 이후 startup timeout도 있어 **환경 문제가 완전히 해결됐다고 주장하지 않는다**. RHEL 대용량/장기 검증은 필요하다.

### 재실행

```text
python tools/test-intranet-auth.py --launcher <publish Launcher> --server <publish DistributionServer> --agent <publish Agent>
python3 tools/test-intranet-auth.py --launcher <Linux Launcher> --server <Linux DistributionServer> --agent <Linux Agent> --nginx <nginx> --benchmark
sudo python3 tools/test-linux-credential-ownership.py --launcher <Linux Launcher>
```

benchmark는 테스트 전용 cryptography 모듈을 사용한다. root 검사 스크립트는 기존 uedt/nobody 및 /etc/ue-dt-launcher가 있는 **로컬 시험 환경에서만** 실행한다. fixture에는 테스트 키가 있으므로 전체 폴더·원시 로그를 Git/CI artifact로 공개하지 않는다.

### 완료 판단

- USER-05: 설정 생성·진단·설치 예제 보완 범위 완료(100%). 실제 MSI/RPM 수명주기는 OPS-02에 남는다.
- SEC-03: 75%. Linux 실제 계정 확인은 완료했지만 Windows LocalService 실제 실행과 기존 현장 credential 이전은 미확인이다.
- SEC-04: 75%. 구현·기능 E2E·GUI·부하를 확인했지만 proxy 간헐 실패/메모리 공백을 남긴다. HTTP 도청/실시간 중계 위험은 설계상 남으며 회사 수용은 별도이다.
- 회사 RHEL/실제 UE·실제 인증서·설치/upgrade/repair/uninstall·장기 운영·모든 DPI 검증은 이번 완료로 표시하지 않는다. 외부 telemetry와 푸시/PR은 수행하지 않는다.
