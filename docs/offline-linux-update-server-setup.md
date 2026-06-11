# Offline Linux Update Server Setup Guide

> **참고:** 이 문서의 최신 정본은 [guide-01-linux-server-setup.md](guide-01-linux-server-setup.md)입니다. 내용이 다를 경우 정본을 따르세요.

이 문서는 인터넷이 차단된 Linux 서버, 특히 RHEL 8.x 계열 서버에 UE-DT Launcher용 업데이트 서버를 구성하는 방법을 설명합니다.

## 1. 목표 구조

업데이트 서버는 런처가 접근할 수 있는 내부망 HTTP 서버입니다.

```text
Client PC
  UeDtLauncher.exe
  launcher.config.json
        |
        | HTTP 내부망 접근
        v
Linux Update Server
  Nginx
  /srv/ue-dt-updates
    catalogs
      general
        catalog.json
      developer
        catalog.json
    projects
      ue-dt-simulator
        prod
          stable
            1.0.0
              windows-x64
                manifest.json
                files
              linux-x64
                manifest.json
                files
        dev
          dev
            1.1.0-dev.1
              windows-x64
                manifest.json
                files
              linux-x64
                manifest.json
                files
```

일반 사용자와 개발자는 catalog를 분리합니다.

```text
catalogs/general/catalog.json   -> 일반 사용자용 prod/stable/latest
catalogs/developer/catalog.json -> 개발자용 dev/beta/stable
```

## 2. 인터넷이 안 되는 서버에서 Nginx 설치

인터넷이 차단된 서버에서는 패키지를 직접 내려받을 수 없습니다. 보통 두 가지 방법 중 하나를 사용합니다.

### 방법 A. RHEL 설치 ISO 또는 사내 미러 사용

RHEL DVD ISO가 있거나 사내 오프라인 yum/dnf 미러가 있으면 그걸 repository로 등록합니다.

예시:

```bash
sudo mkdir -p /mnt/rhel8
sudo mount -o loop /path/to/rhel-8.x-x86_64-dvd.iso /mnt/rhel8
```

repo 파일 작성:

```bash
sudo tee /etc/yum.repos.d/rhel8-offline.repo > /dev/null <<'EOF'
[rhel8-baseos]
name=RHEL 8 BaseOS Offline
baseurl=file:///mnt/rhel8/BaseOS
enabled=1
gpgcheck=0

[rhel8-appstream]
name=RHEL 8 AppStream Offline
baseurl=file:///mnt/rhel8/AppStream
enabled=1
gpgcheck=0
EOF
```

Nginx 모듈 확인 및 설치:

```bash
sudo dnf clean all
sudo dnf module list nginx
sudo dnf module enable nginx:1.14 -y
sudo dnf install nginx -y
```

서버 환경에 따라 Nginx stream 버전은 다를 수 있습니다. `dnf module list nginx` 결과에 맞춰 선택합니다.

### 방법 B. 인터넷 가능한 동일 OS 장비에서 RPM 묶음 준비

인터넷이 가능한 별도 RHEL 8.x 장비에서 Nginx와 의존성 RPM을 내려받고, USB 또는 보안 반입 절차로 오프라인 서버에 옮깁니다.

예시:

```bash
mkdir -p ~/nginx-rpms
cd ~/nginx-rpms
sudo dnf install dnf-plugins-core -y
sudo dnf download --resolve --alldeps nginx
```

오프라인 서버로 복사 후:

```bash
sudo dnf install ./*.rpm -y
```

운영 환경에서는 보안 정책상 RPM 반입 절차, 무결성 검증, 승인 절차를 따릅니다.

## 3. 업데이트 파일 루트 만들기

권장 경로:

```bash
sudo mkdir -p /srv/ue-dt-updates
sudo chown -R root:nginx /srv/ue-dt-updates
sudo chmod -R 750 /srv/ue-dt-updates
```

초기 테스트 중에는 권한 문제를 줄이기 위해 다음처럼 설정할 수 있습니다.

```bash
sudo chmod -R 755 /srv/ue-dt-updates
```

## 4. Nginx 설정

새 설정 파일을 만듭니다.

```bash
sudo tee /etc/nginx/conf.d/ue-dt-updates.conf > /dev/null <<'EOF'
server {
    listen 80;
    server_name _;

    access_log /var/log/nginx/ue-dt-updates.access.log;
    error_log  /var/log/nginx/ue-dt-updates.error.log;

    client_max_body_size 0;

    location /updates/ {
        alias /srv/ue-dt-updates/;
        autoindex off;
        sendfile on;
        tcp_nopush on;
        types {
            application/json json;
            application/octet-stream exe dll pak uasset ubulk bin zip 7z sig;
            text/plain txt log ini;
        }
        default_type application/octet-stream;
    }

    location ~* ^/updates/.+\.(json|sig)$ {
        alias /srv/ue-dt-updates/;
        add_header Cache-Control "no-store" always;
        default_type application/json;
    }
}
EOF
```

