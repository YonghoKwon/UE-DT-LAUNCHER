# 가이드 1편 — 리눅스 업데이트 서버 세팅 (처음부터 끝까지)

> 이 문서는 **빈 RHEL 8.4 (또는 Rocky/Alma 8.x) 서버**에서 시작해서, 클라이언트 런처가 접속할 수 있는 업데이트 서버가 완성될 때까지의 전 과정을 다룹니다.
> 명령어는 모두 복사해서 그대로 실행할 수 있고, 각 단계마다 "확인 방법"이 있습니다.
>
> 관련 문서: [2편 — 패키징 파일 업로드(릴리스 퍼블리시)](guide-02-publish-package.md) · [3편 — 런처 사용법](guide-03-launcher-usage.md)
>
> 기존의 `reference/redhat-distribution-server.md`, `reference/company-rhel84-dt-update-server.md`, `reference/offline-linux-update-server-setup.md` 문서와 내용이 겹치는 부분은 **이 문서를 정본**으로 보면 됩니다.

## 전체 그림

```text
[패키징 PC]                [리눅스 업데이트 서버]                [클라이언트 PC들]
UE 패키징 빌드   ──업로드──▶  nginx (정적 파일 서버)   ◀──HTTP──  일반 PC (GUI 런처)
                            /srv/ue-dt-updates/               개발자 PC (GUI/CLI)
                              catalogs/   ← 어떤 배포가 있는지   픽셀 스트리밍 서버 (service 모드)
                              projects/   ← 실제 파일 + manifest
```

서버가 하는 일은 단순합니다. **JSON 2종(catalog, manifest)과 패키징 파일을 HTTP로 서빙**하는 것뿐입니다.
모든 판단(어떤 버전을 받을지, 어떤 파일이 바뀌었는지)은 클라이언트 런처가 합니다.

## 1단계. 디렉터리 구조 만들기

루트 디렉터리는 `/srv/ue-dt-updates`를 권장합니다. (회사 서버처럼 `/dt/ue-dt-updates`를 써야 하면 아래 모든 명령에서 경로만 바꾸면 됩니다.)

```bash
sudo mkdir -p /srv/ue-dt-updates/{catalogs/{general,developer},projects}
sudo useradd -r -s /sbin/nologin uedt 2>/dev/null || true   # 배포 전용 계정(이미 있으면 무시)
sudo chown -R uedt:uedt /srv/ue-dt-updates
sudo chmod -R 755 /srv/ue-dt-updates
```

완성될 구조는 다음과 같습니다 (릴리스가 쌓이면 이렇게 됩니다):

```text
/srv/ue-dt-updates/
├── catalogs/
│   ├── general/catalog.json          # 일반 사용자용 (공개)
│   │            catalog.json.sig     # 서명 사용 시
│   └── developer/catalog.json        # 개발자용 (Basic Auth 보호)
└── projects/
    └── ue-dt-simulator/              # ← projectId
        ├── prod/                     # ← environment: 가동(운영)
        │   └── stable/               # ← channel
        │       └── 1.0.0/            # ← version
        │           ├── windows-x64/  # ← platform
        │           │   ├── manifest.json
        │           │   ├── manifest.json.sig   # 서명 사용 시
        │           │   └── files/    # 패키징 결과물 전체
        │           └── linux-x64/
        └── dev/                      # ← environment: 개발/테스트
            └── dev/
                └── 1.1.0-dev.3/
                    └── windows-x64/
```

**구분 규칙 요약**: `projects/{프로젝트}/{prod|dev}/{stable|beta|dev}/{버전}/{windows-x64|linux-x64}/`
- 운영 정식 배포 → `prod/stable/`
- 개발·테스트 빌드 → `dev/dev/` (또는 `dev/beta/`)

확인:

```bash
find /srv/ue-dt-updates -type d
```

## 2단계. nginx 설치

인터넷이 되는 서버:

```bash
sudo dnf install -y nginx
```

오프라인(폐쇄망) 서버라면 인터넷 되는 RHEL 8.4 PC에서 RPM을 받아 USB로 옮깁니다:

```bash
# (인터넷 되는 PC에서)
dnf download --resolve --destdir=/tmp/nginx-rpms nginx
# USB로 /tmp/nginx-rpms 를 서버로 복사한 뒤 (서버에서)
sudo dnf install -y /path/to/nginx-rpms/*.rpm
```

## 3단계. 개발자 경로 보호용 비밀번호 만들기

일반 사용자에게는 `prod/stable`만 공개하고, 개발자 카탈로그와 `dev/` 경로는 HTTP Basic Auth로 보호합니다.
**런처 코드만 믿으면 안 되고, 서버에서 막아야 진짜로 막힙니다.**

