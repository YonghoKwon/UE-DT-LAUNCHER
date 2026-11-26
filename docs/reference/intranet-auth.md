# 사내 HTTP + PC 요청 서명 설정

이 문서는 schema 3 `request-signature-v1`용이다. 기존 HTTPS/Bearer 설치는 그대로 유지되며 자동 이전하지 않는다. **HTTP 내용은 암호화되지 않는다.** 도청·실시간 중계 위험은 남고, 회사 운영 수용은 별도이다. 코드서명 인증서(MSI)와 무관한 기능이다.

## A. 최초 준비 — 관리자 작업

서버 CLI 수명 관리: `client-key add ... --expires-at 2030-01-01T00:00:00Z`, `token-issue pc --expires-at ...`, `token-list`, `token-revoke-id --id 관리ID`. 생략하면 기존처럼 무기한이다. 새 자격 등록·실제 연결 성공 후 이전 자격을 폐기한다. 현재 내부 DB schema6 전환 전 서버/watch/작업을 중지하며 일관된 snapshot 백업이 생성된다. 관리 목록에는 token/hash를 출력하지 않는다. `maxApiRequestsPerSecond`/`maxConcurrentDownloads`는 기본 null(비활성), 명시한 양수 한도 초과는429/Retry-After다. 실제 HTTP 슬롯 반환·만료/audit 원자성의 로컬 검증과 회사 계정/프록시 인수를 구분한다.

### PC 여러 대 일괄 등록

서버와 인증 모드가 준비된 뒤 실행한다. 아래 주소·ID는 예시다. 관리자 승인으로 배정한 ID와 공개키 출처를 확인한 뒤 입력 폴더에 넣는다.

| 순서 | 담당 / 실행 위치 | 동작 | 정상 결과 |
|---:|---|---|---|
| 1 | PC 관리자 / Windows PowerShell 또는 Linux 터미널 | PC별 `enroll prepare --name ue-dt-device --key-id pc-001-v1 --client pc-001 --output <수집 폴더>` | `pc-001.enroll.json`에 공개키·호스트명·관측 IPv4·생성 UTC를 기록한다. 같은 key ID로 다시 실행하면 키를 보존한다. 묶음 파일 교체는 `--force`가 필요하다. |
| 2 | 배포 관리자 / Linux 서버 | 공개키 묶음만 `/dt/keys/enroll/`로 전달. `client-enroll`을 `--dry-run`으로 확인하고 같은 입력으로 적용 | 모든 입력을 검사한 후 키를 등록하고 `/dt/config/access-policy.json`의 새 PC 항목을 한 번에 게시한다. 기존 PC의 주소와 grant는 유지한다. |
| 3 | PC 관리자 / Windows PowerShell 또는 Linux 터미널 | 배포 서명 공개키 `/dt/keys/release-public.pem`의 신뢰된 사본을 준비하고 `enroll finish` 실행 | 운영 설정과 `<output>.doctor.json`을 저장한다. Portable은 현재 사용자로 온라인 진단한다. 관리형은 Agent 재로딩 전 `deferred`로 기록한다. |

Windows PC 예시(관리형은 상승된 PowerShell, portable은 `--storage portable`을 두 명령 모두에 지정):

```powershell
.\UeDtLauncher.exe enroll prepare --name ue-dt-device --key-id pc-001-v1 --client pc-001 --output .\enroll
.\UeDtLauncher.exe enroll finish --server-url http://10.20.30.40 --project-id ue-dt-simulator --platform windows-x64 --credential-name ue-dt-device --signing-key-id release-1 --public-key "$env:ProgramData\UE-DT Launcher\config\release-public.pem" --output "$env:ProgramData\UE-DT Launcher\config\launcher.config.json"
```

Linux 서버 예시(초기화된 저장소와 정책을 가리키는 `--config` 사용):

```bash
UeDtLauncher.DistributionServer client-enroll --from /dt/keys/enroll/ --grant ue-dt-simulator:prod:stable --config /dt/config/server.json --dry-run
UeDtLauncher.DistributionServer client-enroll --from /dt/keys/enroll/ --grant ue-dt-simulator:prod:stable --config /dt/config/server.json
```