설정 확인:

```bash
sudo nginx -t
```

시작 및 자동 시작:

```bash
sudo systemctl enable --now nginx
sudo systemctl status nginx
```

## 5. SELinux 설정

SELinux가 enforcing이면 `/srv/ue-dt-updates`를 Nginx가 읽을 수 있도록 context를 부여해야 합니다.

```bash
sudo semanage fcontext -a -t httpd_sys_content_t '/srv/ue-dt-updates(/.*)?'
sudo restorecon -Rv /srv/ue-dt-updates
```

`semanage` 명령이 없다면 `policycoreutils-python-utils` 패키지가 필요할 수 있습니다. 인터넷이 안 되는 서버라면 이 RPM도 오프라인으로 반입해야 합니다.

임시 테스트 목적이라면 Nginx 기본 정적 경로를 사용하는 방법도 있습니다.

```bash
sudo mkdir -p /usr/share/nginx/html/updates
```

이 경우 Nginx 설정의 alias를 `/usr/share/nginx/html/updates/`로 바꾸면 SELinux 문제를 줄일 수 있습니다.

## 6. 방화벽 설정

내부망 클라이언트가 HTTP로 접근해야 하므로 80 포트를 열어야 합니다.

```bash
sudo firewall-cmd --permanent --add-service=http
sudo firewall-cmd --reload
sudo firewall-cmd --list-services
```

보안 정책상 특정 대역만 허용해야 하면 네트워크 팀 정책에 맞춰 zone/source 제한을 적용합니다.

## 7. 파일 배포 구조 만들기

예시 프로젝트:

```text
projectId   = ue-dt-simulator
version     = 1.0.0
platform    = windows-x64
environment = prod
channel     = stable
```

서버 경로:

```bash
sudo mkdir -p /srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files
sudo mkdir -p /srv/ue-dt-updates/catalogs/general
```

Windows 패키징 파일을 보안 반입 절차로 가져온 뒤 `files` 아래에 둡니다.

```text
/srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files/Windows/m7at10_dt.exe
/srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files/Windows/Content/Paks/...
```

## 8. manifest.json 생성 위치

인터넷이 안 되는 서버에서 manifest를 생성해도 되지만, 보통은 패키징 PC에서 manifest를 생성한 뒤 서버로 반입하는 방식이 더 편합니다.

manifest 위치:

```text
/srv/ue-dt-updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
```

manifest 예시:

```json
{
  "appId": "ue-dt-simulator",
  "version": "1.0.0",
  "channel": "stable",
  "platform": "windows-x64",
  "entryPoint": "Windows/m7at10_dt.exe",
  "baseUrl": "http://<SERVER_IP>/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/files",
  "files": [
    {
      "path": "Windows/m7at10_dt.exe",
      "sha256": "...",
      "size": 146432,
      "url": "Windows/m7at10_dt.exe",
      "executable": true
    }
  ]
}
```

`baseUrl`은 반드시 Nginx URL 기준이어야 합니다.

```text
http://<SERVER_IP>/updates/projects/.../files
```

## 9. catalog.json 생성 위치

일반 사용자 catalog:

```text
/srv/ue-dt-updates/catalogs/general/catalog.json
```

