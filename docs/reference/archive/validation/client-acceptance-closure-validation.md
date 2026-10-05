# 클라이언트 재시도·정리·최종 GUI 수용

2026-10-05 / 기준b742750 / codex/client-acceptance-closure. 회사 환경 인수와 합성 로컬 시험을 구분한다.

## 재시도와 적용 후 조회

현재 복원/정리의 retry context를 첫 비동기 I/O 전에 기록한다. 확인 취소는 이전 presentation/상태/유효 resume를 보존하며 preview가 없는 경우 조회로 안내한다. 관리형 preview 응답은 정확한 요청 selection과 비교한다. 사용하지 않는 managed local-path 바인딩을 제거했다. 복구 commit 뒤에는 mutation cancellation을 종료하고 읽기 전용 재확인 제목/단계로 전환한다.

신규18개를 포함한 관련60개 headless/경계 회귀가 통과했다. 실제 retry Button.ClickEvent는 이전 Update/Repair/Launch 호출0건, 확인 취소는 snapshot/resume 보존, 지연된 적용 후 Check는 네 조합 cancel 버튼 제거와 완료 제목/안내 일치를 확인한다.

stage-1 Windows General/Developer/Agent/server publish 후 실제 CLI·console Agent HTTP 요청 서명/readiness/operation/selection-only client/정상 backup IPC 복원 proof가 통과했다. publish source b742750+제품 변경, source hash9ea32560b1761ed541041b2348622c6df0df04b719d9956e520eb0725dd6acf7이다. 이는 최종 GUI 후보가 아니며 실제 GUI/서비스 계정 ACL 통과로 대체하지 않는다.

## 수동 정리 결과

수동 정리는 삭제/실패/실제 잔여 수를 반환하고 일부 실패를 완료로 표시하지 않는다. staging/backups 전용 경로만 허용하며 resume-cache의 파일 목록/크기/수정 시각을 전후 대조한다(전체 내용 hash 인수와 구분). 자동 엔진 Prune의 best-effort 정책은 유지한다. 관련39개 및 stage-2 Windows 게시 CLI/console Agent의 실제 설치/repair/정상 backup 복원이 통과했다. Windows 소유 파일의 delete-sharing handle로 실제 삭제 실패와 해제 후 재정리를 확인했다. Linux는 주입 경계의 권한 거부 시험이며 Windows 잠금 사실로 확대하지 않는다.

stage-2 source2201486+제품 변경/hash ff23541424b7beab85337d430c2470b8551110219efe98941ad7654a47b94156. GUI 정리 실제 적용은 최종 후보에서 별도 수행한다.

## 화면 설정 생성

`sample-config --mode managed-client`는 필수 projectId와 OS/track/version 선택만 출력한다. 운영 필드/프로필·중복/알 수 없는 옵션·exact 버전 누락·latest 버전 혼용을 거부한다. no-force 출력은 원자적 no-overwrite이며 기존 모드는 유지한다. 설정 필요 화면에 키보드로 읽을 수 있는 명령 예시를 제공한다. 관련17개와 stage-3 게시 CLI/console Agent가 실제 생성한 최소 설정으로 doctor/정확한 실행/감독 종료를 통과했다. Developer 게시 EXE의 generator와 build-info도 확인했다. stage-3 sourcef2ab087+제품 변경/hash54ac950d66fbfc42708847bf0392b76872fa476094dbf152364647bebb0d0a9c.

### Linux에서 추가 발견한 설정 생성 경합

Windows785개 전체는 통과했지만 첫 WSL 전체785건은783통과/1실패/1Windows-only skip이었다. `File.Move(overwrite:false)` 동시 작성이 두 번 성공해 기존 설정을 덮을 수 있음을 재현했다. Linux는 RENAME_NOREPLACE를 사용하며 WSL1 파일시스템에서 실제 errno22를 확인해 새로 flush한 임시 설정의 배타적 link 게시→임시 이름 제거를 지원한다. 설치 payload/기존 데이터의 hard link 재사용과 다르며 실패 시 기존 목적지를 변경하지 않는다.

