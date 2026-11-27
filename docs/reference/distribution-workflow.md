# ZIP·외부 release.json 통합 배포 운영

2026-09-28: 신규 사내 HTTP는 [schema 3 요청 서명 설정](intranet-auth.md)을 사용합니다. 아래 HTTPS/Bearer 예제는 기존 모드용이며 HTTP로 주소만 바꾸면 안 됩니다. ZIP·수동 승인·권한 정책·버전 격리는 두 모드에 공통입니다.

> 현재 운영 가이드 / 점검 2026-10-03, codex/operations-hardening-closure. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

설정 예시는 실제 회사 주소·계정·권한으로 바꿔야 합니다. 이전 실행 근거는 [2026-09-12 기록](archive/validation/distribution-validation.md), 성능 추가 결과와 한계는 [2026-09-28 기록](archive/validation/performance-validation.md), 남은 검증은 [보완 목록](../../IMPROVEMENTS.md)을 확인합니다.

ZIP과 JSON은 별개 파일입니다. 내부 release.json은 분류에 사용하지 않습니다. 현재 서버는 Linux systemd 서비스, 클라이언트는 Windows GUI/Linux CLI를 기준으로 운영합니다.

## 1. 서버 준비

2026-11-27 PROD-10 [로컬] 문서 대조. PROD-01D의 Rocky 8.4 격리 검증과 PROD-02D의 가상망 측정은 회사 RHEL·SELinux·스토리지 인수와 별개입니다. 회사는 미검증입니다. [실환경 이관 체크리스트](commercial-deployment.md#실환경-이관-체크리스트)

| 경로 | 운영 용도와 접근 |
|---|---|
| `/dt/store` | 처음에는 없거나 빈 전용 루트. 파일럿은 `dt2prod` 소유이며 로컬 `/dt` XFS 아래에 배치 |
| `/dt/store/incoming`, `/dt/store/upload-status` | 파일럿은 `dt2prod`가 배치·조회. 별도 SSH 업로더 권한은 T7에서 인수 |
| `/dt/store/{processing,releases,archive,.job-locks}` | 서비스 계정의 비공개 처리·게시 payload·작업 보관·잠금 |
| `/dt/store/authority` | `HEAD`, `catalog`, `jobs`, `auth`, `sequence`, `events` 불변 세대, `release-meta`, `progress`, 파생 `audit.jsonl`. 별도 DB 없음 |
| `/dt/config`, `/dt/keys` | 보호 설정·정책 및 배포 서명키. 운영 설정은 관리자 쓰기·서비스 읽기, `/dt/keys/signing.pem`은 서비스 계정 소유 0400 |
| `/dt/publish/dt-current`, `/dt/tools` | 파일럿의 활성 게시본 링크 및 운영 도구. 활성 게시본 아래 `server-linux-x64`·`linux-x64` |

회사 배치는 아래의 [dt2prod 설치](#dt2prod-설치와-nginx-전환)를 따릅니다. 설치 도구는 옛 정적 배포와 incoming을 보존하며 공존 구성이 참조하는 정적 경로를 유지합니다. 폐기는 옛 클라이언트 종료와 전환을 확인한 뒤 사용자 승인으로 진행합니다. SSH 로그인 키는 유지합니다.

확인형 정리: `retention inspect --config server.json`, `retention plan --jobs 실패ID --output 계획.json --config server.json`(또는 명시 `--temporary processing/폴더`). 원인/크기/선택을 확인하고 서버/watch를 정지한 뒤 `retention apply --plan 계획.json --confirm --config server.json`. 공개판·모든 승격판·승인 대기·활성 작업은 보호합니다. stale/재생성 자료는 새 계획이 필요하며 중단은 같은 승인 journal로 재개합니다. 공개판 자동 삭제는 하지 않습니다. 비공개 자료의 보관 기간은 미정입니다.

오프라인 유지보수(2026-10-03): 서버/watch/작업을 정상 중지 → `backup plan --config server.json` → `backup create --output 새백업 --config server.json` → `backup verify --backup 새백업 --config server.json`. private key는 별도 보관한다. 복원은 `restore stage --backup ... --target 빈폴더 --config 원본server.json` 후 `restore activate --target ... --confirm --config 원본server.json`이며 최신 생존 원본의 파일·보안·순번·승격·정책을 대조한다. 누락/불명이면 staged로 차단한다. 반환한 policyPath와 새 root를 운영 설정에 명시하고 별도 서명키를 준비한다. 회사 완전 유실 복구/RPO·RTO는 미검증이다.

별도 관리자 관리 설치에서는 `scripts/install-server.sh`와 [저장소 준비 스크립트](../../scripts/prepare-server-store.sh)의 전용 `uedt-distribution` 계정과 `uedt-upload` 그룹을 사용합니다. 서비스 계정이 같은 마운트의 빈 `/dt/store`를 초기화하고, 업로더에는 incoming 쓰기와 상태 읽기만 허용합니다. 서버는 구형·혼합·손상·미인식 저장소를 변경 전에 거부합니다.

별도 관리자 관리 설치의 HTTP 요청 서명용 `/dt/config/server.json` 예시입니다. `dt2prod` 회사 파일럿은 `dt-setup.conf`를 입력해 운영 설정을 생성합니다. 기존 HTTPS/Bearer 배치를 유지할 때는 해당 설정과 nginx 구성을 따릅니다.

```json
{
  "root": "/dt/store",
  "publicUrl": "http://updates.example.com",
  "listenUrl": "http://127.0.0.1:18500",
  "authenticationMode": "request-signature-v1",
  "signingKeyPath": "/dt/keys/signing.pem",
  "signingKeyId": "release-1",
  "policyPath": "/dt/config/access-policy.json",
  "limits": {"maxExpandedBytes": 536870912000, "maxFiles": 250000, "maxZipBytes": 214748364800}
}
```

사이트 주소와 크기 제한은 실제 배치에 맞게 설정합니다. 신규 HTTP는 `packaging/linux/distribution-nginx-http-signature.conf`를 회사 주소에 맞게 검토하여 [A2 설치](feature-workflow.md#a2-linux-서버-구성--관리자)의 입력으로 사용합니다. 기존 HTTPS/Bearer 예시는 `packaging/linux/distribution-nginx.conf`이며 TLS 인증서와 nginx의 auth_request 모듈을 확인합니다. nginx 설정은 `nginx -t` 후 적용하고 backend 18500은 외부에 열지 않습니다.
새 전용 HTTPS 가상 호스트에는 기존 `/projects`, `/catalogs`, json 확장자별 공개 location을 혼합하지 않습니다. 공개 파일 루트를 지정하지 않아 incoming/authority/키에 접근할 수 없습니다.

### dt2prod 설치와 nginx 전환

RHEL 8.4 x86-64의 기존 nginx 1.14.1 포트 80에서 request-signature-v1 파일럿을 준비합니다. 기존 정적 경로와 공존하는 `coexist`가 기본입니다. 회사에서 옛 클라이언트 사용 종료를 확인한 경우 `cutover`로 설정하면 정적 URL은 404로 응답합니다. 승인된 HTTP origin은 첫 게시 전에 확정하고 PC 보호 설정에도 같은 값을 사용합니다. 이 HTTP 경로는 요청 인증과 메타데이터 서명을 사용하며 전송은 평문입니다.

#### dt2prod 운영자

1. 전달받은 번들 SHA-256을 신뢰된 채널에서 대조합니다. Linux 터미널의 새 작업 폴더에서 번들을 풀고 `dt-setup.conf.example`을 `dt-setup.conf`로 복사합니다. `publicUrl`에 회사의 승인된 포트 80 origin, `signingKeyId`에 서명 키 ID, `legacyProbe`에 인증 없이 200인 옛 파일의 URL 경로를 입력합니다. 설정은 JSON입니다.
2. `check`는 로컬 XFS `/dt`의 쓰기·실행 권한, 85% 디스크 중단선 및 배치 예상 공간, nginx PID와 리스너, backend 포트, 부팅 수단과 번들 무결성을 조회합니다. 70%부터 경고합니다. systemd 사용자 서비스를 사용할 때는 `install` 전에 linger와 사용자 bus를 준비합니다. `/dt/config`, `/dt/keys`, `/dt/store`가 이미 있으면 dt2prod 소유의 비공개 디렉터리여야 합니다. 기존 키·설정·권위 저장소와 이름이 충돌하면 담당자가 경로별로 검토합니다. `/dt` 위임이 필요한 경우 해당 경로의 권한을 먼저 조정합니다.
3. `install`은 서명키와 빈 권한 정책을 만든 뒤 `127.0.0.1:18500`의 Catalog 401을 확인합니다. `/dt/.dt-legacy`의 옛 정적 파일과 incoming은 별도 폴더로 보관합니다. 기존 nginx의 문서 루트는 호환 링크로 계속 접근합니다. `admin-request`로 생성한 요청서와 신뢰된 번들 해시를 담당자에게 전달합니다. 적용 완료 후 `verify`로 loopback·공개 Catalog 401과 옛 경로 200/coexist 또는 404/cutover를 확인합니다.

```bash
./dt-setup.sh check && ./dt-setup.sh install
./dt-setup.sh admin-request
./dt-setup.sh verify
```

설치 로그는 각 단계의 표준 출력과 서비스 로그에 남습니다. linger와 사용자 bus가 준비된 경우 `journalctl --user -u ue-dt-distribution.service`로 확인합니다. crond 예비안은 `/dt/.dt-setup/cron.log`를 사용하며, 부팅 후 서비스를 시작합니다. crond 예비안에서 프로세스가 종료되면 같은 번들의 `install`로 다시 시작하고 로그 크기는 호스트 정책으로 관리합니다. 프로세스와 운영자는 같은 UID를 쓰므로 키와 게시본의 접근 주체도 같습니다.

DT-SETUP의 [로컬] Rocky 8.4 검증은 nginx 1.14.1·XFS에서 공존/전환/실패 원복, crond 경로와 게시본 연속 원복을 확인했습니다. 회사 사용자 systemd 서비스 및 실제 재부팅, 관리자 nginx 적용, PC 인증·전송은 이관 항목입니다.

새 번들에서 기존 설정을 지정해 `./dt-setup.sh update --config /승인경로/dt-setup.conf`를 실행합니다. 검증된 서버·CLI 쌍을 준비한 다음 서비스를 중지하고 `/dt/publish/dt-current`를 새 버전으로 바꿉니다. 시작이나 loopback 401 확인 실패 시 이전 게시본과 서비스를 복원합니다. 중단 기록이 있으면 설치된 번들의 `install`로 복구한 뒤 작업을 다시 시도합니다. `rollback`은 직전 update를 되돌리거나 첫 설치의 서비스를 해제하고 옛 경로를 돌려놓습니다. `/dt/store`, 권한 정책, 서명키 및 비활성 게시본은 보존합니다. 첫 설치 원복 시 담당자와 nginx 원복을 먼저 조율합니다. 옛 자료 삭제는 보관 절차를 따릅니다.

서명 공개키는 `/dt/keys/release-public.pem`에 있습니다. 초기에는 dt2prod가 승인된 ZIP 및 외부 release.json을 `/dt/store/incoming`에 배치합니다. PC 등록 및 버전 권한은 [사내 HTTP 등록 절차](intranet-auth.md)를 따릅니다.

서비스는 부팅 때 커밋된 게시본과 설치 때 확인한 저장소 신원 및 현재 HEAD를 검사합니다. 미완료 작업이나 신원이 다른 저장소는 시작을 차단합니다. 설치 기록의 신원이 누락되거나 journal 경로·신원이 일치하지 않으면 자료를 보존하고 담당자가 대조합니다. `verify`는 loopback·공개 origin의 서명 challenge 형식과 origin 결속도 검사합니다. 여러 번 갱신했다면 `rollback`을 반복해 이전 게시본을 순서대로 선택할 수 있습니다.

#### nginx 담당자

1. 요청서의 선택 모드와 loopback Catalog 401을 확인합니다. 신뢰된 채널의 번들 SHA-256 및 `SHA256SUMS`를 대조하고 root 통제 디렉터리에서 후보를 검토합니다. `nginx -T`로 기존 포트 80 default vhost, 공개·Basic 인증 경로, 캐시·오류·rewrite 상속, 접근 로그와 문서 루트 접근을 확인합니다. loopback 18500은 기존 nginx가 사용합니다.
2. 선택한 후보의 세 include 위치에서 `nginx/uedt-signed-proxy.inc`를 펼쳐 `/root/ue-dt-updates.candidate` 하나를 준비합니다. `coexist`에서는 실서버 정적 규칙을 유지하고 세 signed location 및 전용 log_format만 합칩니다. `cutover`에서는 동일 포트 80 default vhost가 세 signed location을 처리하며 나머지 경로에는 404를 반환합니다. 새 경로에서는 기존 Basic 인증과 nginx auth_request를 해제하고, backend가 실제 연결 IP와 요청 서명을 검증합니다.
   요청서에도 아래 명령이 생성됩니다. 검증한 번들은 `/root/ue-dt-server-rhel84`에 풀고 `mode`에 승인된 모드를 넣습니다.

   ```bash
   set -eu
   bundle=/root/ue-dt-server-rhel84; mode=coexist
   candidate=$(mktemp /root/.ue-dt-updates.XXXXXXXX)
   awk 'FNR==NR { proxy[++n]=$0; next } /^[[:space:]]*include \/etc\/nginx\/uedt-signed-proxy\.inc;$/ { for (i=1;i<=n;i++) print "        " proxy[i]; ++hits; next } { print } END { if (hits!=3) exit 1 }' "$bundle/nginx/uedt-signed-proxy.inc" "$bundle/nginx/$mode/ue-dt-updates.conf" > "$candidate"
   mv -T -- "$candidate" /root/ue-dt-updates.candidate
   ```

3. root 셸에서 요청서의 다섯 줄을 붙여 넣습니다. 기존 conf.d 파일을 glob 밖으로 보관한 뒤 후보를 원자 교체하고 `nginx -t` → reload를 수행합니다. 구문 검사 또는 reload가 실패하면 원본 설정을 복원하고 다시 검사·reload합니다. 출력된 백업 경로를 변경 기록에 남깁니다.
4. 요청서의 공개 Catalog curl이 401을 반환하는지 확인합니다. 운영자가 `verify`로 같은 origin과 옛 경로를 확인합니다. 문제 발생 시 요청서의 백업 경로를 사용해 nginx를 원복합니다. 사용자 unit이 부팅 전부터 필요하고 linger가 비활성일 때 요청서의 `loginctl enable-linger dt2prod`를 적용할 수 있습니다. 설치된 crontab 방식을 systemd로 전환할 때에는 nginx·서비스 변경 창에서 설치 원복 후 같은 설정으로 재설치합니다. 회사 systemd 사용자 서비스, 실제 호스트 재부팅, 실제 PC 인증과 Range는 이관 시 확인합니다.

#### 번들 제작

저장소 루트에서 Linux x64 self-contained 다중 파일 게시본 두 개를 준비하고 Linux 또는 WSL에서 묶습니다. 현재 ELF 의존 버전과 x86-64를 검사하며 GLIBC 2.28 초과는 번들 제작을 중단합니다.

```bash
dotnet publish src/UeDtLauncher.DistributionServer -c Release -r linux-x64 --self-contained true -o publish/dt-server
dotnet publish src/UeDtLauncher -c Release -r linux-x64 --self-contained true -o publish/dt-cli
python3 tools/build-dt-setup-bundle.py --server publish/dt-server --cli publish/dt-cli --output publish/dt-bundle
```

제작기는 `ue-dt-server-rhel84.tar.gz`와 SHA-256을 출력합니다. 새 작업 폴더에 풀고 `sha256sum -c SHA256SUMS`로 파일 무결성을 확인합니다. [Rocky 8.4 시험 구성](../../tools/docker/prod-server/README.md)

## 2. 서명과 PC 권한

```text
UeDtLauncher generate-signing-key --private-key release-private.pem --public-key release-public.pem
```

생성한 개인키는 준비 중 0600으로 보호하고 신규 서버에서는 `/dt/keys/signing.pem`을 서비스 계정 소유 0400으로 배치합니다. 공개키만 클라이언트에 배포합니다. 키 교체 때 새 공개키를 먼저 배포하고 server.json의 keyId/개인키를 바꿉니다. 이미 게시된 Manifest 검증을 위해 사용 중인 기존 공개키를 제거하지 않습니다.

정책 파일 예시:

```json
{"clients":[
 {"id":"pc-a","addresses":["10.10.20.15"],"grants":[
  {"projectId":"demo","environment":"prod","channel":"stable","versions":[]}
 ]},
 {"id":"pc-dev","addresses":["10.10.30.0/24"],"grants":[
  {"projectId":"demo","environment":"prod","channel":"stable","versions":[]},
  {"projectId":"demo","environment":"dev","channel":"dev","versions":["1.2.0"]}
 ]}
]}
```

빈 versions는 해당 트랙의 모든 버전을 의미합니다. grants가 비어 있으면 아무것도 허용하지 않습니다. 정책은 요청 시 다시 읽으므로 임시 파일에 작성 후 rename으로 적용합니다. 오류가 있으면 접근을 허용하지 않습니다.

```text
UeDtLauncher.DistributionServer token-issue pc-a --config /dt/config/server.json
UeDtLauncher.DistributionServer token-revoke pc-a --config /dt/config/server.json
UeDtLauncher credential set --name company-distribution
```

발급 토큰은 한 번만 표시됩니다. 안전한 경로로 해당 PC에 전달하고 기록 파일/명령 이력에 남기지 않습니다. 서버에는 SHA-256만 저장합니다. revoke는 해당 PC의 기존 토큰을 모두 폐기합니다. 토큰과 실제 IP가 모두 일치해야 합니다. 클라이언트가 developer라고 주장해도 서버 권한은 늘어나지 않습니다.

## 3. 접수 및 승인

쉬운 방법은 개발 PC의 `UeDtLauncher upload`와 서버 운영자의 `UeDtLauncher.DistributionServer console --config server.json`입니다. Windows PowerShell·Linux 터미널의 입력과 정상 결과는 [B2~B6 실행 안내](feature-workflow.md#b2-releasejson-만들기--개발-pc-powershell)에 있습니다. OpenSSH/known-host·incoming 권한은 최초 준비이며 이후 버전마다 새 업로드 ID를 사용합니다. `--profile`은 접속 값과 identity 경로를 읽고 `--project-profile`은 프로젝트와 ZIP 내부 경로를 읽습니다. 명령줄 값이 우선합니다. dry-run은 로컬 ZIP을 검사해 생성 예정 파일과 전송 계획을 보여줍니다.

콘솔 1·2는 번호 또는 전체 ID로 선택한 비공개 snapshot을 한국어 표/상세로 표시합니다. 3승인은 버전, 4거절·5실패 재시도는 작업 ID 앞8자를 확인 값으로 입력합니다. 선택 후와 확인 후 대상을 다시 읽으며 바뀐 대상은 적용하지 않습니다. 6승격은 트랙별 현재 추천 표시 → 번호 선택 → revision·사유 → 버전 재입력 순서입니다. 승인과 승격은 합치지 않습니다. 각 작업 뒤 메뉴로 돌아가고 잘못된 메뉴는 다시 묻습니다. EOF와 메뉴0은 종료합니다.

7정리의 목록은 파일 내용 해시 없이 상태·크기·디렉터리 신원만 읽습니다. 선택 후 한 번 만든 계획의 전체 hash/inventory·보호·잠금 검사는 유지합니다. 계획은 `console-retention-<id>.json`에 보존되고 콘솔 적용 성공 후 삭제합니다. 시작 전 잠금 실패는 serve/watch 정상 중지 후 같은 계획으로 적용하고, 중간 I/O 실패는 자료가 일부 처리됐을 수 있으므로 같은 계획/journal로 재개합니다. 새 계획으로 덮지 않습니다. 8자격 조회는 비밀을 출력하지 않습니다. 메뉴/확인 입력 대기 동안 세션 전체 lease는 잡지 않으며 비터미널은 변경 없이 종료합니다. HTTP 쓰기 API는 없습니다.

Windows PowerShell에서 `upload check --profile upload.profile.json`으로 최초 준비를 확인합니다. Linux 터미널은 `./UeDtLauncher upload check --profile upload.profile.json`을 사용합니다. 키 인증 전용(BatchMode)이고 비밀번호를 묻지 않으며, 미등록 호스트 키는 최초1회 터미널 ssh로 직접 확인·등록합니다. check는 디렉터리/쓰기 권한 조회만 하므로 실제 전송·디스크 공간·서비스 읽기 ACL 인수를 대신하지 않습니다. 기본 출력은 한국어이며 기계 연동에는 `upload check`에 `--format json`을 붙입니다.

실제 전송을 수행하는 `upload`는 성공 시 소유 작업 사본만 정리합니다. `--keep-work`를 지정했거나 전송에 실패하면 사본을 보존합니다.

upload는 셸 없이 ArgumentList로 ssh/scp를 호출합니다. 원격 경로·계정/호스트·ID를 허용 문자로 검사하며 두 전송 성공 전에는 이름을 바꾸지 않습니다. 실패 시 단계·잔여 가능 자료를 확인하고 새 ID로 다시 접수합니다. `serve`와 `watch`는 첫 스캔 전에, 스캔 중에는 주기적으로, 스캔이 끝나면 다시 업로드 ID별 상태 JSON을 갱신합니다. 개발자는 같은 SSH 프로필로 `upload status --upload-id <ID> --wait`를 실행해 승인 대기까지 확인합니다. [B3 상태 조회·계정 권한](feature-workflow.md#b3-서버로-두-파일-올리기--개발-pc)

**직접 실행 대안:**

[ZIP 게시 가이드](feature-workflow.md)를 따라 `incoming/upload-id/Package.zip`, `release.json`을 올립니다. `.uploading` 이름으로 올리고 전송 완료 후 최종 이름으로 바꿉니다.

- waiting: 파일 쌍 도착 대기
- validating: 서버 전용 snapshot 복사 및 해시/ZIP 검사
- pending: 승인 대기
- publishing: 승인 기록 후 최종 디렉터리 생성 중. 중단됐다면 같은 approve 재실행
- published: 공개 목록에 등록
- failed: 파일 오류 등. inspect로 확인하고 원본을 고친 후 retry
- rejected: 운영자가 거절함. 수정한 업로드는 새 업로드 폴더를 사용

관리 명령은 전용 서비스 계정 또는 허용된 OS 관리자만 실행합니다. 원본과 외부 JSON은 job.snapshot에 기록된 비공개 폴더에 보관됩니다. approve는 서명된 릴리스를 등록하지만 latest를 바꾸지 않습니다. 첫 버전도 별도 promote가 필요합니다. latest는 그 PC가 허용받은 승격 이력의 마지막 판이며 개발자는 허용된 미승격판도 exact로 선택할 수 있습니다.

### 3.1 개발자 검증 후 추천 승격

```bash
./UeDtLauncher.DistributionServer promotion inspect --project-id demo --environment prod --channel stable --platform windows-x64 --config /dt/config/server.json
# inspect의 revision을 아래 N으로 치환. 첫 승격은 0.
./UeDtLauncher.DistributionServer promote --project-id demo --environment prod --channel stable --platform windows-x64 --version 1.2.0 --expected-revision N --reason '개발자 실행 검증 통과' --config /dt/config/server.json
```

프로젝트/환경/채널/플랫폼마다 독립적으로 지정합니다. Linux 판은 platform=linux-x64로 실행합니다. 승격은 파일·사용 중 앱·PC 권한을 변경하지 않습니다. v2 권한이 없는 PC는 허용된 이전 승격판을 추천받고, 승격 이력이 전혀 없는 PC는 지정 대기 안내를 봅니다. 이미 최신인 대상과 현재 revision으로 반복하면 no-op입니다. revision 변경 오류가 나면 inspect를 다시 확인한 후 판단합니다.

### 3.2 파일 저장소의 승격 형식 확인

이 통합 빌드의 운영 준비는 새 빈 root에서 시작합니다. 관리자는 서버 터미널에서 이미 초기화한 순수 파일 저장소의 설정으로 아래 명령을 실행할 수 있습니다. 두 명령은 모두 조회만 수행합니다.

```bash
./UeDtLauncher.DistributionServer promotion migrate --dry-run --config /dt/config/server.json
./UeDtLauncher.DistributionServer promotion migrate --apply --config /dt/config/server.json
```

정상 파일 저장소는 `ready=true`, `applyRequired=false`로 종료합니다. 구형 SQLite 저장소, 혼합 루트, 손상된 루트는 파일을 변경하기 전에 오류로 종료합니다. 이전 승격 이력이 필요한 운영 데이터는 기존 배치와 백업을 보존합니다.

PROD-01S3에서는 파일 저장소를 명시적으로 주입한 경로에서 게시·승격·Catalog·순번을 검증합니다. 게시된 서버의 일반 승인·승격·serve 진입점과 백업·복원·정리는 PROD-01S5에서 함께 연결합니다. Catalog는 요청마다 HEAD를 읽고 릴리스와 승격 이력을 한 스냅샷에서 가져옵니다. 경로와 SHA-256이 같은 불변 문서의 파싱 결과를 재사용하며, 권한은 매 요청에서 판정합니다.

API v1의 `?selectionPolicy=explicit-promotion-v1` 요청은 전체 허용 승인 목록·추천을 받습니다. query 없는 구형 요청은 승격된 허용 목록만 받습니다. 서명/권한/Range·Catalog freshness 형식은 유지합니다. [검증·호환 범위](archive/validation/release-promotion-validation.md)

## 4. 클라이언트

Agent 관리 설정에 `distributionServerUrl`, `installDir`(버전 설치 루트), `stateRootDir`, 공개키, credential 이름을 지정합니다. Windows 기본 위치는 ProgramData/UE-DT Launcher/config/launcher.config.json, Linux는 /etc/ue-dt-launcher/launcher.config.json입니다.

```json
{
 "schemaVersion":2,"deploymentMode":"managed-agent",
 "distributionServerUrl":"https://updates.example.com",
 "installDir":"/var/lib/ue-dt-launcher/apps",
 "stateRootDir":"/var/lib/ue-dt-launcher/state",
 "requireSignedManifests":true,
 "security":{"credentialName":"company-distribution","allowedDownloadHosts":["updates.example.com"],
 "trustedSigningKeys":[{"keyId":"release-1","publicKeyPath":"release-public.pem"}]}
}
```

Windows 경로는 Windows 관리 디렉터리로 지정합니다. 일반 UeDtLauncher.exe 또는 개발자 UeDtLauncher.Developer.exe를 배포하며 설정으로 종류를 바꾸지 않습니다. GUI는 `gui --config` → 실행 파일 옆 화면 설정만 탐색합니다. 서비스 운영 설정은 자동 탐색하지 않고 Agent만 사용합니다. [최소 화면 설정](guide-03-launcher-usage.md)을 별도로 준비하세요.

위 JSON은 주요 필드 예시이며 회사 PC에 그대로 복사하는 완성 설정이 아닙니다. 프로젝트 ID·대상 OS·일반/개발자 표시 설정과 로그 쓰기 권한을 함께 점검합니다. Agent 서비스 계정이 credential과 공개키를 읽을 수 있어야 합니다. Linux credential 파일의 0600 및 Windows 서비스 ACL을 실제 설치 환경에서 확인하세요.

```text
UeDtLauncher agent update --project demo --environment dev --channel dev --version 1.2.0
```

정확한 선택을 지원하는 Agent가 필요합니다. 서버 권한을 재확인하고 apps/demo/dev/dev/1.2.0/windows-x64 등 버전 전용 폴더로 설치합니다. Linux CLI의 portable 설정에도 distributionServerUrl을 사용하면 동일한 서명·서버 권한 검사를 거칩니다.

새 버전은 별도 설치 디렉터리를 사용합니다. 같은 트랙의 최근 정상 설치에서 검증 후 복사하는 기능이 추가됐습니다. 네트워크 전송은 줄일 수 있지만 독립된 복사본이므로 설치 디스크 공간은 여전히 필요합니다. repair는 재사용하지 않습니다. [성능 설정과 제한](archive/validation/performance-validation.md)

기존 설치 복사 이전은 `import-install --config <기존 설정> --destination-root <새 앱 루트>`로 사전 검사하고 `--apply`로 복사합니다. 원본은 삭제하지 않습니다. 새 설정으로 첫 check/update를 실행해 버전별 설치 상태를 다시 기록합니다. 기존 사용자 저장 데이터는 원본을 보존하고 실제 UE 프로젝트의 데이터 경로에 맞춰 별도 이전합니다.

## 5. 유지보수 및 검증

### 서버 용량과 보관

250 GB 기준 175 GB(70%)에서 운영자가 경고하고 212.5 GB(85%)에서 새 승인을 중단합니다. `df`로 파일시스템 점유를 확인하고 `usage --config /dt/config/server.json`으로 저장소 점유를 확인합니다. 보호된 공개판은 보존하고 확인형 정리 또는 용량 증설 후 여유를 재확인합니다. 용량 기준은 운영 절차에 적용합니다.

| [로컬] 승인 완료 점유 배수 `k` | m7at10 7.12 GB 환산 | ma0t10 0.556 GB 환산 |
|---|---:|---:|
| PROD-01D-r2, ma0t10 사본 `k=3.0045222` | 8개 | 114개 |
| PROD-02D, 20 GB 시험 파일 포함 `k=4.00000370` | 6개 | 86개 |

환산은 한 종류만 보관하면서 OS·기타 20 GB, 다음 업로드 임시 여유 `2S`를 확보한다고 가정한 `⌊(212.5−20−2S)/(k×S)⌋`입니다. `S`는 펼친 릴리스 크기(GB)입니다. 승인 완료 `k`를 다른 패키지에 적용한 계획값이며 회사에서 보관 수를 측정한 값은 아닙니다. PROD-02D의 `k`는 7개 목표 기준 3.57을 초과했습니다. 회사 T4/T5의 여유와 RTO는 이관에서 측정합니다. 주간 오프라인 백업 목표 RPO는 1주이며, 최신 생존 원본을 확인할 수 없는 전손은 서버 재구축·원본 패키지 재게시로 대응합니다.

2026-10-06의 `retention plan`은 schema2입니다. 파일 inventory와 승인한 빈 디렉터리의 OS 신원까지 기록합니다. 적용 도중 새 파일·폴더가 생기면 재귀 삭제하지 않고 quarantine과 journal을 보존합니다. schema1 계획은 적용하지 않으며, 미시작 계획은 `retention plan`으로 다시 생성·검토하고 중단된 기존 journal은 관리자 수동 점검 대상으로 남깁니다. 삭제가 끝난 파일을 되돌려 복구했다고 표시하지 않습니다. 복원 활성화의 권위 세대·정책·서명 검증이나 fence 제거 직전 실패는 공개 차단을 유지하며 재시도 시 생존 원본의 최신 폐기·순번을 다시 확인합니다. 예외 시험과 실제 정전 내구성 보장은 구분합니다.

서버 `intakeWorkers`는 기본1/최대2입니다. 접수 상태·점유는 `authority/HEAD`가 선택한 jobs 세대에서 읽습니다. OS 작업 잠금은 진행 중 폴더를 보호합니다. inspect의 단계·처리 바이트·예상 추가 공간은 현재 claim의 별도 진행 파일을 읽습니다. 이 파일이 없거나 손상되면 진행 수치가 비어 있을 수 있습니다. 게시·승격·인증·순번·정리 이력도 같은 파일 저장소를 사용합니다. 게시 상태와 공개 릴리스 색인, 감사 이벤트는 하나의 HEAD 변경으로 함께 반영됩니다. 목록과 상세 조회의 게시 완료 시각은 이 세대에 기록됩니다. 구/신 서버를 같은 root에서 동시에 실행하지 마세요. 2-worker는 선택 기능이며 API 간섭까지 함께 측정해야 합니다. [측정·검증](archive/validation/operations-closure-validation.md)

`usage`는 용량 조회입니다. 운영 정리는 앞의 `retention inspect → plan → apply --confirm` 절차를 사용합니다. 기존 `cleanup`은 scratch 조회용이며 `cleanup --apply`는 거부됩니다. 중단 journal의 이동/삭제 의도와 directory identity를 유지하고 재생성 source를 수집하지 않습니다. 완료 삭제는 jobs 권위 영역의 삭제 ledger에 남깁니다.
접수·승인 상태 변경과 다운로드 거부는 권위 이벤트에 기록합니다. `authority/audit.jsonl`은 이벤트에서 복원할 수 있는 조회용 파생 파일입니다. nginx 접근 로그는 토큰을 포함하지 않아야 하며, 서비스 로그와 함께 접근 거부를 확인합니다.

백업/복원은 앞의 전용 CLI로 수행합니다. 미완료 retention을 먼저 정리하고, `backup create/verify`로 HEAD·참조 세대·전체 이벤트·release-meta·audit.jsonl·릴리스 파일의 inventory/hash와 정책을 확인합니다. private signing key는 별도 보호 백업이 필요합니다.

HTTP 서버는 요청마다 HEAD 원문을 읽고 권한과 서명을 확인합니다. 새 catalog 경로·해시의 메타데이터를 모두 검증한 뒤 Catalog와 개별 다운로드에 공유합니다. 집합 하나를 릴리스·승격 각각 최대 1,024개, 추정 4 MiB까지 재사용합니다. Catalog 응답은 색인 원문을 확인합니다. 자격 영역과 개별 다운로드의 색인은 HEAD 참조가 같으면 검증된 결과를 재사용하며 해당 세대 파일의 존재와 링크를 확인합니다. 다운로드는 선택한 릴리스 및 파일 경로를 확인합니다. 운영 검사·백업·복구는 권위 원문과 관련 메타데이터를 직접 검사합니다.

`restore stage`의 새 비공개 root는 모든 live 명령이 차단됩니다. `activate`는 현재 생존 원본의 순번·승격·폐기/만료·정책, 필수 작업/릴리스 전체 파일과 서명된 Manifest를 확인합니다. 원본과 대상의 자격·순번 등 권위 문서를 서버 시작과 같은 규칙으로 확인합니다. 최신 HEAD와 참조 세대를 복사하고 검증한 뒤 staged fence를 마지막에 해제합니다. 의도된 삭제 이력이 없는 누락, 추가 파일, 손상 서명, 확인할 수 없는 과거 signing key는 차단합니다. 현재 자료를 포함한 새 backup으로 다시 stage하거나 관리자 점검을 수행하며 client trust를 삭제하지 않습니다. 원본 완전 유실 복구와 회사 RPO/RTO는 별도입니다.

`tools/test-distribution-e2e.sh <Linux서버exe> <Linux런처exe> <nginx>`는 임시 디렉터리에서 TLS·두 파일 접수·승인·서명·IP 차단·위조 헤더·Range·두 버전 동시 설치·Linux 실행권한·repair·토큰 폐기를 검증합니다. 회사 서버 및 실제 Unreal 패키지에 대한 실측 결과와 이 테스트 결과는 구분합니다.

IAccessPolicyProvider는 향후 회사 백엔드 API 구현으로 교체할 수 있는 서버 측 확장 지점입니다. 현재는 로컬 파일 구현만 제공합니다.
