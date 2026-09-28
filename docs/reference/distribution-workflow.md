# ZIP·외부 release.json 통합 배포 운영

2026-09-28: 신규 사내 HTTP는 [schema 3 요청 서명 설정](intranet-auth.md)을 사용합니다. 아래 HTTPS/Bearer 예제는 기존 모드용이며 HTTP로 주소만 바꾸면 안 됩니다. ZIP·수동 승인·권한 정책·버전 격리는 두 모드에 공통입니다.

> 참고 가이드 / 성능 추가 점검 2026-09-28. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

설정 예시는 실제 회사 주소·계정·권한으로 바꿔야 합니다. 이전 실행 근거는 [2026-09-12 기록](distribution-validation.md), 성능 추가 결과와 한계는 [2026-09-28 기록](performance-validation.md), 남은 검증은 [보완 목록](../../IMPROVEMENTS.md)을 확인합니다.

ZIP과 JSON은 별개 파일입니다. 내부 release.json은 분류에 사용하지 않습니다. 현재 서버는 Linux systemd 서비스, 클라이언트는 Windows GUI/Linux CLI를 기준으로 운영합니다.

## 1. 서버 준비

`dotnet publish src/UeDtLauncher.DistributionServer -c Release -r linux-x64 --self-contained true -o artifacts/distribution-server`로 생성하고 `/opt/ue-dt-distribution`에 설치합니다.
전용 `uedt-distribution` 계정을 만들고 `/srv/ue-dt-distribution`을 소유하게 합니다. `processing`, `archive`, DB, 서명키는 업로드 계정에서 접근할 수 없어야 합니다. 업로드 계정에는 `incoming`만 쓸 수 있도록 그룹/ACL을 제한합니다.

`/etc/ue-dt-distribution/server.json`:

```json
{
  "root": "/srv/ue-dt-distribution",
  "publicUrl": "https://updates.example.com",
  "listenUrl": "http://127.0.0.1:18500",
  "signingKeyPath": "/etc/ue-dt-distribution/release-private.pem",
  "signingKeyId": "release-1",
  "policyPath": "/etc/ue-dt-distribution/access-policy.json",
  "limits": {"maxExpandedBytes": 536870912000, "maxFiles": 250000, "maxZipBytes": 214748364800}
}
```

사이트 주소, 인증서, 크기 제한은 실제 패키지와 디스크 용량에 맞게 설정합니다. `packaging/linux/ue-dt-distribution.service`와 `distribution-nginx.conf`를 배포합니다. nginx의 auth_request 모듈을 확인하고 `nginx -t` 후 적용합니다. backend 18500은 외부에 열지 않습니다.
새 전용 HTTPS 가상 호스트에는 기존 `/projects`, `/catalogs`, json 확장자별 공개 location을 혼합하지 않습니다. 공개 파일 루트를 지정하지 않아 incoming/DB/키에 접근할 수 없습니다.

## 2. 서명과 PC 권한

```text
UeDtLauncher generate-signing-key --private-key release-private.pem --public-key release-public.pem
```

