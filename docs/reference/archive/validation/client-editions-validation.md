# 클라이언트 에디션 분리 검증

2026-10-03, 기준 eea1dd3, codex/client-editions-ux-acceptance. 일반/개발자 GUI는 컴파일된 에디션으로 결정하고 기존 CLI는 공통으로 유지한다.

## 초기 빌드 확인

General/Developer의 obj/bin·assembly를 분리하고 Core의 Developer friend assembly와 동적 브랜드 resource URI를 적용했다. step1b의 두 게시 EXE `--build-info`에서 General/Developer를 확인했고 관련10개 회귀 통과. 반대 설정·재로드·메모리 profile 변경으로 capability가 바뀌지 않는다. 기존 self-update는 새 바이너리를 자동 교체하지 않으며 설정/pending을 보존한다.

새 managed fixture의 실제 일반 창에서 업데이트 서비스 정상·미설치·설치 후 실행을 읽었고 별도 개발자 창 제목을 확인했다. 직접 마우스 클릭은 GetCursorPos 0x80070005로 거부됐다. 실제 설치/실행은 미실행이며 도구 권한 오류를 제품 실패로 처리하지 않는다. 사용자에게 로그인된 데스크톱 접근을 요청한 상태이며 독립 구현·검증은 계속 진행한다.
