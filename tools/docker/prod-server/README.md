# Rocky Linux 8.4 배포 서버 통합 시험

Windows Docker Desktop의 Linux 엔진에서 새 실행 ID마다 격리된 스택을 띄웁니다. 서명 origin은 모든 시험 클라이언트에서 `http://host.docker.internal:18080`입니다. Windows의 `host.docker.internal`이 이 PC의 `192.168.0.2`를 가리키는지 준비 스크립트가 검사합니다. `frontend-net`이 공개 HTTP 포트 `192.168.0.2:18080→80`과 nginx·dist가 공유하는 네트워크 네임스페이스를 유지합니다. dist는 그 네임스페이스의 `127.0.0.1:18500`에만 바인딩합니다. SSH 공개 포트는 `127.0.0.1:12222→22`입니다.

## 준비와 기동

저장소 루트에서 Windows PowerShell과 Python 3.12로 실행합니다. 기록 폴더는 실행마다 새로 사용합니다.

```powershell
$id = 'd01d' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$root = Join-Path $env:LOCALAPPDATA "uedt-prod-$id"
New-Item -ItemType Directory $root | Out-Null
$work = Join-Path $root 'fixture'
$win = Join-Path $root 'win'
$linux = Join-Path $root 'linux'
$multi = Join-Path $root 'server-multi'
$sha = git rev-parse HEAD
python tools/publish_runtime_cohort.py --output $win --source-ref $sha --rid win-x64
python tools/publish_runtime_cohort.py --output $linux --source-ref $sha --rid linux-x64
dotnet publish "$linux/source/src/UeDtLauncher.DistributionServer/UeDtLauncher.DistributionServer.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false "-p:SourceRevisionId=$sha" -o $multi
python tools/prepare_prod_server.py stage --run-id $id --work $work --linux-cohort "$linux/cohort.json"
python tools/prepare_prod_server.py up --run-id $id --work $work --server-multi $multi
```

각 명령의 종료 코드를 확인합니다. `up`은 고정 Rocky 8.4 이미지에서 ELF 요구 버전을 검사하고, 시험 서명 키와 정책을 준비한 뒤 `frontend-net`, `dist`, `nginx`, `sshd`를 기동합니다. 게시된 서버는 빈 `/dt/store`에서 세대 0을 생성합니다. 기존 파일이 들어 있는 별도의 미인식 루트는 변경 전에 거부되는지 대조합니다.

`dist`가 하나의 `store` 볼륨 전체를 쓰며, `sshd`는 같은 볼륨의 `incoming`(쓰기 가능)과 `upload-status`(읽기 전용)만 각각 `volume.subpath`로 사용합니다. SSH는 별도 `upload` 브리지에서 실행합니다. SSH 세션의 `/dt/store`는 chroot 내부 경로이며 제품 업로드에 필요한 명령과 제한된 SFTP 요청만 허용합니다. 클라이언트는 SFTP를 사용하는 OpenSSH `scp`가 필요합니다. `config`와 `keys`는 `dist`에서 읽기 전용입니다. SSH 시험에 쓸 키는 `stage --ssh-public-key <키.pub>`로 전달합니다. 옵션을 생략하면 개인키를 보관하지 않는 시험용 공개키를 준비합니다.

## 설치·직접 접속·점유 측정

```powershell
python tools/prepare_prod_server.py connect --run-id $id --work $work --win-cohort "$win/cohort.json"
python tools/prepare_prod_server.py direct --run-id $id --work $work
python tools/prepare_prod_server.py measure --run-id $id --work $work --win-cohort "$win/cohort.json" --ue-package 'C:\Unreal Projects\ma0t10_dt\Saved\StagedBuilds\Windows'
python tools/prepare_prod_server.py restart --run-id $id --work $work
```

`connect`는 Windows와 Linux portable CLI 설치에서 `run --no-launch`를 실행하고 Manifest의 모든 파일을 비교합니다. Linux DNS 해석에 실패하면 Docker의 `host-gateway`를 시도합니다. 기본 주소에서 연결이 거부되고 LAN 공개 주소에서 401을 확인하면 일회용 Linux 컨테이너에만 `host.docker.internal:192.168.0.2`를 적용합니다. 서명 origin은 모든 경로에서 동일합니다. `direct`는 시뮬레이터의 `extra_hosts`에 `host.docker.internal:<frontend-net IP>`를 적용합니다. 시뮬레이터는 같은 origin으로 네임스페이스의 비공개 `18080`에 직접 접속합니다. nginx가 기록한 주소를 시뮬레이터 IP와 대조합니다.

