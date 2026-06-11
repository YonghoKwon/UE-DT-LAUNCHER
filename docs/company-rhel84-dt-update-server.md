# Company RHEL 8.4 `/dt` Update Server Runbook

> **참고:** 이 문서의 최신 정본은 [guide-01-linux-server-setup.md](guide-01-linux-server-setup.md)입니다. 내용이 다를 경우 정본을 따르세요.

이 문서는 `feature/launcher-production-hardening` 브랜치의 실제 런처 스키마에 맞춰 회사 RHEL 8.4 가상 서버에 UE-DT Launcher 업데이트 서버를 구축하는 절차입니다.

검증된 런처 흐름:

```text
launcher.config.json
  catalogUrl
    -> catalog.json
      -> projects[].releases[].manifestUrl
        -> manifest.json
          -> baseUrl + files[].url
```

중요:

- `catalog.json`은 반드시 `projects[].releases[]` 구조여야 합니다.
- `items[]` 구조는 현재 런처의 `DistributionCatalog` 모델과 맞지 않아 `Project was not found in catalog` 오류가 납니다.
- 다른 PC에서 실행하는 런처는 `localhost`가 아니라 실제 서버 IP 또는 DNS를 사용해야 합니다.

## 1. 서버 경로와 URL

실행 팀은 `/dt` 아래 권한이 있으므로 업데이트 루트는 `/dt/ue-dt-updates`로 둡니다.

```text
/dt/ue-dt-updates/
  catalogs/
    developer/
      catalog.json
    general/
      catalog.json
  projects/
    m7at10/
      dev/
        stable/
          1.0.3/
            windows-x64/
              manifest.json
              files/
                m7at10_dt.exe
                Engine/
                m7at10_dt/
```

nginx는 `/updates/` URL을 `/dt/ue-dt-updates/`로 매핑합니다.

```text
http://<server>/updates/catalogs/developer/catalog.json
http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json
http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files/m7at10_dt.exe
```

## 2. Windows PC에서 nginx 설치 파일 준비

인터넷이 되는 Windows PC에서 nginx.org 공식 RPM과 signing key를 다운로드할 수 있습니다.

```powershell
mkdir C:\ue-dt-offline-rpms\nginx-org
cd C:\ue-dt-offline-rpms\nginx-org

Invoke-WebRequest `
  -Uri "https://nginx.org/packages/rhel/8/x86_64/RPMS/nginx-1.30.0-1.el8.ngx.x86_64.rpm" `
  -OutFile "nginx-1.30.0-1.el8.ngx.x86_64.rpm"

Invoke-WebRequest `
  -Uri "https://nginx.org/keys/nginx_signing.key" `
  -OutFile "nginx_signing.key"
```

아래 두 파일을 폐쇄망 RHEL 8.4 서버의 `/dt/install-files/nginx-org`로 반입합니다.

```text
nginx-1.30.0-1.el8.ngx.x86_64.rpm
nginx_signing.key
```

서버에서 설치합니다.

```bash
mkdir -p /dt/install-files/nginx-org
cd /dt/install-files/nginx-org

sudo rpm --import nginx_signing.key
rpm -K nginx-1.30.0-1.el8.ngx.x86_64.rpm
sudo dnf -y install ./nginx-1.30.0-1.el8.ngx.x86_64.rpm
nginx -v
```

`rpm -K`에서 `digests signatures OK`가 나오면 서명 검증은 정상입니다.

의존성 오류가 나면 오류에 표시된 RHEL 8 x86_64 RPM을 Windows PC에서 추가로 받아 같은 디렉터리에 넣고 다시 실행합니다.

```bash
sudo dnf -y install ./*.rpm
```

## 3. nginx 설정

RHEL 계열 nginx 기본 설정에 기본 `server` 블록이 남아 있으면 `/updates/` 설정이 무시될 수 있습니다. 기존 파일을 백업한 뒤 최소 설정으로 정리합니다.