수정한 Windows/WSL 관련10개가 통과했고 WSL 게시 CLI로 설정 생성과 기존 파일 거부를 확인했다. 초기 LF/CRLF patch 적용 실패 뒤 실행된 이전 코드 검사는 수정 증거로 사용하지 않았다. 첫 RENAME_NOREPLACE-only 후보의 WSL2개 실패도 보존하며 최종 전체 회귀/게시본은 뒤에서 기록한다. WSL 임시 mirror 출처는7c6cb1a+명시적 패치이며 Git clean 상태로 보충하지 않는다.

## 최종 시험 도구와 고정 게시본

publisher는 커밋된 Git snapshot에서 General/Developer/Agent/server와 별도 합성 앱을 생성하고 입력 hash·SDK·역할/에디션·runtime sidecar·성공/실패 단계를 기록한다. 새 GUI fixture는 명시적 schema2 cohort manifest와 역할별 실제 경로/hash를 확인한 후 복사하며, 제품 출처와 harness 출처를 분리한다. 이전 root는 조회 가능하지만 최종 GUI 수용으로 채택하지 않는다.

복원 조회 장애는 실제 rollback operation ID/ACK가 아닌 별도 file-transition 관측이다. 정확한 fixture/publication/install/release/backup/fingerprint/새 시도와 손상 사전 inventory·전체 정상 payload/metadata 전환·runtime 불변·설치 잠금 해제를 요구한다. 다른 작업·변경된 preview·미완료 적용·401/403에서는 발화하지 않으며 pending reset과 발화 이력을 보존한다. 게시본×모드×에디션×사례 ledger는 GUI 입력 주체·실제 display 로그·PNG·필수 checks를 요구하고 다른 게시본/비교 scope/CLI 설치/미실행을 전체 통과로 합산하지 않는다.

도구 계약9개/기존fixture23개/proxy4개(합36개)가 통과했다. Windows/Linux final snapshot은 제품18db00d, source hasha93b4a3f89ad61ee9b01c4740ff9a6dcff79a705d0a9a531cacc919ff348a828이며 같은 입력에서 게시했다. 새 managed fixture를 실제 실행하고 상태 버튼 입력·미설치 inventory 불변·display1920×1080/scale1/text1/highContrastfalse를 확인했다. 네 조합 전체 수용은 아직 진행 전/중이며 이 도구 통과가 실제 모든 GUI 적용 통과는 아니다.

이전 병렬 승인3건의 로그에는 IOException 일반 문장만 있어 원인/HResult를 확정할 수 없다. 새 준비는 단계·종료 코드·timeout/start-failed를 원자적으로 기록하고 승인/승격을 별도 기록한다. 새로운 fixture는 순차 준비하며 이를 병렬 실패 해결 증거로 사용하지 않는다.

## 초기 GUI 점검 중 추가 수정

고정18db00d 후보의 실제 개발자 설정 없음 화면에서 주 버튼이 설정 재읽기를 우회해 RunAsync로 진입함을 발견했다. 설정 오류 중에는 양 에디션 주/상태 버튼이 설정 재읽기→조회로 연결되고 주 버튼은설정 다시 확인, 설치 변경/폴더 열기는 비활성화한다. 실제 Button.ClickEvent 네 조합 회귀는 backend Check1회/변경0회·앱 경로 생성0건·확인창0건을 검사한다. 관련14개와 변경 코드의 양 에디션 publish/build-info가 통과했다. 이 제품 수정 후 기존 네 fixture는 보존·소유 harness만 종료했고 다음 고정 후보의 새 root로 영향 시험을 재수행한다.

초기 실제 screen은1920×1080/scale1/text1/highContrastfalse였고 MG 설정 없음/오류/재조회 불변을 확인했다. 캡처는 native JPEG로 제공돼 PNG/JPEG 서명을 모두 지원하고 이후에는 원본 형식 확장자로 저장한다. 초기 일부JPEG.png 이름은 시험 이력이며 최종 공개 근거는 실제 형식을 사용한다.
