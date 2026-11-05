# 회사 주요 후보 v2 백업 (2026-11-04)

v3 기준([회사 주요 프로젝트 선정 기준 v3](../../회사%20주요%20프로젝트%20선정%20기준%20v3%20(2026-11-04).md))으로 바꾸기 전 Worker 코드와 계산 결과를 그대로 둔 것이다. 빌드에 들어가지 않게 `.cs.txt`로 저장했다.

| 파일 | 내용 |
|---|---|
| `FeaturedScorer.v2.cs.txt` | v2 점수: 계약·로봇·신공장·해외·종료 임박·소규모 −2 + 발주 30일 건수·NAS 30일 파일 수·모듈라/ES 기록 수 |
| `TraceCollector.v2.cs.txt` | v2 흔적: NAS 전체 폴더 파일 수(소음 파일만 제외), 모듈라·ES 기록 |
| `ErpMirror.v2.cs.txt` · `MainData.v2.cs.txt` | v2 때 ERP 발주 집계(30·90일)·데이터 묶음 |
| `featured_suggestion v2 결과 (2026-11-04).csv` | 11-04 로컬 v2 계산 결과(상위 30건, 점수 근거·AI 이유 포함) |

되돌리려면 `.cs.txt`를 `server/src/DyOne.Worker/Jobs/ProjectMain/`의 같은 이름 `.cs`로 덮어쓴다(표 칸은 v3에서 늘기만 해서 그대로 써도 된다).