```bash
sudo cp /etc/nginx/nginx.conf /etc/nginx/nginx.conf.bak.$(date +%Y%m%d%H%M%S)
sudo tee /etc/nginx/nginx.conf >/dev/null <<'EOF'
user nginx;
worker_processes auto;
error_log /var/log/nginx/error.log warn;
pid /run/nginx.pid;

events {
    worker_connections 1024;
}

http {
    log_format main '$remote_addr - $remote_user [$time_local] "$request" '
                    '$status $body_bytes_sent "$http_referer" '
                    '"$http_user_agent" "$http_x_forwarded_for"';

    access_log /var/log/nginx/access.log main;

    sendfile on;
    tcp_nopush on;
    tcp_nodelay on;
    keepalive_timeout 65;

    include /etc/nginx/mime.types;
    default_type application/octet-stream;

    include /etc/nginx/conf.d/*.conf;
}
EOF
```

`/etc/nginx/conf.d/ue-dt-updates.conf`를 생성합니다.

```bash
sudo tee /etc/nginx/conf.d/ue-dt-updates.conf >/dev/null <<'EOF'
server {
    listen 80;
    server_name _;

    access_log /var/log/nginx/ue-dt-updates.access.log;
    error_log /var/log/nginx/ue-dt-updates.error.log warn;

    client_max_body_size 0;
    send_timeout 600s;

    types {
        application/json json;
        application/octet-stream pak ucas utoc exe dll so zip bin dat sig;
        text/plain txt log;
    }
    default_type application/octet-stream;

    location = / {
        return 200 "UE-DT update server\n";
        add_header Content-Type text/plain;
    }

    location /updates/ {
        alias /dt/ue-dt-updates/;
        autoindex off;
        try_files $uri $uri/ =404;
        add_header Cache-Control "no-store" always;
    }
}
EOF
```

서비스를 시작합니다.

```bash
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl status nginx
```

방화벽을 사용하는 경우:

```bash
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --reload
```

SELinux가 `Enforcing`인 경우:

```bash
sudo semanage fcontext -a -t httpd_sys_content_t "/dt/ue-dt-updates(/.*)?"
sudo restorecon -Rv /dt/ue-dt-updates
```

## 4. Windows.zip 배포

`Windows.zip` 안에 `m7at10_dt.exe`, `Engine/`, `m7at10_dt/`가 들어 있는 구조라면 `files` 아래에 압축 해제합니다.

```bash
mkdir -p /dt/ue-dt-updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files
unzip Windows.zip -d /dt/ue-dt-updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files
chmod -R 0755 /dt/ue-dt-updates
sudo restorecon -Rv /dt/ue-dt-updates
```

아래 파일이 존재해야 합니다.

```text
/dt/ue-dt-updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files/m7at10_dt.exe
```

## 5. manifest.json 생성

Windows 관리자 PC에서 PowerShell 스크립트로 manifest를 만들 수 있습니다.

```powershell
.\tools\generate-manifest.ps1 `
  -PackageDir "\\internal-share\ue-dt-updates\projects\m7at10\dev\stable\1.0.3\windows-x64\files" `
  -Output "\\internal-share\ue-dt-updates\projects\m7at10\dev\stable\1.0.3\windows-x64\manifest.json" `
  -ProjectId "m7at10" `
  -Version "1.0.3" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -EntryPoint "m7at10_dt.exe" `
  -BaseUrl "http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files"
```

서버에서 PowerShell 7을 사용할 수 없다면 Windows PC에서 `manifest.json`을 만든 뒤 서버로 반입합니다.

## 6. catalog.json 생성

개발자용 catalog 위치:

```text
/dt/ue-dt-updates/catalogs/developer/catalog.json
```

내용:

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-05-12T12:45:00Z",
  "projects": [
    {
      "projectId": "m7at10",
      "displayName": "m7at10 Local",
      "releases": [
        {
          "version": "1.0.3",
          "channel": "stable",
          "environment": "dev",
          "platform": "windows-x64",
          "manifestUrl": "http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json",
          "manifestSignatureUrl": null,
          "allowedClientProfiles": ["developer"],
          "isLatest": true,
          "notes": "RHEL 8.4 internal test release"
        }
      ]
    }
  ]
}
```