개인키는 서버 서비스와 승인 운영자만 읽도록 0600으로 보호합니다. 공개키만 클라이언트에 배포합니다. 키 교체 때 새 공개키를 먼저 배포하고 server.json의 keyId/개인키를 바꿉니다. 이미 게시된 Manifest 검증을 위해 사용 중인 기존 공개키를 제거하지 않습니다.

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
UeDtLauncher.DistributionServer token-issue pc-a --config /etc/ue-dt-distribution/server.json
UeDtLauncher.DistributionServer token-revoke pc-a --config /etc/ue-dt-distribution/server.json
UeDtLauncher credential set --name company-distribution
```

발급 토큰은 한 번만 표시됩니다. 안전한 경로로 해당 PC에 전달하고 기록 파일/명령 이력에 남기지 않습니다. 서버에는 SHA-256만 저장합니다. revoke는 해당 PC의 기존 토큰을 모두 폐기합니다. 토큰과 실제 IP가 모두 일치해야 합니다. 클라이언트가 developer라고 주장해도 서버 권한은 늘어나지 않습니다.

## 3. 접수 및 승인

[ZIP 게시 가이드](guide-02-publish-package.md)를 따라 `incoming/upload-id/Package.zip`, `release.json`을 올립니다. `.uploading` 이름으로 올리고 전송 완료 후 최종 이름으로 바꿉니다.

- waiting: 파일 쌍 도착 대기
- validating: 서버 전용 snapshot 복사 및 해시/ZIP 검사
- pending: 승인 대기
- publishing: 승인 기록 후 최종 디렉터리 생성 중. 중단됐다면 같은 approve 재실행
- published: 공개 목록에 등록
- failed: 파일 오류 등. inspect로 확인하고 원본을 고친 후 retry
- rejected: 운영자가 거절함. 수정한 업로드는 새 업로드 폴더를 사용

관리 명령은 전용 서비스 계정 또는 허용된 OS 관리자만 실행합니다. 원본과 외부 JSON은 job.snapshot에 기록된 비공개 폴더에 보관됩니다. 디렉터리 생성만으로 공개되지 않으며 DB에 등록된 릴리스만 서비스합니다. latest는 트랙별로 가장 최근 승인된 릴리스입니다. 이전 버전은 개발자가 exact로 선택할 수 있습니다.

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

Windows 경로는 Windows 관리 디렉터리로 지정합니다. 런처 폴더의 GUI 설정은 clientProfile=general 또는 developer를 지정합니다. GUI 경로 우선순위는 `gui --config` → 실행 파일 옆 설정 → 관리 설정이며, 관리형 설치 보안 값은 Agent의 보호된 설정에서 가져옵니다.

위 JSON은 주요 필드 예시이며 회사 PC에 그대로 복사하는 완성 설정이 아닙니다. 프로젝트 ID·대상 OS·일반/개발자 표시 설정과 로그 쓰기 권한을 함께 점검합니다. Agent 서비스 계정이 credential과 공개키를 읽을 수 있어야 합니다. Linux credential 파일의 0600 및 Windows 서비스 ACL을 실제 설치 환경에서 확인하세요.

```text
UeDtLauncher agent update --project demo --environment dev --channel dev --version 1.2.0
```

정확한 선택을 지원하는 Agent가 필요합니다. 서버 권한을 재확인하고 apps/demo/dev/dev/1.2.0/windows-x64 등 버전 전용 폴더로 설치합니다. Linux CLI의 portable 설정에도 distributionServerUrl을 사용하면 동일한 서명·서버 권한 검사를 거칩니다.

새 버전은 별도 설치 디렉터리를 사용합니다. 같은 트랙의 최근 정상 설치에서 검증 후 복사하는 기능이 추가됐습니다. 네트워크 전송은 줄일 수 있지만 독립된 복사본이므로 설치 디스크 공간은 여전히 필요합니다. repair는 재사용하지 않습니다. [성능 설정과 제한](performance-validation.md)

기존 설치 복사 이전은 `import-install --config <기존 설정> --destination-root <새 앱 루트>`로 사전 검사하고 `--apply`로 복사합니다. 원본은 삭제하지 않습니다. 새 설정으로 첫 check/update를 실행해 버전별 설치 상태를 다시 기록합니다. 기존 사용자 저장 데이터는 원본을 보존하고 실제 UE 프로젝트의 데이터 경로에 맞춰 별도 이전합니다.

## 5. 유지보수 및 검증

2026-09-28 추가: 서버 `intakeWorkers`는 기본1/최대2입니다. 접수 상태의 단계·처리 바이트·예상 추가 공간을 inspect로 확인할 수 있습니다. DB schema v2로 최초 실행 시 기존 DB를 백업한 뒤 migration하며, 이전/새 프로세스를 같은 DB에 동시에 실행하지 않습니다. active_work·OS 작업 잠금이 활성 임시 폴더를 정리로부터 보호합니다. 상세 교체/복원·측정 주의는 [성능 검증](performance-validation.md)을 따릅니다.

usage는 디렉터리별 용량을 표시합니다. cleanup은 실패한 scratch 작업 폴더만 대상으로 기본 dry-run하며 `--apply`를 명시해야 지웁니다. 게시된 릴리스와 참조 중인 원본 snapshot은 삭제하지 않습니다.
DB audit에는 접수/승인 상태 변경이 기록됩니다. nginx 접근 로그는 토큰을 포함하지 않아야 하며, 서비스 로그와 함께 접근 거부를 확인합니다.

백업은 서비스를 멈춘 후 root 디렉터리 전체(DB, WAL, releases, processing 포함)를 파일 권한과 함께 복사합니다. server.json·policy·개인키는 별도 암호화 백업으로 보호합니다. 복원은 서비스 정지 상태의 빈 root에 원래 구조로 복사하고 소유자/권한을 복원한 다음 list/inspect, 서명 다운로드, 허용·거부 PC 검사를 수행합니다. sequence DB를 예전 값으로 복원하면 클라이언트의 replay 검사에 걸릴 수 있으므로 복원 시 기존 최고 sequence 이상으로 운영자가 복구해야 합니다.

`tools/test-distribution-e2e.sh <Linux서버exe> <Linux런처exe> <nginx>`는 임시 디렉터리에서 TLS·두 파일 접수·승인·서명·IP 차단·위조 헤더·Range·두 버전 동시 설치·Linux 실행권한·repair·토큰 폐기를 검증합니다. 회사 서버 및 실제 Unreal 패키지에 대한 실측 결과와 이 테스트 결과는 구분합니다.

IAccessPolicyProvider는 향후 회사 백엔드 API 구현으로 교체할 수 있는 서버 측 확장 지점입니다. 현재는 로컬 파일 구현만 제공합니다.
