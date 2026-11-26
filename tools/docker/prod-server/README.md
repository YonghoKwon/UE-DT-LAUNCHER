# Rocky Linux 8.4 배포 서버 통합 시험

Windows Docker Desktop의 Linux 엔진에서 새 실행 ID마다 세 서비스를 띄웁니다. 서명 origin은 모든 시험 클라이언트에서 `http://host.docker.internal:18080`입니다. Windows의 `host.docker.internal`이 이 PC의 `192.168.0.2`를 가리키는지 준비 스크립트가 검사합니다. 공개 포트는 HTTP `192.168.0.2:18080→80`과 SSH `127.0.0.1:12222→22`입니다. 배포 서버 자체는 공유 네트워크 네임스페이스의 `127.0.0.1:18500`만 사용합니다.

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

각 명령의 종료 코드를 확인합니다. `up`은 고정 Rocky 8.4 이미지에서 ELF 요구 버전을 검사하고, 시험 서명 키와 정책을 준비한 뒤 `dist`, `nginx`, `sshd`를 기동합니다. 게시된 서버는 빈 `/dt/store`에서 세대 0을 생성합니다. 기존 파일이 들어 있는 별도의 미인식 루트는 변경 전에 거부되는지 대조합니다.

`dist`가 하나의 `store` 볼륨 전체를 쓰며, `sshd`는 같은 볼륨의 `incoming`(쓰기 가능)과 `upload-status`(읽기 전용)만 각각 `volume.subpath`로 사용합니다. `config`와 `keys`는 `dist`에서 읽기 전용입니다. SSH 시험에 쓸 키는 `stage --ssh-public-key <키.pub>`로 전달합니다. 옵션을 생략하면 개인키를 보관하지 않는 시험용 공개키를 준비합니다.

## 설치·직접 접속·점유 측정

```powershell
python tools/prepare_prod_server.py connect --run-id $id --work $work --win-cohort "$win/cohort.json"
python tools/prepare_prod_server.py direct --run-id $id --work $work
python tools/prepare_prod_server.py measure --run-id $id --work $work --win-cohort "$win/cohort.json" --ue-package 'C:\Unreal Projects\ma0t10_dt\Saved\StagedBuilds\Windows'
python tools/prepare_prod_server.py restart --run-id $id --work $work
```

`connect`는 Windows와 Linux portable CLI 설치에서 `run --no-launch`를 실행하고 Manifest의 모든 파일을 비교합니다. Linux DNS 해석에 실패하면 Docker의 `host-gateway`를 시도합니다. 기본 주소에서 연결이 거부되고 LAN 공개 주소에서 401을 확인하면 일회용 Linux 컨테이너에만 `host.docker.internal:192.168.0.2`를 적용합니다. 서명 origin은 모든 경로에서 동일합니다. `direct`는 시뮬레이터의 `extra_hosts`에 `host.docker.internal:<dist IP>`를 적용합니다. 시뮬레이터는 같은 origin으로 네임스페이스의 비공개 `18080`에 직접 접속합니다. nginx가 기록한 주소를 시뮬레이터 IP와 대조합니다.

`measure`는 ma0t10 원본을 읽어 만든 사본과 ZIP member의 SHA-256을 대조합니다. 단계별 `du -sb`의 기준 바이트를 빼고 승인 완료 점유량과 최대 표본을 원본 크기로 나눕니다. 승인 중 샘플 간격은 약 50ms입니다. `restart`는 `nginx`와 `sshd`를 멈춘 뒤 `dist`를 재시작하고, `dist`가 healthy가 된 다음 두 서비스를 재시작합니다. 공유 네트워크 네임스페이스를 이 순서로 다시 연결하고 `authority`와 `releases`의 파일 SHA-256을 비교합니다.

## 정리

시험 직후 `python tools/prepare_prod_server.py down --run-id $id --work $work`를 실행합니다. 같은 실행 ID가 달린 컨테이너, 네트워크, 볼륨과 이미지가 모두 사라졌는지 확인합니다. 시작 전과 정리 후의 Docker 리소스 목록을 대조하고 빌드 캐시 수치는 `docker system df`에 기록합니다.

회사 배치는 [RHEL 설치 스크립트 초안](../../../scripts/install-server.sh)을 참고합니다. 회사 서버와 SELinux는 인수 환경에서 확인합니다.