PowerShell 스크립트로 갱신하는 경우:

```powershell
.\tools\update-catalog.ps1 `
  -CatalogPath "\\internal-share\ue-dt-updates\catalogs\developer\catalog.json" `
  -ProjectId "m7at10" `
  -DisplayName "m7at10 Local" `
  -Version "1.0.3" `
  -Environment "dev" `
  -Channel "stable" `
  -Platform "windows-x64" `
  -ManifestUrl "http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json" `
  -AllowedClientProfiles "developer" `
  -Notes "RHEL 8.4 internal test release" `
  -SetLatest
```

## 7. launcher.config.json

다른 Windows PC에서 실행하는 런처 설정은 서버 IP 또는 DNS를 사용합니다.

```json
{
  "catalogUrl": "http://<server>/updates/catalogs/developer/catalog.json",
  "catalogSignatureUrl": null,
  "catalogPublicKeyPath": null,
  "projectId": "m7at10",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "stable",
  "versionPolicy": "latest",
  "requestedVersion": null,
  "targetPlatform": "windows-x64",
  "manifestUrl": "http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json",
  "manifestSignatureUrl": null,
  "manifestPublicKeyPath": null,
  "installDir": "app",
  "stagingDir": ".staging",
  "backupDir": ".backup",
  "installedManifestPath": "installed-manifest.json",
  "launchAfterUpdate": true,
  "repairMode": false,
  "removeFilesNotInManifest": false,
  "maxRetryCount": 3,
  "httpTimeoutSeconds": 300,
  "launchArguments": ["-log"],
  "projectAssetsDir": "assets/projects",
  "projects": [
    {
      "projectId": "m7at10",
      "displayName": "m7at10 Local",
      "description": "사내 RHEL 8.4 업데이트 서버 테스트 프로젝트입니다.",
      "status": "사내 테스트",
      "installPath": "app",
      "engineVersion": "Unreal",
      "technology": "Windows",
      "sortOrder": 0,
      "isPinned": true,
      "visibleToProfiles": ["developer"]
    }
  ],
  "packages": []
}
```

## 8. 검증

서버에서:

```bash
curl -I http://localhost/updates/catalogs/developer/catalog.json
curl -I http://localhost/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json
curl -I http://localhost/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files/m7at10_dt.exe
```

런처 PC에서:

```powershell
curl.exe -I http://<server>/updates/catalogs/developer/catalog.json
curl.exe -I http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/manifest.json
curl.exe -I http://<server>/updates/projects/m7at10/dev/stable/1.0.3/windows-x64/files/m7at10_dt.exe
```

모두 `200 OK`이면 런처 실행 준비가 끝난 상태입니다.

## 9. 오류별 확인

`Project was not found in catalog: m7at10`

- `catalog.json`에 `projects[].projectId = "m7at10"`이 있어야 합니다.
- catalog는 `items[]`가 아니라 `projects[].releases[]` 구조여야 합니다.

`No allowed release matched this client configuration`

- config의 `clientProfile`, `environment`, `channel`, `targetPlatform`과 catalog release 조건이 일치해야 합니다.
- 예: `developer/dev/stable/windows-x64`.

다운로드 404

- `manifestUrl`과 manifest의 `baseUrl`에 `localhost`가 남아 있지 않은지 확인합니다.
- nginx `location /updates/`가 `/dt/ue-dt-updates/`로 alias되는지 확인합니다.

해시 불일치

- 파일을 교체한 뒤 manifest를 다시 생성해야 합니다.

