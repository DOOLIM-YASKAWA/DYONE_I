# docs — 만들고 분석할 때 참고하는 문서

기능을 만들거나 데이터를 분석할 때 계속 참고할 문서를 모아 둔다. 새로 분석한 결과도 여기에 남긴다.

- 코드에 쓰인 규칙의 **근거**는 여기, 규칙 자체는 코드에 둔다(문서와 코드가 다르면 코드가 실제 동작).
- 원본 자료(그룹웨어·ERP·NAS)는 읽기만 하고, 정리한 결과만 여기에 둔다. 비밀번호·개인 연락처는 넣지 않는다.
- 운영 배포 차이는 여기가 아니라 저장소 최상위 `DEPLOY.md`·`DEPLOY-PENDING.md`.
- 사양정리 양식 규칙(영업부 V5 양식)은 `E:\dy-one\spec-draft\양식\사양정리 양식 규칙.md`(별도 저장소).

## 목록

| 폴더 | 내용 | 쓰는 코드 |
|---|---|---|
| [spec-analysis/](spec-analysis/이력·판%20비교·AI%20역할%20(2026-10-29).md) | 사양서 분석 기능 방향 — 같은 공정 이력(모델·PLC) 비교, 이전 판 비교, 외부/사내 AI 역할. 판 흐름 설계: [사양 판 흐름 설계](spec-analysis/사양%20판%20흐름%20설계%20(2026-10-30).md) · [시안](spec-analysis/사양%20판%20흐름%20시안.html) · [팀 제안 비교](spec-analysis/팀%20제안%20비교%20(2026-10-30).md) · [합친 시안](spec-analysis/사양%20검토%20합친%20시안.html) | `DyOne.Spec/Draft/ModelPicker.cs`·`UtilitySystem.cs`, `Modules/Spec/SpecAi.cs` |
| [partner-access/](partner-access/README.md) | 협력사 로그인·권한 — 정본(그룹웨어 회사 공용 주소록), 회사별 공용 비밀번호, 배정 프로젝트만 보기 | `Platform/ProjectAccess.cs`, `Modules/Partners/PartnersModule.cs`, `DyOne.Worker/Jobs/PartnerSyncJob.cs`, `server/db/partner.sql` (1~3단계) |
| [spec-dept/](spec-dept/README.md) | 사양서에서 부서(역할)별로 확인할 항목 — 조직도별 종합, 사양서 분석, 근거 자료 | `server/src/DyOne.Spec/Dept/DeptChecklist.cs` (사양서 분석 '부서별 확인' 탭) |
| [project-docs/](project-docs/) | 프로젝트 자료함 — 종류(NAS 13칸 + 현장 백업)·NAS 13칸 분류 기준·6~9칸 실제 내용·사내 AI 붙일 자리 | `web/src/pages/projects/ProjectFiles.tsx`, `server/db/project_docs_v2.sql` |
| [ai/](ai/README.md) | 사내 AI 공통 환경 — 공통 클라이언트·화면 에이전트 붙이는 법·원칙 | `server/src/DyOne.Api/Platform/Llm.cs`, `Modules/Ai/AiAgentModule.cs`, `web/src/ai/` |
| [factory-hub/](factory-hub/공장%20허브%20추천안%20(2026-10-30).md) | 공장 허브 — 공장 하나에 SRS·MotoSim·프로젝트·사양을 모아 보는 화면 추천안 · [시안](factory-hub/공장%20허브%20시안.html) | (미구현) |
| [cafeteria-menu/](cafeteria-menu/README.md) | 수담푸드(구내식당) 식단 출처 — 네이버 QR 메뉴판 이미지 구조, 가져오는 방법을 고른 이유 | `DyOne.Worker/Jobs/SudamMenuJob.cs`, `web/src/pages/MealsPage.tsx` |
