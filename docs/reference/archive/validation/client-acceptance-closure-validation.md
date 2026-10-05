# ク라이언트 재시도·정리·최종 GUI 수용

2026-10-05 / 기준b742750 / codex/client-acceptance-closure. 회사 환경 인수와 합성 로컬 시험을 구분한다.

## 재시도와 적용 후 조회

현재 복원/정리의 retry context를 첫 비동기 I/O 전에 기록한다. 확인 취소는 이전 presentation/상태/유효 resume를 보존하며 preview가 없는 경우 조회로 안내한다. 관리형 preview 응답은 정확한 요청 selection과 비교한다. 사용하지 않는 managed local-path 바인딩을 제거했다. 복구 commit 뒤에는 mutation cancellation을 종료하고 읽기 전용 재확인 제목/단계로 전환한다.

신규18개를 포함한 관련60개 headless/경계 회귀가 통과했다. 실제 retry Button.ClickEvent는 이전 Update/Repair/Launch 호출0건, 확인 취소는 snapshot/resume 보존, 지연된 적용 후 Check는 네 조합 cancel 버튼 제거와 완료 제목/안내 일치를 확인한다.

stage-1 Windows General/Developer/Agent/server publish 후 실제 CLI·console Agent HTTP 요청 서명/readiness/operation/selection-only client/정상 backup IPC 복원 proof가 통과했다. publish source b742750+제품 변경, source hash9ea32560b1761ed541041b2348622c6df0df04b719d9956e520eb0725dd6acf7이다. 이는 최종 GUI 후보가 아니며 실제 GUI/서비스 계정 ACL 통과로 대체하지 않는다.