```bash
sudo dnf install -y httpd-tools           # htpasswd 명령 제공
sudo htpasswd -c /etc/nginx/.htpasswd-developer devuser
# New password: ******  (개발자에게 공유할 비밀번호 입력)
sudo chmod 640 /etc/nginx/.htpasswd-developer
sudo chgrp nginx /etc/nginx/.htpasswd-developer
```

개발자를 추가할 때는 `-c` 없이: `sudo htpasswd /etc/nginx/.htpasswd-developer 다른계정`

## 4단계. nginx 설정

`/etc/nginx/conf.d/ue-dt-updates.conf` 파일을 만듭니다:

```nginx
server {
    listen 80 default_server;          # 이 서버 블록을 80포트의 기본으로 (아래 4-1 참고)
    server_name _;                     # 사내 IP로 직접 접속하면 _ 그대로 둬도 됩니다

    root /srv/ue-dt-updates;           # /dt/ue-dt-updates 를 쓰면 여기만 바꾸면 됩니다

    # 대용량 패키징 파일 전송 최적화
    sendfile on;
    tcp_nopush on;
    gzip off;                          # .pak 등은 이미 압축되어 있어 의미 없음

    # JSON은 항상 새로 받도록 (버전 갱신 즉시 반영)
    location ~* \.(json|sig)$ {
        add_header Cache-Control "no-cache";
        try_files $uri =404;
    }

    # ① 일반 공개: 운영 안정화 배포
    location ~ ^/projects/[^/]+/prod/stable/ {
        try_files $uri =404;
    }

    # ② 일반 공개: 일반 사용자 카탈로그
    location /catalogs/general/ {
        try_files $uri =404;
    }

    # ③ 인증 필요: 개발자 카탈로그
    location /catalogs/developer/ {
        auth_basic           "UE-DT Developer";
        auth_basic_user_file /etc/nginx/.htpasswd-developer;
        try_files $uri =404;
    }

    # ④ 인증 필요: dev 환경 전체 (prod/beta 등 비공개 트랙도 여기에 추가)
    location ~ ^/projects/[^/]+/dev/ {
        auth_basic           "UE-DT Developer";
        auth_basic_user_file /etc/nginx/.htpasswd-developer;
        try_files $uri =404;
    }

    # 그 외 경로는 차단 (디렉터리 목록 노출 방지)
    location / {
        return 404;
    }
}
```

각 블록의 의미:
- `location ~* \.(json|sig)$` — catalog/manifest는 캐시하지 않아 릴리스 직후 바로 새 버전이 보입니다.
- ①② — 일반 사용자 런처가 인증 없이 접근하는 유일한 경로입니다.
- ③④ — 개발자 빌드는 비밀번호 없이는 URL을 알아도 못 받습니다.
- 마지막 `location /` — 위 규칙에 안 걸리는 모든 경로(예: `prod/beta/`)는 404. 공개하고 싶은 트랙이 생기면 ① 형태로 추가하세요.

문법 검사 후 적용:

```bash
sudo nginx -t                # "syntax is ok" 확인
sudo systemctl enable --now nginx
```

### 4-1. 기본 server 블록 충돌 제거 (꼭 확인)

RHEL의 nginx 기본 설정(`/etc/nginx/nginx.conf`)에는 **80포트를 `default_server`로 점유한 기본 server 블록**(웰컴페이지, `root /usr/share/nginx/html`)이 이미 들어 있습니다. 그대로 두면 `nginx -t` 시 `conflicting server name "_"` 경고가 뜨고, **IP로 접속하면 우리 설정이 아니라 기본 블록이 응답**해서 `/projects`·`/catalogs`가 404가 됩니다.

```bash
sudo cp /etc/nginx/nginx.conf /etc/nginx/nginx.conf.bak.$(date +%F)
```
`/etc/nginx/nginx.conf` 안의 `server { ... root /usr/share/nginx/html; ... }` 블록 전체를 **주석 처리(또는 삭제)**한 뒤:
```bash
sudo nginx -t && sudo systemctl reload nginx   # 경고 없이 "syntax is ok / successful"
```

## 5단계. SELinux와 방화벽

RHEL은 SELinux가 기본 Enforcing이라, 컨텍스트를 지정하지 않으면 nginx가 파일을 읽지 못해 **403**이 납니다.

```bash
sudo dnf install -y policycoreutils-python-utils
sudo semanage fcontext -a -t httpd_sys_content_t "/srv/ue-dt-updates(/.*)?"
sudo restorecon -Rv /srv/ue-dt-updates
```

방화벽 포트 개방:

