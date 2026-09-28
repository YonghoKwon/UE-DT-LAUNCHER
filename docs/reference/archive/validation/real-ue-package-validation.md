# 실제 MA0T10 UE 패키지 — 로컬 Windows 런처 시험

시험일: 2026-09-29. 합성 demo.exe가 아니라 사용자 지정 `C:\Unreal Projects\ma0t10_dt`의 UE5.3.2 Windows Development 패키지를 사용했다. **회사 운영 인수·Linux/Shipping 검증은 아니다.**

## 출처와 결과

| 항목 | 값/결과 |
|---|---|
| UE 작업 브랜치 | codex/launcher-package-validation, 시작148cc336 |
| UE 부분 커밋 | 760082c: Editor-only 테스트11파일 가드; e3d9d0c: 승인된 TestMap 참조 복구·점검 도구 |
| DTCore 체크아웃 | a1b333ef06fe920ef20b3e1373ab292da35e583f, 기존 parent gitlink 변경은 보존/미stage |
| 런처 작업 브랜치 | codex/real-ue-package-validation, 제품 바이너리는 기존8ce5060 게시본 |
| 빌드 | UE5.3.2 / Win64 Development / D3D12, 최종 Build/Cook/Stage/Archive 성공 |
| 초기 실패 | 에디터 헤더가 게임 타깃에 포함됨; DT_DxLevel이 참조한 TestMap의 구형 모니터 위젯 누락 |
| 에디터 회귀 | 관련29건:12정상+17경고 동반 성공, 실패0/미실행0. 경고는 격리 UserDir의 초기 SaveGame 파일 부재 |
| 콘텐츠 복구 | 사용자 승인 후 TestMap의 K2Node_CreateWidget_0만 현재 WBP_VirtualSensorMonitorPanel로 연결. 원본 백업·dry-run·재실행 무변경 확인 |
| 최종 쿠킹 범위 | 맵 제외 없이 정상 패키징. SensorTestMap만 지정한 중간 시도도 간접 TestMap 참조 때문에 실패했으며 최종 성공으로 세지 않음 |
| 파일·크기 | 328파일 / 878,409,923바이트, ZIP412,522,478바이트 |
| 등록 | Windows.zip + 외부 release.json → 비공개 검사 → pending → 명시적 approve → 서명 게시 |
| 릴리스 | ma0t10-dt / prod / stable / 0.1.0-ue-test.20260929 / windows-x64. prod/stable은 격리 시험 서버의 이름이지 회사 운영 배포가 아님 |
| GUI 설치 | 새 승인 후 ‘설치 후 실행’ 클릭. CLI 선설치 없음, Manifest328파일 전체 크기/SHA-256 일치 |
| 실제 실행 | ma0t10_dt bootstrap과 Binaries/Win64의 실제 UE 자식 실행, SensorTestMap 로드·D3D12/SM6 창·센서 모니터 프레임 증가 확인 |
| 첫 종료 | 외부 Alt-F4 입력에 의한 정상 종료 로그. 에이전트가 종료 버튼을 눌렀다고 기록하지 않음 |
| 재실행·수명 | 별도 승인 후 재실행. 런처 종료 전후 같은 bootstrap PID77112/자식PID51048·생성시각과 runtime 시도 유지. 재실행한 GUI도 Running 인식 |
| 정상 종료 | 에이전트가 관측한 UE 창에 Alt-F4. 프로세스 종료 후 supervisor-completed/Quiescent, F6 후 최신 상태·실행 가능 복귀 |
| 종료 후 무결성 | Manifest328파일 재검증 성공. 설치 폴더의 추가 런타임 로그는 별도 확인 |

화면 근거는 이 작업 대화의 native GUI 캡처에 있다. UE 창의 센서 모니터 프레임168→940을 관측했지만, 이는 정격Hz/FPS·센서 정확도·장면의 시각 품질 합격 판정이 아니다. 첫 런처 캡처가 UE 창에 가려졌던 결과와 두 번째 실행의 직접 UE 창 캡처를 구분한다.

## 해시와 보존

| 대상 | SHA-256 |
|---|---|
| ZIP | c22ff98a7bc4801bde8d4f5f8ff229662220cd3c13d90181ee1c00a1d44832a1 |
| UE bootstrap | ff88ad08643771005df2e19c151f07d6b0c964b0d26e2283d2535087cce82213 |
| UE 실제 게임 EXE | 6d23e7fb3e5930df714f5c8bdbb25062f4015a3b730561b225552ed7b2315647 |
| Launcher GUI/CLI | 2a809f4c6729aff6b60ab4e17c2380d196c4a18ad5069787e2bee9164670a4ab |
| Agent | a6c09dcced93b1b7ae22e876d0105844cd94a3456237fb5e68805f1133196549 |
| 배포 서버 | 75d9553cc872220fa9aabf9c705b8a3c3536072ad8eb3bb3e976d984f8d0019f |
| 사용자 SensorTestMap, 변경 전후 동일 | c00eedbc0b06cee415643a2a26d42df6bbad1c08380463f22218629b17c63587 |
| 사용자 Config/Game.ini, 변경 전후 동일 | 6d21be54bb1ba57b954b667cf26bcf2dc5f4c22cdce33f45e206a88cb5cde8d1 |

