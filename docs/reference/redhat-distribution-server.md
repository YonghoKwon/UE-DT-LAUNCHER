# Red Hat Enterprise Linux 8.4 업데이트 서버 구성 가이드

> **참고:** 이 문서의 최신 정본은 [guide-01-linux-server-setup.md](../guide-01-linux-server-setup.md)입니다. 내용이 다를 경우 정본을 따르세요.

이 문서는 하나의 Linux 서버에서 여러 프로젝트의 Windows/Linux 패키징 파일을 관리하고, 클라이언트 유형별로 다른 업데이트를 제공하기 위한 권장 구조를 설명합니다.

## 목표

- 프로젝트별 관리
- 버전별 관리
- OS별 관리
- 운영/개발 환경 분리
- 일반 사용자와 개발자용 PC 분리
- 일반 사용자는 Windows 운영 안정화 버전만 접근
- 개발자는 Windows/Linux 개발 버전 접근 가능
- 런처는 catalog를 보고 자신에게 허용된 manifest만 선택
- 서버는 general/developer 경로 자체를 분리하여 개발 버전 직접 다운로드를 차단

## 권장 URL 구조

```text
https://updates.example.com/catalogs/general/catalog.json
https://updates.example.com/catalogs/developer/catalog.json

https://updates.example.com/projects/{projectId}/{environment}/{channel}/{version}/{platform}/manifest.json
https://updates.example.com/projects/{projectId}/{environment}/{channel}/{version}/{platform}/manifest.json.sig
https://updates.example.com/projects/{projectId}/{environment}/{channel}/{version}/{platform}/files/...
```

예시:

```text
/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
/projects/ue-dt-simulator/dev/dev/1.1.0-dev.3/windows-x64/manifest.json
/projects/ue-dt-simulator/dev/dev/1.1.0-dev.3/linux-x64/manifest.json
```

## 서버 디렉터리 구조

Nginx document root를 `/srv/ue-dt-updates`로 가정합니다.

```text
/srv/ue-dt-updates/
  catalogs/
    general/
      catalog.json
      catalog.json.sig
    developer/
      catalog.json
      catalog.json.sig
  projects/
    ue-dt-simulator/
      prod/
        stable/
          1.0.0/
            windows-x64/
              manifest.json
              manifest.json.sig
              files/
                Windows/...
      dev/
        dev/
          1.1.0-dev.3/
            windows-x64/
              manifest.json
              manifest.json.sig
              files/
                Windows/...
            linux-x64/
              manifest.json
              manifest.json.sig
              files/
                Linux/...
    ue-dt-client/
      prod/
      dev/
```

## 일반 사용자와 개발자 접근 분리

런처의 catalog 필터링만으로는 보안이 충분하지 않습니다. 일반 사용자가 개발 manifest URL을 직접 알게 되면 다운로드를 시도할 수 있습니다. 따라서 서버에서도 경로를 분리해야 합니다.

권장 방식:

- `/catalogs/general/` 공개
- `/projects/*/prod/stable/` 공개
- `/catalogs/developer/` 인증 필요
- `/projects/*/dev/` 인증 필요

## Nginx 예시 설정

`/etc/nginx/conf.d/ue-dt-updates.conf` 예시:

```nginx
server {
    listen 80;
    server_name updates.example.com;

    root /srv/ue-dt-updates;
    autoindex off;

    location /catalogs/general/ {
        try_files $uri =404;
    }

    location ~ ^/projects/[^/]+/prod/stable/ {
        try_files $uri =404;
    }

    location /catalogs/developer/ {
        auth_basic "Developer Updates";
        auth_basic_user_file /etc/nginx/.htpasswd-developer;
        try_files $uri =404;
    }

    location ~ ^/projects/[^/]+/dev/ {
        auth_basic "Developer Updates";
        auth_basic_user_file /etc/nginx/.htpasswd-developer;
        try_files $uri =404;
    }

    location / {
        return 403;
    }
}
```

HTTPS 운영에서는 80 대신 443/TLS 설정을 붙이거나, 사내 reverse proxy/load balancer 뒤에 둡니다.

## 개발자 인증 파일 생성

RHEL 8.4에서 `htpasswd` 명령이 없다면 `httpd-tools` 패키지가 필요합니다.

```bash
sudo dnf install -y nginx httpd-tools
sudo htpasswd -c /etc/nginx/.htpasswd-developer devuser1
sudo htpasswd /etc/nginx/.htpasswd-developer devuser2
sudo nginx -t
sudo systemctl enable --now nginx
sudo systemctl reload nginx
```

