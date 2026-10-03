# 클라이언트 에디션 분리 검증

[정제 실행 근거](client-editions-evidence.json). GUI의 환경 제약은 미실행으로 보존하며 과거 CLI 설치 결과를 실제 클릭 증거로 사용하지 않는다.

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
