> 검증 이력 보관: 실제 실행 날짜·환경·결과를 보존합니다. 아카이브 이동은 증거 폐기를 뜻하지 않습니다. 현재 진행률은 [개선 대장](../../../../IMPROVEMENTS.md), 현재 사용 절차는 [문서 색인](../../README.md)을 따릅니다.

# 성능 개선 설정·검증 기록

2026-09-27~28 / Windows + WSL Ubuntu(.NET 8), 브랜치 `codex/launcher-performance`, 변경 전 `39ca4d6`. 실제 UE/RHEL/회사 서버 시험이 아닌 재현 데이터 결과입니다. PERF-03의 일부 지연 기준은 미달이며 이 문서는 운영 승인서가 아닙니다.

## 설정과 호환성

클라이언트의 전체 설정에 선택적으로 추가합니다. 관리형에서는 GUI 표시 설정이 아니라 **Agent 보호 설정**이 적용됩니다.

```json
{
  "performance": {
    "downloadConcurrency": 2,
    "hashConcurrency": 2,
    "reusePreviousInstallations": true
  }
}
```

- 다운로드 1~8, 해시 1~4. 생략하면 각각 2이며 1은 순차 비교 모드입니다. 범위 밖 값은 거부합니다.
- 검증과 다운로드만 병렬화하고 실제 적용·rollback은 직렬 유지합니다. 실패하면 형제 작업을 취소하고 모두 종료한 뒤 반환합니다.
- 재사용은 인증된 Catalog/Manifest의 **새 버전 최초 설치**에만 적용합니다. 같은 프로젝트/환경/채널/OS의 최근 정상 설치 3개, 같은 경로·크기·해시가 후보입니다.
- 로컬 기록은 힌트이며 복사 결과를 대상 서명 Manifest의 SHA-256으로 검증합니다. 원본은 변경하지 않고 손상·누락·잠금은 다운로드로 전환합니다. hard link·공용 캐시는 없습니다.
- 같은 트랙의 기록이 256개를 넘으면 탐색 비용을 제한하기 위해 재사용 없이 다운로드합니다. repair와 기존 대상 설치에는 재사용을 적용하지 않습니다.
- 재사용은 파일 단위입니다. 큰 .pak 등의 해시가 바뀌면 그 파일 전체 다운로드가 필요합니다. 합성 데이터의90% 절감이 실제 UE 패키지에서도 보장되는 것은 아닙니다.
- IPC v1과 기존 3필드 진행 이벤트를 유지하고 선택적 바이트·성능 필드를 추가했습니다. GUI 속도는 실제 네트워크 바이트, 진행률은 재사용을 포함한 논리 완료량입니다. 일반 화면에 원시 측정 JSON을 표시하지 않습니다.

서버 설정의 선택 필드:

```json
{"intakeWorkers": 1}
```

1 또는 2만 허용하며 기본1을 유지합니다. 큐 크기는 worker 수의 2배이며 초과 건은 공정한 순환 순서로 다음 스캔에 처리합니다. 자동 승인은 추가하지 않았습니다.

서버 DB schema는 v2입니다. **기존 서버/관리 CLI를 중지하고 외부 백업을 확보한 뒤 교체**합니다. 최초 새 프로세스는 이전 DB의 일관된 SQLite 백업을 `distribution.pre-v2-<UTC>.db`에 만든 후 transaction으로 migration합니다. 이전/새 바이너리를 같은 DB에 동시에 사용하지 않습니다. journal mode는 변경하지 않았습니다. 롤백이 필요하면 모든 프로세스를 멈추고 이전 바이너리와 대응 백업을 함께 복원하며 catalog sequence 역행도 운영 가이드에 따라 점검합니다.

`inspect`에 phase, processedBytes, totalBytes, estimatedAdditionalDiskBytes, progressUpdatedAt이 추가됩니다. 바이트 갱신은 최대 초당1회, 단계 변경은 즉시 기록합니다. 예상 추가 공간은 복사/해제 시점별 필요량 +64MiB 여유이며 **공간 예약이나 성공 보장 아님**입니다. 원본 ZIP·snapshot·기존 해제본·게시 준비본이 함께 있으면 전체 보관량은 이 값보다 큽니다. 압축 ZIP C/해제 U 기준 신규 게시의 보수적 피크는 약 2C+2U와 기존 데이터·오버헤드를 함께 고려합니다.