예시:

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-01-01T00:00:00Z",
  "projects": [
    {
      "projectId": "ue-dt-simulator",
      "displayName": "UE-DT Simulator",
      "releases": [
        {
          "version": "1.0.0",
          "channel": "stable",
          "environment": "prod",
          "platform": "windows-x64",
          "manifestUrl": "http://<SERVER_IP>/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json",
          "manifestSignatureUrl": null,
          "allowedClientProfiles": ["general"],
          "isLatest": true,
          "notes": "운영 안정화 릴리스입니다."
        }
      ]
    }
  ]
}
```

개발자 catalog:

```text
/srv/ue-dt-updates/catalogs/developer/catalog.json
```

개발자 catalog에는 `allowedClientProfiles`를 `developer`로 둡니다.

```json
"allowedClientProfiles": ["developer"]
```

## 10. 내부망 접근 확인

서버에서 확인:

```bash
curl -I http://localhost/updates/catalogs/general/catalog.json
curl -I http://localhost/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
```

클라이언트 PC에서 브라우저로 확인:

```text
http://<SERVER_IP>/updates/catalogs/general/catalog.json
http://<SERVER_IP>/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json
```

둘 다 열려야 런처가 접근할 수 있습니다.

## 11. 일반 사용자 launcher.config.json

일반 사용자 PC의 `launcher.config.json`은 프로젝트 목록을 자세히 들고 있을 필요가 없습니다.

권장 최소 예시:

```json
{
  "catalogUrl": "http://<SERVER_IP>/updates/catalogs/general/catalog.json",
  "catalogSignatureUrl": null,
  "catalogPublicKeyPath": null,

  "projectId": "ue-dt-simulator",
  "clientProfile": "general",
  "environment": "prod",
  "channel": "stable",
  "versionPolicy": "latest",
  "requestedVersion": null,
  "targetPlatform": "windows-x64",

  "manifestUrl": "http://<SERVER_IP>/updates/projects/ue-dt-simulator/prod/stable/1.0.0/windows-x64/manifest.json",
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
  "projects": [],
  "packages": []
}
```

`projects`는 빈 배열로 두고, 실제 프로젝트 목록은 catalog에서 관리하는 방향을 권장합니다.

## 12. 개발자 launcher.config.json

개발자 PC는 developer catalog를 바라보게 합니다.

```json
{
  "catalogUrl": "http://<SERVER_IP>/updates/catalogs/developer/catalog.json",
  "clientProfile": "developer",
  "environment": "dev",
  "channel": "dev",
  "versionPolicy": "latest",
  "targetPlatform": "windows-x64",
  "installDir": "app",
  "stagingDir": ".staging",
  "backupDir": ".backup",
  "installedManifestPath": "installed-manifest.json",
  "projects": [],
  "packages": []
}
```

## 13. 프로젝트 추가/삭제 운영 방식

클라이언트마다 `launcher.config.json`의 `projects`를 수정하는 방식은 운영에 적합하지 않습니다.

권장 방식:

```text
launcher.config.json: 클라이언트 권한/정책만 저장
catalog.json: 프로젝트 목록, 릴리스 목록, 버전, OS, manifest URL 관리
manifest.json: 실제 파일 목록 관리
```

### 프로젝트 추가

1. `/srv/ue-dt-updates/projects/<projectId>/...` 아래에 파일을 올립니다.
2. 해당 release의 `manifest.json`을 생성합니다.
3. `catalogs/general/catalog.json` 또는 `catalogs/developer/catalog.json`에 project/release를 추가합니다.
4. 클라이언트는 catalog 새로고침 후 새 프로젝트를 볼 수 있습니다.

### 프로젝트 제거

1. catalog에서 해당 project 또는 release를 제거합니다.
2. 클라이언트는 catalog 새로고침 후 목록에서 사라집니다.
3. 이미 설치된 파일 삭제는 별도 uninstall/cleanup 정책으로 처리합니다.

### 프로젝트 비활성화

완전 삭제 대신 release를 catalog에서 제거하거나 `allowedClientProfiles`에서 해당 profile을 제거합니다.

```json
"allowedClientProfiles": ["developer"]
```

위처럼 두면 일반 사용자는 해당 release를 받을 수 없습니다.

## 14. 보안 권장 사항

오프라인 서버라도 내부망 보안은 필요합니다.

```text
일반 사용자 catalog와 개발자 catalog 분리
developer catalog는 개발자 PC 대역에서만 접근 허용
manifest/catalog 서명 검증 사용
파일 반입 시 SHA-256 검증
Nginx access log 정기 확인
운영 catalog에는 prod/stable/latest만 노출
```

Nginx에서 개발자 catalog 접근을 IP 대역으로 제한하는 예시:

```nginx
location /updates/catalogs/developer/ {
    alias /srv/ue-dt-updates/catalogs/developer/;
    allow 10.10.20.0/24;
    deny all;
}
```

## 15. 운영 체크리스트

```text
[ ] Nginx 설치 완료
[ ] /updates URL 접근 가능
[ ] firewalld에서 http 허용
[ ] SELinux context 설정 완료
[ ] general catalog 접근 가능
[ ] developer catalog 접근 가능 또는 접근 제한 확인
[ ] manifest URL 접근 가능
[ ] files URL 다운로드 가능
[ ] 일반 사용자 launcher.config.json은 general catalog만 바라봄
[ ] 개발자 launcher.config.json은 developer catalog만 바라봄
[ ] 프로젝트 목록은 catalog에서 관리
[ ] 클라이언트별 projects 배열 수동 수정 금지
```