`measure`는 ma0t10 원본을 읽어 만든 사본과 ZIP member의 SHA-256을 대조합니다. 단계별 `du -sb`의 기준 바이트를 빼고 승인 완료 점유량과 최대 표본을 원본 크기로 나눕니다. 승인 중 샘플 간격은 약 50ms입니다. `restart`는 `dist`만 중지·시작합니다. 미인증 Catalog의 401 → 502 → 401, 프런트엔드 컨테이너 ID와 시작 시각, `authority`와 `releases`의 파일 SHA-256을 대조합니다.

## 정리

시험 직후 `python tools/prepare_prod_server.py down --run-id $id --work $work`를 실행합니다. `stage`가 만든 `docker-ownership.json`은 정리까지 유지합니다. 각 볼륨은 생성 시 기록한 소유 표식·생성 시각·마운트 지점과 사용 및 삭제 직전 값을 대조합니다. 동일 이름의 다른 볼륨이 있으면 중단해 그대로 보존합니다. 시작 전과 정리 후 Docker 리소스 목록을 대조합니다. 빌드 캐시 수치는 `docker system df`에 기록합니다.

회사 배치는 [RHEL 설치 스크립트 초안](../../../scripts/install-server.sh)을 참고합니다. 기존 키·런처 파일은 파일별로 대조합니다. 후보 nginx 설정 검사 후 원자적으로 게시하며, 실제 구성 검사가 실패하면 해당 실행이 추가한 설정을 되돌립니다. 재실행은 이미 설치된 동일 파일을 보존합니다. SELinux가 활성화된 경우 설치 전에 대상의 fcontext 매핑을 확인합니다. 회사 서버와 실제 SELinux 정책은 인수 환경에서 확인합니다.

설치 스크립트가 생성하는 `ue-dt-distribution.service`는 `Restart=on-failure`를 사용합니다. nginx는 별도 systemd 서비스로 실행하며 [서명 모드 nginx 설정](../../../packaging/linux/distribution-nginx-http-signature.conf)은 고정 loopback 주소 `127.0.0.1:18500`으로 요청을 전달합니다. 실서버 인수에서는 `systemctl restart ue-dt-distribution` 동안 공개 Catalog를 반복 조회하고, nginx의 `MainPID`가 유지되며 백엔드가 돌아온 뒤 미인증 401과 인증 다운로드가 복구되는지 기록합니다.

## dt2prod 설치 도구 시험

[운영 가이드](../../../docs/reference/distribution-workflow.md#dt2prod-설치와-nginx-전환)에 따라 서로 다른 서버·CLI 게시본으로 번들 두 개를 만듭니다. 이 시험은 01D와 같은 Rocky 8.4 digest 및 vault 패키지를 사용합니다. 마운트는 컨테이너 안의 새 backing 파일과 루프 장치 하나로 구성하며 외부 포트를 게시하지 않습니다.

```powershell
$id = 'dt-setup-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$image = "uedt-dt-setup-test:${id}"
docker --context desktop-linux build -f tools/docker/prod-server/dt-setup.Dockerfile --build-arg "RUN_ID=$id" -t $image .
docker --context desktop-linux run --name $id --label "uedt.run=$id" --network none --privileged -d $image sleep infinity
docker --context desktop-linux cp $firstBundle "${id}:/tmp/first.tar.gz"
docker --context desktop-linux cp $nextBundle "${id}:/tmp/next.tar.gz"
docker --context desktop-linux cp tools/docker/prod-server/test-dt-setup.sh "${id}:/tmp/test-dt-setup.sh"
docker --context desktop-linux exec $id sed -i 's/\r$//' /tmp/test-dt-setup.sh
docker --context desktop-linux exec $id bash /tmp/test-dt-setup.sh /tmp/first.tar.gz /tmp/next.tar.gz
```

`$firstBundle`과 `$nextBundle`은 각 tar.gz의 절대 경로입니다. 스크립트는 XFS·dt2prod·구 nginx 사이트를 만들고 사전 검사·롤백·공존·교체를 시험합니다. 일반 컨테이너에서는 cron에 등록된 부팅 명령을 dt2prod로 실행하고, 생성된 root 명령은 `systemctl reload nginx`만 `nginx -s reload`로 연결해 그대로 사용합니다. systemd 사용자 unit과 실제 재부팅은 이관에서 확인합니다.

종료 후 컨테이너와 이미지의 `uedt.run`이 이번 ID인지 확인하고 해당 리소스를 제거합니다. 스크립트의 종료 훅이 이번 루프 장치를 해제합니다. 전후 Docker 리소스 인벤토리를 대조합니다.