`--grant project:environment:channel`은 트랙마다 반복할 수 있다. `--address-source observed`는 기본값이다. NAT·DHCP 등으로 관측값을 사용할 수 없으면 `--address-source file:/dt/config/enroll-addresses.csv`를 두 서버 명령에 넣고, UTF-8 CSV에 `clientId,address` 헤더와 주소마다 `pc-001,10.10.20.15/32` 같은 행을 적는다. CSV에는 정규 형식 IP 또는 CIDR을 사용한다. IPv4는 네 개의 10진수 옥텟을 쓰고, CIDR은 정규 네트워크 주소와 앞자리 0 없는 prefix를 지정한다. CSV를 선택하면 모든 묶음에 관리자 지정 주소가 필요하다. 관리자는 묶음의 출처와 주소를 검토하고 적용 중 다른 정책 편집을 피한다.

검증 오류가 있으면 명령이 오류 목록과 함께 종료된다. 키 적용은 `client-key add`와 같은 쓰기 잠금에서 정책 교체까지 진행한다. Linux는 원래 정책의 소유자·그룹·모드·ACL을 검증한 뒤에만 키를 추가한다. 적용 중 키 등록 뒤 정책 교체에 실패하면 같은 입력으로 다시 실행한다. 이미 등록된 동일 PC·공개키는 재사용하고 정책 추가를 완료한다. 다른 PC 또는 다른 공개키에 같은 ID를 사용하면 거부한다. 폐기된 키 상태는 그대로 유지한다. 등록한 키의 배포 권한에는 정책 grant가 필요하다.

관리형 PC의 `finish --public-key`에는 관리자가 고정 위치에 미리 배치한 공개키의 절대 경로를 지정한다. Windows는 `%ProgramData%\UE-DT Launcher\config\release-public.pem`, Linux는 `/etc/ue-dt-launcher/release-public.pem`이다. 이 파일과 관리 경로의 쓰기 권한을 관리자에게 제한하고 업데이트 서비스 계정에는 읽기를 허용한다. 명령은 경로 링크와 보호되지 않은 소유권·쓰기 권한을 거부한다. Portable은 사용자 관리 경로의 공개키를 지정한다.

