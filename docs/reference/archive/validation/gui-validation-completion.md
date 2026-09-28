# 새 GUI 실제 수용 검증 후속

2026-09-28 / 기준 f6d5174 / codex/gui-validation-completion.

## 1. 재현 도구

- 동일 소스에서 Windows GUI/Agent/서버/합성 앱 publish. 바이너리별 SHA-256과 기준 HEAD를 fixture.json에 기록하고 제어 시 재검증한다.
- --prepare-gui --defer-v2는 v1만 승인하고 v2 job을 승인 대기로 보존한다. approve-v2는 기존 서버 승인 CLI를 호출한다.
- 지정 attempt 자연 종료, 설치/정상 백업 전체 hash 확인, preview fingerprint 변경 도구와 경로/해시 단위 검사 추가.
- 최초 readiness 시험은 CLI status를 JSON으로 오해해 실패했다. 실제 IPC status/capability 응답 검사로 수정했다.
- intake-proof-02: published Agent 준비/이미지/doctor, v2 승인 대기→수동 승인, 합성 부모 선종료/자식 유지/자연 종료와 미설치 보존 통과. 도구 단위 1개 통과. GUI 설치 버튼 시험은 아직 하지 않았다.

## 2. 상태·복원 대상

- IPC v1의 선택적 PreviousInstallation은 인증된 같은 track의 bounded 설치 기록만 표시한다. 선택 버전의 기존 필드 의미는 유지하며 늦은 응답은 현재 선택과 대조한다.
- portable rollback도 정확한 버전 경로와 preview fingerprint를 기존 설치 lease에서 확인한다. 취소 전에는 작업 시작 표시를 변경하지 않는다. Catalog typed 오류·지원 ID와 재시도 버튼을 보존한다.
- 과대 metadata의 InvalidDataException 누락을 회귀에서 발견해 수정. 신규 이전 설치/기존 오류·복원 관련 14개 통과, 기존 전체 428개 통과(신규 테스트 추가 전). Windows publish GUI 시작·서비스 단절 안내, 실제 console Agent broker/preview/변조 거부/복원 PASS (`uedt-broker-proof-i6cjafyk`). GUI 복원 적용 증거는 아직 아니다.

## 남은 수용

실제 GUI 적용 직전 승인과 사용자 협업 OS 변경이 필요하다. 실제 내레이터 음성은 사용자 선택으로 후속 보류한다. 단계별 결과를 추가하며 코드·자동화·GUI·실제 OS 결과를 서로 대신하지 않는다.

## 3. 접근성 표시 상태

- 알림 컨트롤을 재사용하고 단계/완료/실패 의미가 바뀔 때만 Text를 바꾼다. 오류 제목을 먼저 확정하며 byte tick은 같은 알림을 반복하지 않는다. 기본 Avalonia peer 경로를 사용한다.
- 1100 DIP 경계를 실제로 넘는 검색/포커스 회귀와 peer 이름 이벤트/재구성 보존 시험 통과. 관련 화면 테스트 27개, Windows 전체 437개 통과, Release 경고/오류 0.
- 열린 dialog 고대비 갱신, 안전한 focus fallback, 도움말 ID, 작업 상세 영역의 제한된 스크롤, 비민감 UiDisplay 로컬 진단 추가.
- 사용자 설정 변경 후 baseline-01 실제 일반 GUI에서 screenPixels 1920×1080, workingPixels 1920×1032, RenderScaling=1, clientDip=1120×740, textScale=1, highContrast=false 관측. v1만 공개된 미설치 화면 정상. 설치 적용 승인 대기.

## 4. 실제 GUI 적용 (baseline-01, 1920×1080/100%)

