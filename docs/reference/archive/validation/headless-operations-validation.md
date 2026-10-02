# Headless 후속 운영 검증

2026-10-03, 기준 fe10488, 브랜치 codex/headless-operations. 실제 GUI/입력 조작은 수행하지 않는다. 회사 운영 인수와 분리한다.

## 1. 측정 기반

- Windows single-file/self-contained Release CLI·console Agent·서버 게시 후 합성 HTTP 요청 서명 E2E 통과.
- 기존 HTTP/Bearer 측정 가정을 제거하고 schema3/device key/명시적 promote로 전환했다. HTTPS/Bearer는 독립 검증하며 서로 다른 프로토콜의 과거 수치를 비교 기준으로 합산하지 않는다.
- 준비 1회, 측정 3회, 1/10/30 연결, Catalog+3바이트 Range, 클라이언트 서명/응답 검증 시간 포함. OS 캐시는 비우지 않았다.
- 첫 비단일 파일 게시 시험은 fixture가 CLI만 복사해 시작 실패했다. 이후 새 single-file 묶음으로 재시험했다. 첫 large 도구 실행은 Markdown 집계 형식 결함으로 종료했으며, 수정 후 새 fixture client-large-02 전체 실행이 통과했다.
- Windows 80MiB/10파일 시험: 최초 83,886,080바이트, 변경 없음 0, 다음 버전 8,388,608, force repair 83,886,080바이트. 다음 버전 90% 절감 유지. 실제 UE 크기/성능 보장이 아니다.
- psutil 20ms 샘플의 출처·누락 사유를 기록한다. 측정 불가를 0으로 보충하지 않는다. 시작/challenge 실패도 실패 그룹으로 기록한다. 계약 테스트 3개 통과.
- raw 로그·키·DB·패키지는 ignored publish/headless 아래에만 보존한다. 이 문서는 정제된 근거이며 PERF-03/SEC-04를 완료로 올리지 않는다.

## 2. 이후 기록

Core 물리 이동, 취소/재개, 인증 수명, 유지보수, 백업/정리, 자동 점검, 최종 재측정 결과는 실제 검증 후 이 문서에 누적한다.