DTCore 소스/gitlink와 Samples/PixelStreaming은 직접 편집·stage하지 않았다. UE 빌드에는 PixelStreaming RuntimeDependencies를 Samples로 복사하는 엔진 동작이 포함된다. Samples 전체의 사전 해시는 수집하지 않았으므로 바이트 단위 불변을 주장하지 않는다. 기존 사용자 변경은 UE 저장소에 남겨 두었다.

## 통신·저장 격리와 발견 사항

- 읽기 전용 맵 점검은21 Actor와 센서의 LogOnly 출력을 확인했다. 맵을 저장하거나 BeginPlay하지 않았다.
- 런처 서버/Agent는 별도 디렉터리·PC키·서명키·loopback HTTP 요청 서명으로 실행했다. Windows 서비스 설치나 회사 서버 변경은 하지 않았다.
- UE에는 별도 `-UserDir`와 DTCore API/WebSocket의 소유한 loopback503 sink를 지정했다. 실제 로그의 ConnectWebSocket URL과 UserDir 적용을 확인했다. 이는 방화벽 수준의 전체 네트워크 격리나 실장비/Broker 연동 검증은 아니다.
- **보완 필요:** UE/DTCore가 `Logs/CustomLogs/DxLog_2026-09-29.log`를 설치 디렉터리에 추가 생성했다. 표준 사용자/Program Files 쓰기 권한·로그/세이브 위치 계약은 USER-01/OPS-02에서 별도 해결해야 한다. 이 파일을 임의 삭제하거나 DTCore를 수정하지 않았다.
- DTCore EnhancedInput 플러그인 의존 선언 경고는 보존했다. 테스트에서 새로 추가한 조건부 컴파일은 Editor 테스트를 없앤 것이 아니라 게임 빌드에서만 제외한 것이다.
- 실제 UE의 버전 업데이트·손상 복구·rollback·정전/강제 종료·회사 RHEL·Linux/Shipping·MSI 서비스 계정 시험은 아직 미실행이다.

## 재현 순서

격리된 새 경로에서만 사용한다. 아래 준비 도구는 UE를 자동 설치/실행하지 않으며, 기존 root를 덮어쓰지 않는다. ZIP은 기존 패키지 파일 전체를 읽어 생성하고 외부 release.json을 만들며 ZIP 안에 release.json을 추가하지 않는다.

```powershell
# UE 저장소에서: Editor/Live Coding 종료 후 실행
& 'C:/Program Files/Epic Games/UE_5.3/Engine/Build/BatchFiles/RunUAT.bat' BuildCookRun `
  '-project=C:/Unreal Projects/ma0t10_dt/ma0t10_dt.uproject' `
  -noP4 -platform=Win64 -clientconfig=Development -build -cook -stage -pak -iostore -archive `
  '-stagingdirectory=<새 stage 절대경로>' '-archivedirectory=<새 package 절대경로>' -unattended -utf8output

# 런처 저장소에서: 패키지·publish EXE 경로를 실제 값으로 지정
python tools/prepare_real_ue_fixture.py --root <새 시험 root> prepare `
  --package <package/Windows> --launcher <GUI/UeDtLauncher.exe> `
  --agent <Agent/UeDtLauncher.Agent.exe> --server <Server/UeDtLauncher.DistributionServer.exe> --approve
# 위 프로세스가 READY를 출력하면 별도 터미널에서 GUI만 연다.
python tools/prepare_real_ue_fixture.py --root <시험 root> open-general
# 적용 직전 대상 확인 후 실제 GUI에서 설치/실행.
python tools/prepare_real_ue_fixture.py --root <시험 root> verify
python tools/prepare_real_ue_fixture.py --root <시험 root> status
# UE 정상 종료/Quiescent 확인 후 시험용 서버·Agent만 종료
python tools/prepare_real_ue_fixture.py --root <시험 root> stop-services
```

이번 로컬 산출물은 `publish/real-ue/20260929-01/package/Windows` 및 `fixture/server/incoming/ue-001/{Windows.zip,release.json}`에 있다. private키·로그·설치본·ZIP은 Git에서 제외한다. 도구 preflight6건 통과; 초기 실행 후 같은 basename의 향후 fixture 충돌을 막는 절대경로 기반 endpoint 함수를 추가하고 단위 검사했다. 이번 이미 실행 중인 fixture는 생성 당시 endpoint를 그대로 사용한다.

OPS-01은 로컬 Windows 실제 UE 증거를 확보한50%로 기록한다. 회사 TLS/CA·IP·Linux/RHEL·실제 UE 업데이트/복구와 운영 gate는 여전히 미완료이며, 다른 항목을 자동 완료하지 않는다.