작업 OS 잠금과 active_work 기록이 살아 있는 임시 폴더를 cleanup에서 보호합니다. crash 뒤에는 OS 잠금 확보 후 미완료 건을 복구하고, 명백한 동일 버전 충돌의 패배 작업은 failed로 기록합니다. 공개 버전 삭제·덮어쓰기는 없습니다.

API는 매 요청 토큰/실제 IP를 검사하고 정책 파일 원문을 다시 읽습니다. 동일 내용의 파싱/CIDR 결과만 재사용하며 삭제·오류 시 이전 정상 권한으로 대체하지 않습니다. 정책 원문은 최대4MiB, 불변 이미지 메타데이터 캐시는 최대256항목/32MiB입니다. Catalog의 sequence·시각·서명은 매번 새로 생성합니다. 짧은 SQL 읽기/쓰기 조정 중 파일 I/O나 서명을 수행하지 않습니다.

## 측정 방법

- 작은 파일: 1,000×4KiB. 큰 파일: 10×8MiB. 혼합: 900×4KiB + 10×4MiB.
- 결정적 두 버전에서 바이트 기준90%를 동일하게 유지. 최초 설치/변경 없음/다음 버전/손상 후 repair를 측정.
- 준비1회 + 측정3회 중앙값. 각 반복은 새 설치·상태 경로, OS 캐시는 강제 비우지 않음. 같은 Windows 머신·self-contained 다중 파일 publish 형식 사용.
- 클라이언트 비교는 loopback HTTP 테스트 프록시에 파일 요청당5ms 지연을 추가. 서명과 Bearer는 유지하며 이 HTTP 예외를 운영 설정에 복사하지 않음.
- 프로세스 CPU/RSS는20ms 샘플. 클라이언트 wall time에는 프로세스 시작/설정/metadata도 포함.
- `[Performance]`의 hash/copy 시간은 worker 작업 시간 합계, download는 복사·재시도·검증을 포함한 staging 경과 시간, apply는 commit 경과 시간. **서로 더해서 전체 시간으로 쓰지 않음**.
- networkBytes는 콘텐츠 응답 본문(실패·재시도 포함), reusedBytes는 검증에 성공한 복사만 집계. metadata·TLS 오버헤드는 제외.
- 기존 바이너리의 단계 시간은 로그 경계 추정이며, 정확한 이전 copy/DB 내부 대기 시간은 알 수 없으므로 null로 기록. null을0으로 표시하지 않음.

## 클라이언트 결과

단위 초, 측정3회 중앙값. 아래 모든 구성의 다음 버전 콘텐츠 수신량은90% 감소했습니다.

| 데이터 | 최초 설치 전→후 | 변경 없음 전→후 | 다음 버전 전→후 | repair 전→후 |
|---|---:|---:|---:|---:|
| 작은 파일 | 10.96→5.98 | 0.64→0.68 | 11.01→3.29 | 12.43→6.99 |
| 큰 파일 | 1.19→0.93 | 0.54→0.50 | 1.07→0.64 | 0.87→0.85 |
| 혼합 | 10.35→5.49 | 0.66→0.68 | 10.18→3.04 | 10.90→6.62 |

변경 없는 점검의 작은/혼합 데이터는 약6%/3% 증가했고10% 기준 이내였습니다. 모든 상황이 빨라졌다고 해석하지 않습니다. 기본 다운로드2/해시2를 유지합니다.

## 서버 결과와 미달 항목

최종 API는 직접 loopback HTTP, 연결당20회 Catalog/Range 교대 요청입니다. 동일 bench 권한으로1/10/30개 연결을 사용했으며, 서로 다른30대 PC·30개 정책 조합의 측정은 아닙니다. 준비1회 제외 후3회 p95의 중앙값이며 실패도 지연 표본에 포함합니다.

| 동시 연결 | 변경 전 혼합 p95(ms) | 최종 혼합 p95(ms) | 측정 요청 실패 전→후 |
|---:|---:|---:|---:|
| 1 | 4.51 | 4.34 | 0→0 /60건 |
| 10 | 5.43 | 24.66 | 2→0 /600건 |
| 30 | 833.44 | 81.23 | 32→0 /1,800건 |

