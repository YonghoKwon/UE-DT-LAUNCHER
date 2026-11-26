# PROD-04B Docker SSH 업로드 상태 검증

[로컬] Windows Docker Desktop과 Rocky Linux 8.4, 실행 p04b0625fb6e84d1. 재검증 게시본 소스는 6cf598c510a25f60bc98493a94f869636c0f5a6f이며, 게시 당시 코드 트리는 origin/main 0b34e419ae1195a817b5550fe54adaf30bed5c35와 동일했습니다. 이 기준에는 01D(#35)와 03I(#39)가 포함됩니다.

## 게이트

| 게이트 | 결과 | 관측 |
|---|---|---|
| G1 | N/A | 검증 스크립트와 증거만 추가합니다. 1차 후보의 전체 테스트 1,552건 통과(실패·skip 0)는 추가 증거입니다. |
| G2 | PASS | 같은 ID에서 상태 전이 5/5. ZIP 582,163,251 B, 전송 8.938초, 최종 두 파일 해시 일치. |
| G3 | PASS | 전송 후 --wait가 3.328초에 pending으로 종료 0. |
| G4 | PASS | 깨진 ZIP failed와 수동 거절 rejected의 고정 한국어 사유 2/2, 내부 필드 노출 0. |
| G5 | PASS | CLI 0–4, 파일 미생성 SSH 44, 연속 SSH 실패 3회 뒤 CLI 1. 제한 4회 조회 후 5.187초. |
| G6 | PASS | SSH 실패 1회 뒤 같은 상태. pipefail 적용 전후 수집 종료 0, 서버 파일 740개와 상태 파일 5개의 집계 SHA-256 일치. |
| G7 | PASS | SSH 10건 거부, jail UID 거부 10건, 서버 원본에서 UID 20002의 EACCES 10건. 상태 읽기·incoming 쓰기 성공, sshd 마운트 2개. |
| G8 | PASS | 컨테이너·네트워크·볼륨·이미지 목록 차이와 실행 라벨 잔여 0. BuildKit 캐시 285개·16.23GB → 295개·16.72GB. |

## 같은 업로드 ID의 상태

업로드 ID p04b0625fb6e84d1-primary. 전송 상태를 관측한 후보로 승인·승격·후속 승격 확인을 모두 진행합니다.

| 단계 | 상태 | 조회 UTC |
|---|---|---|
| 전송 중 | transferring | 2026-11-26T17:32:58.2876559+00:00 |
| 승인 대기 | pending | 2026-11-26T17:33:04.6512291+00:00 |
| 승인 후 | published | 2026-11-26T17:33:14.0936568+00:00 |
| 승격 후 | recommended | 2026-11-26T17:33:17.9955497+00:00 |
| 다른 판 승격 후 | published | 2026-11-26T17:33:30.5535589+00:00 |

## 업로더 권한

SSH 세션은 chroot의 강제 명령에서 거부됐습니다. 별도 UID 검사에서 SSH jail의 마운트 격리를 확인했습니다. dist에서는 root로 보호 대상의 존재를 먼저 확인하고, 추가 그룹이 없는 UID/GID 20002로 원본을 직접 열었습니다. 원본 접근의 거부 판정은 EACCES를 요구합니다.

| 동작 | SSH 종료 | jail UID 결과 | dist 원본 UID 결과 |
|---|---:|---|---|
| authority | 126 | ENOENT | EACCES |
| releases | 126 | ENOENT | EACCES |
| processing | 126 | ENOENT | EACCES |
| archive | 126 | ENOENT | EACCES |
| config | 126 | ENOENT | EACCES |
| keys | 126 | ENOENT | EACCES |
| status-overwrite | 126 | EACCES | EACCES |
| status-create | 126 | EROFS | EACCES |
| status-delete | 126 | EROFS | EACCES |
| store-create | 126 | EACCES | EACCES |

원본 store 볼륨은 dist에서 rw입니다. sshd에는 incoming rw와 upload-status ro만 마운트됐습니다. 업로더는 SSH로 자신의 상태 파일을 읽고 자신의 incoming 폴더에 쓸 수 있었으며, dist에서 같은 UID로 시도한 상태 파일 읽기도 성공했습니다.

## 재현

Windows Python 3.12와 psutil, .NET SDK, OpenSSH, Docker Desktop이 필요합니다. 01D의 격리된 포트와 Compose 구성, ma0t10의 읽기 전용 원본 패키지를 사용합니다. PowerShell 래퍼는 지정 소스의 Windows·Linux 게시본 생성부터 Docker 정리까지 실행합니다. ELF 검사 입력은 multi-file 게시로 만들며 실제 서버는 single-file 게시본을 씁니다.

**저장소 루트에서 실행**

```powershell
& .\tools\docker\prod-server\acceptance\reproduce-04b.ps1 -Repo . -Private .\artifacts\prod-04b-repro-new -UePackage '<ma0t10 Windows 패키지>' -Output .\artifacts\prod-04b-evidence -SourceRef HEAD
```

## 회사 인수

회사 RHEL의 SELinux·방화벽은 T2, 실제 업로드 계정·키·incoming 권한은 T7에 남아 있습니다.

[PowerShell 재현](../../../../tools/docker/prod-server/acceptance/reproduce-04b.ps1) · [Python 재현](../../../../tools/docker/prod-server/acceptance/reproduce-04b.py) · [정제 증거](prod-04b-ssh-status-evidence.json)
