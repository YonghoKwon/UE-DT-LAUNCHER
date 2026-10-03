# 클라이언트 에디션 분리 검증

[정제 실행 근거](client-editions-evidence.json). GUI의 환경 제약은 미실행으로 보존하며 과거 CLI 설치 결과를 실제 클릭 증거로 사용하지 않는다.

## 데스크톱 연결 재개 시도

### 추가 재연결: 관리형 실제 설치·업데이트 통과

같은 `publish/editions/final` 게시본과 `accept-managed` fixture에서 실제 버튼으로 v1 설치·실행, v2 업데이트·실행을 수행했다. 각 설치의 Manifest 파일3개 해시가 일치하며 v2 작업 전후 v1 payload/state 전체 보호 snapshot이 동일했다. v1/v2 모두 런처 창을 닫아도 실제 자식 marker가 살아 있고 Running 상태가 유지됐다. 시험 도구의 정확한 실행 marker에 자연 종료를 요청한 뒤 ended와 Quiescent를 확인했다. GUI 설치를 CLI 선설치로 대신하지 않았다.

동일 게시본의 Developer 창에서 exact v1/v2 선택, v2 설치 상태, demo/prod/stable/2.0.0/windows-x64 실행 확인창을 실제 마우스로 확인했다. 취소 후 v2 payload/state snapshot 불변과 새 실행 marker0건을 대조했다. 개발자 실행 승인·복구·복원과 portable 전체 흐름은 이번 시험으로 완료하지 않았다.

**발견한 표시 결함:** 완료된 실행 작업에서도 `cancel-operation`(작업 취소) 버튼이 남았다. 실행·파일 무결성 시험 통과와 UI 결함을 구분하며, 수정 후 해당 흐름을 새 게시본에서 다시 검증해야 한다. 실제 관측1440×900·RenderScaling1은1920×1080 수용으로 계산하지 않는다. 회사 인수 및 USER-02/UI-02 전체 진행률은 상향하지 않는다.

사용자 재연결 후 세션 Active와 최신 accept-managed 창을 확인했다. 추천 미지정/주 버튼 비활성, service 단절 후 상태 확인 클릭, 친화적인 오류·지원 ID·재시도 표시, service 재연결 후 다시 시도 클릭→오류가 정리된 추천 대기 복귀를 확인했다. 설치 앱/marker 생성0건과 미설치 보호 snapshot 불변을 대조했다. 실제 성공한 마우스 동작은2건이다.

v1을 관리자 promote한 뒤 상태 확인을 진행하려는 중 세션이 다시 Disc로 바뀌었다. GetCursorPos0x80070005, CreateForMonitor0x80070057로 입력/캡처가 차단돼 설치/실행/복구/복원은 미실행이다. 사용자는 시험 동안 원격 데스크톱 창을 연결·잠금 해제 상태로 유지해야 한다. 실제 관측 화면은1440×900·RenderScaling1이며1920×1080 수용으로 기록하지 않는다. 현재 서버/Agent와 fixture는 이어서 사용할 수 있도록 보존했다.

2026-10-03, 기준 eea1dd3, codex/client-editions-ux-acceptance. 일반/개발자 GUI는 컴파일된 에디션으로 결정하고 기존 CLI는 공통으로 유지한다.

## 초기 빌드 확인

General/Developer의 obj/bin·assembly를 분리하고 Core의 Developer friend assembly와 동적 브랜드 resource URI를 적용했다. step1b의 두 게시 EXE `--build-info`에서 General/Developer를 확인했고 관련10개 회귀 통과. 반대 설정·재로드·메모리 profile 변경으로 capability가 바뀌지 않는다. 기존 self-update는 새 바이너리를 자동 교체하지 않으며 설정/pending을 보존한다.

새 managed fixture의 실제 일반 창에서 업데이트 서비스 정상·미설치·설치 후 실행을 읽었고 별도 개발자 창 제목을 확인했다. 직접 마우스 클릭은 GetCursorPos 0x80070005로 거부됐다. 실제 설치/실행은 미실행이며 도구 권한 오류를 제품 실패로 처리하지 않는다. 사용자에게 로그인된 데스크톱 접근을 요청한 상태이며 독립 구현·검증은 계속 진행한다.

## 설치본

General MSI는 일반 EXE/Agent, Developer MSI는 일반/개발자 EXE/Agent를 포함하며 비설치 CAB 추출 SHA-256 검증을 통과했다. 같은 버전 제품 중복은 Upgrade table의 inclusive 범위와 Launch condition으로 차단하며 제품 계열 UpgradeCode는 공통이다. 실제 install/upgrade/repair/uninstall은 수행하지 않았다.

Ubuntu WSL에서 base/developer RPM을 생성하고 newc 안전 검사·payload hash·권한/config(noreplace)·정확한 base version dependency를 확인했다. developer addon에는 Agent/service/config를 중복 소유하지 않는다. Windows/Linux CI와 서명/추출/산출물 목록을 에디션에 맞게 연결했으며 원격 CI/회사 인증서는 미검증이다.

## 사용자 작업 조정

## 성능 재확인

같은80MiB large 데이터·동일 도구/Windows 호스트·준비1/측정3·1/10/30 연결로 기존 closure/final-04와 에디션 후보를 비교했다. API p95 약8.1/11.5/30.2→7.9/11.4/30.5ms, 실패0·다음 버전 콘텐츠90% 절감 유지. 이번 단일 연결 악화는 재현되지 않았고 서버 병목 코드는 변경하지 않았다. 과거 전체 작은/혼합 시나리오·측정 순서/부하·회사 proxy 인수를 대신하지 않으므로 PERF-03은75%를 유지한다.

취소 결과에 operation record를 보존하고, 같은 릴리스/Manifest의 GUI 재개 버튼을 추가했다. 새 권한 확인과 digest 일치가 필요하며 재개는 자동 실행하지 않는다. 서버도 요청한 선택과 이전 operation의 선택이 다르면 거부한다. 실행 중 차단/선택 변경/완료 후 stale resume를 보호한다.

관련15개 및 추가 resume 결속 회귀 통과. Windows/WSL 전체667개 통과. 게시 CLI·console Agent·서버의 headless3개, 일반/개발자 CLI readiness8개와 WSL HTTPS/Bearer를 확인했다. 개발자 CLI 시험은 일반 runtime-host가 있는 공유 설치 구조에서 수행한다. 직접 마우스 수용은 세션 Disc에 따른 입력 거부로 미실행이다.