기존 SQLite RETURNING 완료 시 busy 실패를 재현했고, 짧은 명시 transaction과 서버 내부 SQL 읽기/쓰기 조정으로 해결했습니다. 중간 후보는10개 연결 p95가 약221ms였으며 최종24.66ms로 줄었지만 **변경 전보다10% 이상 악화**된 상태입니다. 따라서 PERF-03의 지연 합격/기본 운영 채택은 보류합니다. 오류를 숨기거나 서명·freshness를 완화해 이전 속도로 되돌리지 않습니다. 권한/무결성 개선 구현과 성능 기준 통과를 구분합니다.

Catalog만 측정하는 테스트 프록시 결과와 위 직접 Catalog/Range 혼합 결과는 합쳐서 비교하지 않습니다. 원시 결과의 성공 전용 지연·상태 코드·중복 sequence도 확인합니다.

### 접수와 API 동시 부하

80MiB ZIP 4건, 기존 승인 seed 1개, 10개 연결의 Catalog/Range 요청을 함께 실행했습니다. 준비1회 후 worker1/2 순서를 번갈아 측정3회씩 수행했습니다. 전송 완료 rename부터4건 pending까지의 시간으로, watcher의 최대2초 스캔 간격도 포함합니다.

| worker | 접수 중앙값(ms) | CPU 초(중앙값) | 최대 샘플 RSS(MiB) | Catalog p95(ms) | Range p95(ms) | HTTP 실패 |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 5,019.1 | 6.297 | 187.3 | 149.8 | 16.9 | 0 |
| 2 | 2,594.5 | 3.984 | 133.5 | 32.0 | 27.8 | 0 |

API만 SQL을 조정한 중간 후보에서는 background 접수 쓰기와 경합하여 worker1이 중앙값35초까지 지연됐습니다. 같은 root의 짧은 SQL 구간을 공유 조정한 후 위 결과가 나왔습니다. worker2에서는 Range p95가 오히려 늘어났으므로 무조건2를 권장하지 않으며 기본1을 유지합니다. 서버 데이터 볼륨·PC 수가 달라지면 다시 측정해야 합니다.

## 실제 실행과 자동화

- Windows 전체293/293, Linux 전체293/293; Release build/publish 경고·오류0. Python 도구 계약16/16.
- 중간 단계도 독립 staged 소스로 시험: 서버 조회 단계267/267, 진행/공간 단계271/271. 관련 파일만 부분 커밋.
- Linux 최초 시험(당시286개)의 외부 artifacts 경로 때문에 테스트6개가 저장소를 찾지 못했습니다. 저장소 아래 격리 artifacts 경로로286개 재통과 후, 추가 회귀를 포함한 최종293개도 통과했습니다. 경로 수정과 제품 결함 수정을 혼동하지 않습니다.
- 신규 회귀: 병렬 상한·취소/형제 종료·Range·해시/재시도·논리 진행, 재사용 원본 독립·손상 fallback·트랙 경계, 정책 교체/삭제·토큰 폐기·동시 Catalog, 작업별 실제 자식 프로세스 잠금·강제 종료 복구·cleanup 경합·공정 큐·중복 승인 충돌.
- 이전 install-state/manifest의 null·잘못된 JSON·용량 초과는 설치 실패 대신 네트워크 다운로드로 전환합니다. InvalidDataException이 IOException 하위가 아닌 경계를 수정했고, 6개 회귀 테스트와 publish CLI의 null 기록 시험(80MiB 정상 다운로드)을 확인했습니다. 대상 서명 검증 실패는 여전히 중단합니다.
- Windows 개발자 GUI에서1.0.0 설치, 네트워크 실패 후 다시 시도, 실행 확인. 일반 GUI에서2.0.0 설치/실행·최신 상태 확인. 서비스 종료 후 일반 문구 `업데이트 서비스 연결 필요`도 확인.
- GUI/Agent 측정:1.0.0 콘텐츠10,584,064 bytes 수신;2.0.0은1,048,576 bytes 수신 +9,535,488 bytes 재사용. 두 버전 상태·PID 기록과 Windows Agent repair 완료 확인.
- Windows named pipe와 Linux Unix socket의 실제 Agent status 성공, 기존 단일 응답과 선택적 진행 필드 호환 테스트 통과.
- Linux publish CLI의 HTTPS 설치·두 버전 실행·손상 repair·IP/위조 헤더/토큰 폐기·Range E2E 통과. CI에 같은 기능 E2E gate를 추가했지만 원격 CI 실행은 아직 수행하지 않음.
- 최종 Linux 코드로 같은 HTTPS E2E를 다시 통과했습니다. 이 호스트는 설치하지 않고 풀어 둔 nginx 바이너리를 사용해 최초 기본 `/var/log/nginx/error.log` 경고가 있었으나, 테스트 설정의 별도 로그 경로와 nginx 설정 검사는 정상입니다. 빌드 경고와 런타임 환경 메시지는 구분합니다. 사용한 GUI·Agent·서버·프록시 프로세스는 정리했습니다.