- 사용자 실행 직전 승인 후 일반 v1 설치/실행 성공. 3개 파일 해시 일치, Running. GUI 종료/재실행 중 동일 runtime attempt와 합성 자식 유지, 지정 marker를 통한 자연 종료 후 Quiescent 확인.
- v2 수동 승인 후 재시작한 GUI에서 기존 v1/최신 v2/업데이트 후 실행 관측. 사용자 승인 후 v2 설치/실행, 3개 해시 정상, v1 설치/state 전체 snapshot 불변.
- 개발자 v1→v2 선택, v2 실행 중 update/repair/rollback disabled와 보호 snapshot 불변. 자연 종료 후 version.txt 손상→승인받은 GUI repair→3개 파일 정상.
- 정상 파일 상태에서 추가 GUI repair를 승인받아 정상 최신 백업 20260928121628 생성, 백업 3개 전체 해시 확인. 재손상 후 preview의 현재/복원 버전 2.0.0 확인, Escape 취소 시 snapshot 불변.
- 사용자 승인 후 정상 backup 실제 복원 성공. 제목/알림 모두 백업 복원 완료, 3개 해시 정상, v1 불변.
- preview를 연 뒤 metadata 공백을 바꾸고 승인받아 적용 시도: backup-preview-changed 거부, 변경 직후 snapshot 대비 설치/state/backup 불변. 다시 시도는 새 확인창을 요구했고 Escape로 취소.
- baseline-01은 c5a9d71에 포함된 UI 소스의 커밋 전 publish(Assembly 기준 3df63ed), 실행 파일 hash는 fixture.json에 기록됨. 후속 결함 수정본 성공으로 확대하지 않는다.

### 실제 시험 중 추가 발견

- 상태 확인이 Catalog를 다시 읽지 않아 새 배포가 재시작 전 반영되지 않음: 새 Catalog→설치 상태 조회로 수정.
- RuntimeBlocked에도 실행 의도 재시도 버튼이 보임: 숨기고 handler에서도 조회만 허용하도록 수정, 회귀 통과.
- 손상 파일이 있어도 개발자 응답이 files valid라고 표시됨: 누락/변경 개수와 metadata 서명 결과를 분리.
- 미설치 read-retry-01 단절 시험에서 streaming 작업의 30분 timeout이 연결 대기에도 적용됨. 연결 3초 제한을 분리하고 외부 취소 의미를 보존. 관련 9개 통과. 기존 시험은 재연결 뒤 설치 폴더/실행 marker 0건이지만 오류 재시도 성공 증거는 아님.
- Portable preview의 내부 동기 metadata 읽기는 UI 스레드 밖으로 이동. 실제 portable GUI 적용은 별도 시험 대기.
- 사용자 preferences snapshot/본인이 쓴 값 hash 확인 후 복원 도구 추가, 중간 사용자 변경 거부 회귀 통과. 최초 baseline의 설정 원본도 변경 전 확보했다.

### 최신 수정본 회귀

- read-retry-02에서 단절 오류 전환은 수정됐지만 Catalog 재시도 후 이전 오류 문구가 남는 것을 실제로 관측했다. 재시도 성공 시 설치 상태 조회까지 수행하되 update/launch는 호출하지 않도록 수정했다.
- read-retry-03(step4c 게시본): Agent 중단→3초 연결 제한 오류→Agent 재시작→GUI 다시 시도→설치 필요/설치 후 실행 화면 복귀를 직접 통과. apps 폴더 없음, 합성 실행 marker 0개 유지.
- 고대비 해제 시 열린 dialog의 primary 색/글자 복원도 보강했고 headless 회귀로 검사했다. 실제 OS 고대비 전환은 아직 별도 대기.
- 현재 Windows/WSL 전체 자동화 각각 441개, skip 0. Windows Release 경고 0/오류 0. Python fixture 경로/해시·사용자 설정 복원 충돌 검사 2개 통과.
- WSL HTTP 요청 서명+Agent E2E `/tmp/uedt-intranet-lh82jaag`, HTTPS/Bearer+nginx `/tmp/uedt-distribution-e2e.Z4R33C` 통과(연결 제한 보강 전). 최신 publish로 추가 재시험 결과를 별도로 기록한다.
- 현재 실제 OS 조합은 1920×1080/100%의 일반·개발자 2개이며, 나머지 16개 조합·최악 조건·portable GUI 실제 적용·개발자 실행 확인 승인 등은 완료로 표시하지 않는다. UI-01/02/03은 75% 유지한다.
