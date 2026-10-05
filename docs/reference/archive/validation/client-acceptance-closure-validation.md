# 클라이언트 재시도·정리·최종 GUI 수용

2026-10-05 / 기준b742750 / codex/client-acceptance-closure. 회사 환경 인수와 합성 로컬 시험을 구분한다.

## 재시도와 적용 후 조회

현재 복원/정리의 retry context를 첫 비동기 I/O 전에 기록한다. 확인 취소는 이전 presentation/상태/유효 resume를 보존하며 preview가 없는 경우 조회로 안내한다. 관리형 preview 응답은 정확한 요청 selection과 비교한다. 사용하지 않는 managed local-path 바인딩을 제거했다. 복구 commit 뒤에는 mutation cancellation을 종료하고 읽기 전용 재확인 제목/단계로 전환한다.

신규18개를 포함한 관련60개 headless/경계 회귀가 통과했다. 실제 retry Button.ClickEvent는 이전 Update/Repair/Launch 호출0건, 확인 취소는 snapshot/resume 보존, 지연된 적용 후 Check는 네 조합 cancel 버튼 제거와 완료 제목/안내 일치를 확인한다.

stage-1 Windows General/Developer/Agent/server publish 후 실제 CLI·console Agent HTTP 요청 서명/readiness/operation/selection-only client/정상 backup IPC 복원 proof가 통과했다. publish source b742750+제품 변경, source hash9ea32560b1761ed541041b2348622c6df0df04b719d9956e520eb0725dd6acf7이다. 이는 최종 GUI 후보가 아니며 실제 GUI/서비스 계정 ACL 통과로 대체하지 않는다.

## 수동 정리 결과

수동 정리는 삭제/실패/실제 잔여 수를 반환하고 일부 실패를 완료로 표시하지 않는다. staging/backups 전용 경로만 허용하며 resume-cache의 파일 목록/크기/수정 시각을 전후 대조한다(전체 내용 hash 인수와 구분). 자동 엔진 Prune의 best-effort 정책은 유지한다. 관련39개 및 stage-2 Windows 게시 CLI/console Agent의 실제 설치/repair/정상 backup 복원이 통과했다. Windows 소유 파일의 delete-sharing handle로 실제 삭제 실패와 해제 후 재정리를 확인했다. Linux는 주입 경계의 권한 거부 시험이며 Windows 잠금 사실로 확대하지 않는다.

stage-2 source2201486+제품 변경/hash ff23541424b7beab85337d430c2470b8551110219efe98941ad7654a47b94156. GUI 정리 실제 적용은 최종 후보에서 별도 수행한다.