### WSL1 nginx 큰 응답 제한

이 환경의 nginx1.18/WSL1에서1MiB 응답이 부분 전송 후 upstream timeout으로 중단됐습니다. Windows/Linux curl의 배포 API 직접 요청은1MiB 전체를 수 ms에 받았지만 nginx HTTP/HTTPS 경유는16~80KiB에서 멈췄습니다. HTTP1.1/Connection 헤더/버퍼링 설정 시험으로 해결되지 않았고, select/poll 모듈은 해당 테스트 바이너리에 없습니다. 이것만으로 특정 커널 결함 원인을 확정하지 않습니다.

프로덕션 런처·파일 응답·nginx 템플릿을 추측으로 변경하지 않았습니다. 임시 nginx 설정은 원복했고 격리된 Windows HTTPS 테스트 프록시를 사용해 큰 파일 GUI 흐름을 별도 검증했습니다. TLS 인증서·호스트 검증, 서버 Bearer/IP/서명 검사는 유지했습니다. 이 성공은 RHEL nginx 대용량 전송 성공을 뜻하지 않습니다. 실제 UE/RHEL 및 별도 Linux 커널에서의 큰 파일 프록시 시험이 남습니다.

## 재현 명령

Python3.11+와 psutil이 필요합니다. publish 출력 경로는 자신의 빌드에 맞춥니다. output은 존재하지 않는 새 경로만 허용하며 raw 데이터·로그·키·DB는 커밋하지 않습니다.

```text
python -m unittest discover -s tools -p "test_benchmark*.py"
python tools/benchmark-launcher-performance.py --launcher publish/client/UeDtLauncher.exe --server publish/server/UeDtLauncher.DistributionServer.exe --output publish/perf-run
python tools/benchmark-distribution-api.py --server publish/server/UeDtLauncher.DistributionServer.exe --config publish/perf-run/server.json --output publish/perf-api
python tools/benchmark-intake-performance.py --server publish/server/UeDtLauncher.DistributionServer.exe --launcher publish/client/UeDtLauncher.exe --fixture publish/perf-run/server/incoming/large-1.0.0 --output publish/perf-intake
```

비교용 클라이언트 실행은 `--download-concurrency 1 --hash-concurrency 1 --no-reuse`를 추가합니다. `--corrupt-source-record`는 이 새 테스트 루트의 이전 기록만 null로 바꿔 다운로드 fallback을 확인하는 결함 주입 옵션입니다. 각 실행은 고유한 테스트 서버 루트를 만들고 자신이 시작한 프로세스만 종료합니다. 동일 output으로 재실행하지 않습니다.

세부 결과는 로컬 `publish/performance/{baseline-controlled,candidate-controlled,baseline-api,candidate-api-v4,intake-v4}/results.json` 및 results.md에 있습니다. 초기 탐색 실행은 동시 빌드·측정 프록시 연결 소진 영향을 받아 최종 클라이언트 비교에서 제외했습니다. 최종 비교는 upstream 연결을 재사용하는 같은 도구와 조용한 빌드 조건으로 순차 실행했습니다.

`tools/prepare-performance-windows-gui.py`는 유지 중인 Linux HTTPS E2E fixture에 Windows 테스트 릴리스와 격리된 Agent/GUI 설정을 준비합니다. `tools/run-loopback-test-proxy.py`는 위 환경 제약을 분리하는 **테스트 전용** 대체 경로이며 회사 배포용이 아닙니다. 개발자·일반 GUI 화면은 로컬 `publish/performance/gui-evidence/`에 보관합니다. 실제 회사 데이터나 개인키를 이 경로에 넣지 않습니다.
