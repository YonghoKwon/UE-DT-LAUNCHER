# 현재 전체 진행 상태

4종 정본과 현재 게시본 근거에서 생성한 자료입니다. 항목 퍼센트 평균을 제품 완성률로 해석하지 않습니다.

| 구분 | 완료 | 부분 | 대기 | 열린 항목 | 전체 |
|---|---:|---:|---:|---:|---:|
| 작업 전 | 6 | 19 | 3 | 22 | 28 |
| 현재 | 6 | 19 | 3 | 22 | 28 |

| 분야 | 구현 | 자동화 | 게시 실행 | 실제 GUI | 회사 인수 |
|---|---|---|---|---|---|
| 클라이언트 | 재시도·선택·commit 후 오류·정리 보완 | Windows 849 / Linux 848+제외1 | 양 에디션 True/True | 현재 8/51 | 미검증 |
| 서버 정리·복원 | schema2 비재귀 정리·실패 fence 유지 | 저장 경계/새 자료 보존 통과 | 양 OS backup/restore/retention | 해당 없음 | 계정·복구 훈련 별도 |
| 인증·실행 경계 | 보호 유지·거부/슬롯 회귀 보강 | 요청 서명·Bearer·취소/만료 회귀 | HTTP True / HTTPS Windows TLS bearer benchmark and runtime broker passed; Linux current broker timeout; prior Linux TLS proof is historical | 오류 전수 대기 | CA·프록시·계정 별도 |
| 성능 | 이번은 비교 측정, 추가 최적화 미채택 | 90% 바이트 재사용 유지 | Windows 1/10/30,10연결 개선 미달 | 해당 없음 | 실제 규모/SLA 별도 |
| 설치본·CI | 이번 묶음의 변경 대상 아님 | 이전 근거 보존 | 이번 실제 MSI/RPM 설치·원격CI 미실행 | 해당 없음 | 인증서·VM·회사 인수 별도 |

| 현재 GUI 조합 | 통과 | 실패 | 미실행 | 진행 | 환경 제약 |
|---|---:|---:|---:|---:|---:|
| managed / general | 5 | 0 | 7 | 0 | 0 |
| managed / developer | 1 | 0 | 12 | 0 | 0 |
| portable / general | 1 | 0 | 11 | 0 | 0 |
| portable / developer | 1 | 0 | 13 | 0 | 0 |

| 항목 | 진행 | 남은 체크포인트 | 완료 조건 |
|---|---:|---:|---|
| OPS-01 | 50% | 50% | 실제 UE 업데이트/복구·Shipping/Linux/RHEL·회사 TLS/CA·IP·서비스 계정 검증은 남음. 로컬 성공은 회사 승인 아님 |
| OPS-02 | 0% | 100% | 실제 install/upgrade/repair/uninstall·재부팅/서비스·credential·데이터 보존. 계획 생성은 실설치 증거가 아님 |
| OPS-04 | 50% | 50% | 열린 writer/모든 경합·실제 업로드 계정·회사 기간/공개판 삭제 정책 별도 |
| OPS-05 | 50% | 50% | 장애 전수·과거 signing key·회사 RPO/RTO/복구훈련 및 완전 유실 별도 |
| OPS-06 | 50% | 50% | 실제 원격 CI 실행·운영 인수 결과 확인. 공유 runner 시간을 성능 gate로 사용하지 않음 |
| OPS-07 | 50% | 50% | 실제 OS 예약/서비스 계정 인수 별도. 자동 설치/전환 제외 |
| OPS-08 | 50% | 50% | 실제 회사 인증서와 설치된 EXE 서명 검증은 미완료. [기록](../../../../docs/reference/archive/validation/deployment-safety-validation.md) |
| OPS-09 | 75% | 25% | 이전 관리형 복구/rollback과 이번 portable 증거는 게시본별 이력. 새 후보 양 모드 전체 GUI 수용, 모든 진입점/상태 조합의 실제 전수 검증과 회사 UE/계정·원격 CI는 미완료. [기존 기록](../../../../docs/reference/archive/validation/managed-gui-safety-validation.md), [이번 기록](../../../../docs/reference/archive/validation/ui-acceptance-finalization.md) |
| PERF-03 | 75% | 25% | 이전 수치는 과거 이력. 직전Windows1연결10.6% 악화·통제된 반복/작업 간섭·회사 성능 인수는 남음. [최신 비교](../../../../docs/reference/archive/validation/operations-closure-validation.md) |
| USER-01 | 50% | 50% | 실제 UE GUI 설치/업데이트/복원·SaveGame 보존 사용자 직접 조작 대기. CustomLogs·데이터 이전·회사 계정 미완료. 실제 수용 전75%로 올리지 않음 |
| USER-02 | 75% | 25% | 최신 게시본의 네 조합 잔여 오류별 조치/재시도. 실제 회사 새 PC 인수는 별도 |
| USER-03 | 75% | 25% | 회사 계정·적용/복구/부분 파일 전체 장애 인수는 별도 |
| USER-04 | 50% | 50% | 폐기/네트워크 단절/정확한 선택의 전수 실행과 회사 정책 인수 |
| UI-02 | 75% | 25% | 네 조합 잔여 수용은 [단일 체크리스트](../../../../docs/reference/guide-03-launcher-usage.md#현재-gui-수용-체크리스트) 참조. 이전 후보 합산 금지 |
| UI-03 | 75% | 25% | 새 후보 키보드 전체 흐름·OS 고대비 확인. 내레이터 실행/녹음/청취는 후속 보류하며 코드/UIA 통과로 대체하지 않음 |
| UI-04 | 0% | 100% | 필요성 합의 후 opt-in 구현. 닫기/종료 의미·알림 설정 명확화 |
| SEC-01 | 50% | 50% | 운영 교체·서명키 수명·회사 정책/계정 인수 |
| SEC-02 | 50% | 50% | 모든 endpoint/부하 장애와 회사 프록시 인수 |
| SEC-03 | 75% | 25% | Windows LocalService 실제 설치 계정 읽기와 기존 credential 이전 현장 확인 남음. [기록](../../../../docs/reference/archive/validation/intranet-auth-validation.md) |
| SEC-04 | 75% | 25% | WSL proxy의 간헐 handshake/startup timeout과 메모리 지표 미수집을 보존. 실제 RHEL 대용량·회사 HTTP 위험 수용 별도. [기록](../../../../docs/reference/archive/validation/intranet-auth-validation.md) |
| EXT-01 | 0% | 100% | 회사 API 합의 후 timeout/cache TTL/기본 거부. 장애·취소·오래된 응답에서 권한 확대 없음 |
| DEV-01 | 50% | 50% | 실제 GUI 수용·View 책임 추가 정리 |