## SELinux 권한 예시

RHEL에서 SELinux가 enforcing이면 Nginx가 `/srv/ue-dt-updates`를 읽지 못할 수 있습니다.

```bash
sudo mkdir -p /srv/ue-dt-updates
sudo chown -R nginx:nginx /srv/ue-dt-updates
sudo semanage fcontext -a -t httpd_sys_content_t '/srv/ue-dt-updates(/.*)?'
sudo restorecon -Rv /srv/ue-dt-updates
```

`semanage`가 없다면 다음 패키지가 필요할 수 있습니다.

```bash
sudo dnf install -y policycoreutils-python-utils
```

## 방화벽

```bash
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --permanent --add-service=https
sudo firewall-cmd --reload
```

## catalog.json 역할

catalog는 여러 프로젝트/버전/OS/환경 중 클라이언트가 받을 수 있는 release 목록입니다.

일반 사용자용 catalog는 Windows 운영 안정화 버전만 포함합니다.

```json
{
  "schemaVersion": 1,
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE DT Simulator",
      "releases": [
        {
          "version": "1.0.0",
          "channel": "stable",
          "environment": "prod",
          "platform": "windows-x64",
          "manifestUrl": "https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json",
          "manifestSignatureUrl": "https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json.sig",
          "allowedClientProfiles": ["general", "developer"],
          "isLatest": true
        }
      ]
    }
  ]
}
```

개발자용 catalog는 dev/windows-x64, dev/linux-x64 release를 포함할 수 있습니다.

## 클라이언트 설정 예시

일반 사용자 Windows PC:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/general/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/general/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "targetPlatform": "windows-x64"
}
```

개발자 Windows PC:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/developer/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/developer/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "targetPlatform": "windows-x64"
}
```

개발자 Linux PC:

```json
{
  "catalogUrl": "https://updates.example.com/catalogs/developer/catalog.json",
  "catalogSignatureUrl": "https://updates.example.com/catalogs/developer/catalog.json.sig",
  "catalogPublicKeyPath": "manifest-public-key.pem",
  "projectId": "ue-dt-simulator",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "targetPlatform": "linux-x64"
}
```

## manifest 생성 및 업로드 절차

예시: Windows 운영 안정화 버전 1.0.0

```powershell
.\publish\win-x64\UeDtLauncher.exe generate-manifest `
  --package-dir "C:\PackageBuilds\ue-dt-simulator\1.0.0\windows-x64" `
  --base-url "https://updates.example.com/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files" `
  --entry-point "Windows/m7at10_dt.exe" `
  --version "1.0.0" `
  --platform "windows-x64" `
  --output "manifest.json"
```

업로드 위치:

```text
/srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
/srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files/...
```

## 서명 생성

catalog와 manifest 모두 서명하는 것을 권장합니다.

키 생성:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out manifest-private-key.pem
openssl ec -in manifest-private-key.pem -pubout -out manifest-public-key.pem
```

manifest 서명:

```powershell
.\publish\win-x64\UeDtLauncher.exe sign-manifest `
  --manifest "manifest.json" `
  --private-key "manifest-private-key.pem" `
  --output "manifest.json.sig"
```

catalog 서명도 동일 명령을 사용합니다.

```powershell
.\publish\win-x64\UeDtLauncher.exe sign-manifest `
  --manifest "catalog.json" `
  --private-key "manifest-private-key.pem" `
  --output "catalog.json.sig"
```

## 운영 흐름

```text
1. UE 프로젝트 패키징
2. 프로젝트/환경/채널/버전/OS 경로에 파일 업로드
3. manifest.json 생성
4. manifest.json.sig 생성
5. catalog.json에 release 추가 또는 isLatest 갱신
6. catalog.json.sig 생성
7. 일반 사용자용 catalog에는 prod/stable/windows-x64만 노출
8. 개발자용 catalog에는 dev/windows-x64, dev/linux-x64 노출
9. 클라이언트 런처 실행
10. 런처가 자신의 config에 맞는 release를 선택하고 업데이트
```

## 주의사항

- 일반 사용자 차단은 런처 코드만 믿으면 안 됩니다.
- 개발 manifest와 개발 파일 경로는 반드시 서버에서 인증으로 막아야 합니다.
- 일반 사용자용 catalog에는 개발 release를 넣지 마세요.
- 개발자용 catalog URL과 인증 정보는 일반 사용자 PC에 배포하지 마세요.
- HTTPS를 사용하고, catalog/manifest 서명을 켜는 것을 권장합니다.