```bash
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --reload
```

> 문제 발생 시: `sudo ausearch -m avc -ts recent` 로 SELinux 거부 로그를 확인하세요.
> `semanage` 명령을 쓸 수 없는 환경이면 임시로 `/usr/share/nginx/html` 아래(기본 컨텍스트가 이미 맞음)에 두는 방법도 있지만 권장하지 않습니다.
>
> **루트를 `/dt/ue-dt-updates`처럼 비표준 경로에 둘 때**: nginx 워커(`nginx` 유저)가 상위 디렉터리 `/dt`를 통과(traverse)할 수 있어야 합니다. `/dt` 권한이 `750`이면 못 들어가니 `sudo chmod o+x /dt` 로 통과 권한을 주고, `semanage fcontext`/`restorecon`도 `/dt/ue-dt-updates` 기준으로 실행하세요. **파일을 새로 복사·압축해제할 때마다 `sudo restorecon -Rv <루트>`** 를 다시 실행해야 403이 안 납니다.

## 6단계. (권장) 서명 키 만들기

catalog/manifest 변조를 막으려면 ECDSA P-256 서명을 켭니다. **개인키는 릴리스를 만드는 관리 PC에만** 두고, 서버와 클라이언트에는 절대 올리지 않습니다.

```bash
# 관리 PC에서 1회만 실행
openssl ecparam -name prime256v1 -genkey -noout -out manifest-private-key.pem   # 비밀! 유출 금지
openssl ec -in manifest-private-key.pem -pubout -out manifest-public-key.pem    # 클라이언트에 배포
```

- `manifest-private-key.pem` → 릴리스 서명용. 관리 PC에 보관.
- `manifest-public-key.pem` → 모든 클라이언트 PC의 런처 폴더에 복사.

서명 생성/검증 방법은 [2편 6장](guide-02-publish-package.md)과 [3편 설정 표](guide-03-launcher-usage.md)를 보세요.
클라이언트에서 `requireSignedManifests: true`로 설정하면 서명 없는 배포는 아예 거부됩니다.

## 7단계. 동작 확인 (이 단계를 꼭 거치세요)

아직 릴리스를 안 올렸다면 빈 카탈로그라도 만들어 확인합니다:

```bash
echo '{"schemaVersion":1,"generatedAt":"2026-01-01T00:00:00Z","projects":[]}' | sudo -u uedt tee /srv/ue-dt-updates/catalogs/general/catalog.json
```

서버 자신에서:

```bash
# 1. 일반 카탈로그 → 200 + JSON 본문
curl -i http://127.0.0.1/catalogs/general/catalog.json

# 2. 개발자 카탈로그 비밀번호 없이 → 401 Unauthorized 가 나와야 정상
curl -i http://127.0.0.1/catalogs/developer/catalog.json

# 3. 개발자 카탈로그 비밀번호 포함 → 200 (catalog.json을 올린 뒤)
curl -i -u devuser:비밀번호 http://127.0.0.1/catalogs/developer/catalog.json

# 4. 없는 경로 → 404
curl -i http://127.0.0.1/anything-else
```

클라이언트 PC에서: 위 명령의 `127.0.0.1`을 서버 IP로 바꿔 동일하게 확인합니다. 안 되면 방화벽(5단계)부터 다시 보세요.

릴리스를 올린 뒤에는 추가로:

```bash
curl -I http://서버IP/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json   # 200
curl -I http://서버IP/projects/ue-dt-simulator/dev/dev/1.1.0-dev.3/windows-x64/manifest.json # 401
```

## 8단계. 자주 발생하는 문제

| 증상 | 원인 | 해결 |
| --- | --- | --- |
| 모든 파일이 403 | SELinux 컨텍스트 누락 | 5단계 `semanage fcontext` + `restorecon` 재실행 |
| 서버에선 되는데 클라이언트에서 안 됨 | 방화벽 | `firewall-cmd --add-service=http` 확인 |
| catalog는 받는데 파일이 404 | catalog의 `manifestUrl`/`baseUrl`이 실제 경로와 다름 | 2편의 퍼블리시 도구를 쓰면 자동으로 일치합니다 |
| 새 릴리스를 올렸는데 구버전이 보임 | JSON 캐시 | 4단계의 `no-cache` location 블록 확인 |
| 401이 나와야 할 경로가 200 | location 순서/정규식 문제 | `nginx -T`로 적용된 설정 전체를 확인 |
| `conflicting server name "_"` 경고 / IP 접속 시 웰컴페이지·404 | 기본 server 블록과 충돌 | 4-1단계(기본 server 블록 제거) 수행 |
| nginx 시작 실패 | 설정 문법 오류 | `sudo nginx -t` 출력의 줄 번호 확인 |

