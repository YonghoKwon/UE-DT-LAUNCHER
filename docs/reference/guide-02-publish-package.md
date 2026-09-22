# ZIP + 외부 release.json 배포

> 참고 가이드 / 문서 점검 2026-09-22 / 구현 기준 2cd28c8. 현재 기능은 [README](../../README.md), 미완료 항목은 [보완 목록](../../IMPROVEMENTS.md)을 따릅니다.

점검: 2026-09-22 / 구현 기준 2cd28c8. Windows와 Linux는 각각 별도의 ZIP/JSON 쌍과 업로드 폴더를 사용합니다. 아래 실행 파일 경로는 예시입니다.
ZIP 안에 배포 JSON을 넣지 않습니다. ZIP 옆에 release.json을 만들고 같은 incoming 하위 폴더로 업로드합니다.
이 문서는 기존 직접 압축 해제 / --no-copy 게시 절차를 대체합니다. 새 배포 서버에서는 공개 파일 디렉터리에 수동으로 압축을 풀지 않습니다.

1. Unreal Windows/Linux 패키징 결과를 ZIP으로 준비합니다.
2. 클라이언트 CLI로 메타데이터를 생성합니다.

```text
UeDtLauncher release-metadata --zip Windows.zip --project-id demo --version 1.0.0 --platform windows-x64 --payload-root Windows --entry-point Demo.exe --output release.json
```

Linux ZIP 내부가 Linux/Demo.sh와 Linux/Demo/Binaries/Linux/Demo라면 다음처럼 지정합니다. 경로는 payload-root 기준이므로 Linux 접두어를 다시 넣지 않습니다.

```text
UeDtLauncher release-metadata --zip Linux.zip --project-id demo --version 1.0.0 --platform linux-x64 --payload-root Linux --entry-point Demo.sh --executable-paths Demo.sh,Demo/Binaries/Linux/Demo --output release.json
```

ZIP을 열었을 때 바로 실행 파일이 있으면 --payload-root .을 사용합니다. 생성 명령은 ZIP을 수정하지 않고 크기·SHA-256을 계산하며 기존 release.json은 덮어쓰지 않습니다. ZIP 변경 시 새 업로드 폴더에 JSON을 다시 생성하세요.
--environment prod|dev, --channel stable|beta|dev, --notes, --hero-path, --thumbnail-path도 지원합니다.

3. incoming/upload-001/에 Windows.zip.uploading과 release.json.uploading으로 올립니다. 전송 완료 후 각각 Windows.zip, release.json으로 이름을 바꿉니다.
4. 서비스가 두 파일을 감지해 검사합니다. 수동 접수는 아래와 같습니다.

```text
UeDtLauncher.DistributionServer ingest /srv/ue-dt-distribution/incoming/upload-001 --config /etc/ue-dt-distribution/server.json
UeDtLauncher.DistributionServer list --config /etc/ue-dt-distribution/server.json
UeDtLauncher.DistributionServer inspect <job-id> --config /etc/ue-dt-distribution/server.json
UeDtLauncher.DistributionServer approve <job-id> --config /etc/ue-dt-distribution/server.json
```

승인 전에는 공개되지 않습니다. 승인 시 디렉터리가 없으면 생성합니다. 동일 버전 덮어쓰기는 거부됩니다. ZIP에 포함된 파일은 서버에서 실행하지 않습니다.
승인 중 중단된 publishing 작업은 같은 approve 명령으로 재개합니다. failed 접수는 파일을 고친 후 retry <job-id>로 다시 대기 상태로 만듭니다.
원본은 비공개 snapshot으로 보관합니다. 신규 프로젝트는 게시 후 별도 접근 정책을 추가해야 다운로드할 수 있습니다.

현재 latest는 같은 환경·채널·OS에서 마지막 승인된 버전입니다. 과거 버전을 나중에 승인하면 최신 선택이 바뀔 수 있으므로 승인 순서를 확인하세요. 관리 명령은 서비스 계정 또는 허용된 운영자로 실행합니다.

[서버 구성 및 권한](distribution-workflow.md)을 함께 확인하세요.