`finish`의 `--output`은 관리자가 미리 보호한 Agent 설정 위치 또는 portable 사용자 설정 경로를 사용한다. `--force`를 명시할 때 설정과 기존 진단 파일을 교체한다. 관리형 서비스 반영은 관리자 작업이다. 반영 후 선택 전용 화면 설정으로 `doctor --online`을 다시 실행한다. 화면 설정은 [기능 지도 A4](feature-workflow.md#a4-런처와-agent-설정--pc-관리자)를 참고한다.

### PC 개별 등록

| 순서 | 실행 위치 | 명령/작업 | 정상 결과 |
|---:|---|---|---|
| 1 | Linux 서버 관리자 | 기존 server.json의 `publicUrl`을 실제 사내 HTTP origin으로, `authenticationMode`를 `request-signature-v1`로 설정. `listenUrl`은 `http://127.0.0.1:18500` 유지 | 기존 root·서명키·policy 경로 보존. 변경 전 DB 백업, 구/신 서버 동시 실행 금지 |
| 2 | Linux 서버 관리자 | `packaging/linux/distribution-nginx-http-signature.conf`의 예시 IP를 실제 IP로 바꾸어 배치, `nginx -t` 후 운영 절차에 따라 반영 | 파일은 backend를 한 번만 통과. backend 원격 노출·공개 static alias 금지 |
| 3 | Windows PC의 관리자 PowerShell | 아래 키 생성 명령 실행 | 개인키는 보호된 저장소, 내보낸 JSON에는 공개키만 존재 |
| 4 | 관리자 간 신뢰된 경로 | `pc-001-public.json`을 배포 서버로 전달하고 공개키 fingerprint/출처 확인 | 개인키는 PC 밖으로 복사하지 않음 |
| 5 | Linux 서버 관리자 | `UeDtLauncher.DistributionServer client-key add --client pc-001 --public-key /tmp/pc-001-public.json --config /etc/ue-dt-distribution/server.json` | key ID 등록. 같은 ID 재등록/재배정 거부 |
| 6 | Linux 서버 관리자 | 기존 access-policy.json에 `pc-001`의 실제 IP/CIDR와 허용 프로젝트·환경·채널·버전 설정 | 등록만으로 권한이 부여되지 않으며 미등록/빈 grants는 거부 |
| 7 | 클라이언트 관리자 | 서버의 **배포 서명 공개키**를 신뢰된 경로로 받아 설정 옆 `release-public.pem`에 설치 | PC 요청 인증키와 다른 키임. 개인키를 설정에 넣지 않음 |
| 8 | 클라이언트 관리자 | 아래 sample-config 생성 → 검토 → Agent의 관리 설정 위치에 배치 | Windows 실제 ProgramData, Linux /etc 설정 사용. 기존 파일은 명시적 교체 전 보존 |
| 9 | 클라이언트 | Agent 시작 후 `UeDtLauncher doctor --config <설정 파일> --online` | credential 형식/접근, 공개키, 인증/서명 확인. 빈 허용 목록은 연결 장애와 구분 |

Windows 관리자 PowerShell 예시(실제 PC별 key ID 사용):

```powershell
.\UeDtLauncher.exe credential keygen --name ue-dt-device --key-id pc-001-v1 --public-out C:\Temp\pc-001-public.json
.\UeDtLauncher.exe sample-config --server-url http://10.20.30.40 --project-id ue-dt-simulator --platform windows-x64 --profile general --credential-name ue-dt-device --signing-key-id release-1 --public-key release-public.pem --output C:\Temp\launcher.config.json
.\UeDtLauncher.exe doctor --config "C:\ProgramData\UE-DT Launcher\config\launcher.config.json" --online
```

`C:\Temp`는 사전에 준비한다. sample-config는 실제 Windows ProgramData 위치를 사용한다. 저장소의 정적 예제는 표준 `C:` 배치용이므로 사용자 환경이 다르면 생성기를 사용한다.

Linux 관리형 예시(기존 RPM의 uedt 계정/폴더가 준비된 뒤 실행):

```bash
sudo /opt/ue-dt-launcher/UeDtLauncher credential keygen --name ue-dt-device --key-id server-001-v1 --public-out /tmp/server-001-public.json
/opt/ue-dt-launcher/UeDtLauncher sample-config --server-url http://10.20.30.40 --project-id ue-dt-simulator --platform linux-x64 --profile developer --public-key release-public.pem --output /tmp/launcher.config.json
sudo /opt/ue-dt-launcher/UeDtLauncher credential repair-permissions --name ue-dt-device --dry-run
```

관리형 키는 uedt 소유 0600, 부모는 root:uedt 0750이다. 변경이 필요하면 결과를 검토한 뒤 `--apply`를 명시한다. `chmod 644`로 해결하지 않는다. 신규 계정 생성·회사 서비스 재시작은 이 문서의 명령이 자동 실행하지 않는다.

portable은 `sample-config --deployment-mode portable`과 `credential keygen ... --storage portable`을 함께 사용한다. 개인키는 현재 사용자의 LocalApplicationData/UE-DT Launcher/credentials에 저장한다. 테스트용 `UE_DT_AGENT_DATA_ROOT`는 운영 환경에서 지정하지 않는다.

## B. 새 버전 배포 / C. 사용자 실행

ZIP + 외부 release.json 생성·접수·수동 승인 순서는 [기능 지도](feature-workflow.md)의 B와 동일하다. ZIP마다 PC 키를 만들지 않는다.

- 일반 사용자는 런처를 열어 상태 확인 후 설치/업데이트/실행 버튼을 사용한다. 자동 설치하지 않는다.
- 개발자 화면은 별도 `UeDtLauncher.Developer` 빌드로 실행한다. 같은 PC의 서버 허용 범위는 그대로 적용된다.
- 관리형 GUI는 목록·이미지·설치·진단을 Agent에 요청한다. GUI에 개인키 접근 권한을 부여하지 않는다.
- UI 설정과 Agent 운영 설정은 별개다. GUI는 `--config → 실행 파일 옆 화면 설정`만 탐색하며 서비스 설정으로 자동 전환하지 않는다. 화면에는 선택만 저장하고 서버 URL/credential/공개키/보호 경로는 Agent에 둔다. [최소 화면 설정](guide-03-launcher-usage.md)을 참고한다. exe 옆 파일로 Agent의 보호된 설정을 바꾸지 않는다.
- MSI는 `launcher.config.example.json`만 설치한다. 활성 설정을 자동 덮어쓰지 않는다. RPM은 `%config(noreplace)`로 기존 관리 설정을 보존한다.

## D. 문제 해결·키 교체

| 증상 | 확인/조치 |
|---|---|
| credential 검사 실패 | 관리자 `credential status --name …`로 유형·접근 확인. 관리형 `doctor`는 Agent 계정에서 검사 |
| 401 / 인증 실패 | 서버 `client-key list --config …`에서 key ID·폐기 여부 확인. 자동 Bearer fallback 없음 |
| 403 / 접근 거부 | PC 실제 IP/CIDR와 grants 확인. GUI 프로필 변경으로 해결하지 않음 |
| rate-limited / 429 | 잠시 후 조회 재시도. Retry-After가 있으면 숫자 안내 반영, 반복되면 관리자에게 요청 한도 점검 요청 |
| stale-challenge | 자동 재발급1회 이후에도 실패. PC 시계·서버 재시작·프록시를 관리자가 확인. 인증키 미등록/폐기/만료 원인을 화면에 더 노출하지 않음 |
| 연결 정상, 허용 배포 없음 | 승인된 OS별 배포와 해당 PC grants 확인 |
| 서명/요청 결속 오류 | 배포 공개키·key ID·서버 주소 확인. 검증 옵션을 끄지 않음 |
| nonce 수용 한도 초과 | 만료를 기다리고 요청 폭주 원인 확인. 살아 있는 nonce를 삭제하지 않음 |
| 기존 HTTP/Bearer 설정 | HTTPS를 유지하거나 PC 키를 별도 등록한 schema 3 설정으로 명시적으로 이전 |

교체: **새 이름/key ID로 생성 → 공개키 등록 → 새 설정 연결 확인 → 이전 key ID 폐기**.

```bash
UeDtLauncher.DistributionServer client-key revoke --key-id pc-001-v1 --config /etc/ue-dt-distribution/server.json
```

`credential delete --name …`는 로컬 파일만 삭제한다. 서버 키 폐기와 구분한다. 키 수명 자동화·회사 API·무인 주기는 후속 범위이다.

## 호환·검증 한계

- 2026-11-12: CLI/doctor/GUI는 같은 오류 코드를 사용하고 기본 CLI 오류는 지원 ID·안전한 원인·조치를 출력한다. `--error-details`는 타입 체인만 추가한다. 리디렉션은 UTF-8이며 서명·해시·nonce·sequence·요청 결속·재발급1회 계약은 유지한다.
- 개발 PC `upload`의 SSH 전송은 HTTP 요청 서명 API와 별개이다. 서버 콘솔은 로컬 기존 승인/승격/retention 경로를 사용하며 HTTP 쓰기 API·업로드 계정의 DB 조회를 추가하지 않는다. [최초 준비/반복 배포 순서](feature-workflow.md#b2-releasejson-만들기--개발-pc-powershell)
- release.json/Manifest/설치 경로/IPC v1 기존 필드는 유지한다. schema 3 설정은 구형 런처에 배포하지 않는다.
- DB v3 이전 전 자동 백업. 되돌릴 때 DB만 몰래 교체하거나 구/신 버전을 동시에 돌리지 않는다. Catalog sequence 보존도 확인한다.
- 실제 결과는 [검증 기록](archive/validation/intranet-auth-validation.md)에 구분한다. WSL/합성 테스트는 RHEL/Unreal/LocalService 운영 승인과 다르다.