## 9단계. (선택) 프로젝트별 접근 IP 제한

특정 프로젝트를 **허용된 IP/대역에서만** 받게 하려면 nginx에서 막습니다(런처는 클라이언트에서 도는 프로그램이라 서버에서만 진짜 차단됩니다). 손으로 location을 짜는 대신, **허용목록 JSON에서 nginx 설정을 자동 생성**하세요.

**1) 허용목록 작성** — `project-ip-allowlist.json` (서버 측 인프라 파일. catalog와 달리 클라이언트에 공개하지 않습니다)
```json
{
  "projects": {
    "m7at10-dt": ["10.10.20.0/24", "172.18.45.0/24"],
    "ue-dt-simulator": ["10.20.0.0/16"]
  }
}
```

> 이 ACL은 **목록에 있는 프로젝트만** 제한하며, 목록에 없는 프로젝트는 기존 공개/인증 규칙을 그대로 따른다(자동 차단 아님). 어떤 프로젝트를 잠그려면 반드시 이 목록에 넣어야 합니다.

**2) nginx 설정 생성** (서버에 둔 런처 바이너리(`UeDtLauncher`, 2편 참고) 사용)
```bash
UeDtLauncher generate-nginx-acl \
  --allowlist project-ip-allowlist.json \
  --output /etc/nginx/conf.d/ue-dt-acl.conf
```
생성물은 프로젝트별 `location ~ ^/projects/<id>/ { allow …; deny all; }` 블록입니다 — **해당 프로젝트 경로 전체(prod+dev)를 허용 IP로 게이트**합니다.

**3) ⚠️ 배치 순서 중요** — nginx는 **소스 순서대로 정규식 location을 평가하고 첫 번째로 매칭되는 것**을 씁니다. 4단계의 `location ~* \.(json|sig)$` 블록이 ACL보다 위에 있으면 `manifest.json`/`catalog.json`/`*.sig` 요청이 그 블록에 먼저 걸려 ACL을 우회합니다. 따라서 이 ACL을 **server 블록의 가장 첫 번째 정규식 location**으로, 즉 **`~* \.(json|sig)$` 블록보다도 위에** (그리고 당연히 일반 `^/projects/` 블록보다도 위에) 두어야 합니다. `ue-dt-updates.conf`의 server 블록 맨 위에서 include 하세요:
```nginx
server {
    listen 80 default_server;
    server_name _;
    root /srv/ue-dt-updates;           # /dt/ue-dt-updates 를 쓰면 여기만 바꾸면 됩니다

    include /etc/nginx/conf.d/ue-dt-acl.conf;   # ← 가장 첫 정규식 location: json/sig 블록보다도 먼저
    # ... 이하 4단계의 location 들 (~* \.(json|sig)$ 포함) ...
}
```
> 생성된 `ue-dt-acl.conf`는 `conf.d/`에 있으면 nginx가 자동 include 하는데, 그 경우 순서가 보장되지 않을 수 있습니다. 순서를 확실히 하려면 위처럼 server 블록 안에서 명시적으로 `include` 하고, 자동 include와 중복되지 않게 파일을 `conf.d/` 밖(예: `/etc/nginx/ue-dt-acl.conf`)에 두는 것을 권장합니다. ACL 블록은 자체적으로 `add_header Cache-Control "no-cache" always;` 를 포함하므로, 제한된 프로젝트의 manifest/catalog도 릴리스 즉시 갱신됩니다.

**4) 적용 + 확인**
```bash
sudo nginx -t && sudo systemctl reload nginx
# manifest (json) — include가 첫 location이면 차단 IP에서 403이 떨어집니다
curl -I http://127.0.0.1/projects/m7at10-dt/prod/stable/1.0.0/windows-x64/manifest.json   # 허용 IP=200, 그 외=403
# 실제 에셋 (…/files/…) 도 같은 ACL로 게이트되는지 확인
curl -I http://127.0.0.1/projects/m7at10-dt/prod/stable/1.0.0/windows-x64/files/Game.pak  # 허용 IP=200, 그 외=403
```
허용 안 된 IP의 클라이언트는 403을 받고, 런처는 "이 네트워크(IP)에서는 접근이 허용되지 않은 프로젝트입니다" 메시지를 보여줍니다. IP가 바뀌면 JSON만 고쳐 2~4를 다시 하면 됩니다.

## 다음 단계

서버 준비가 끝났습니다. 이제 [가이드 2편 — 패키징 파일 업로드(릴리스 퍼블리시)](guide-02-publish-package.md)로 넘어가 실제 빌드를 올려보세요.
