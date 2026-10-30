# 운영 반영 대기 체크리스트 (DEPLOY PENDING)

로컬에서 검증한 변경을 **운영에 반영할 때 빠뜨리면 안 되는 것들**. 반영하면 체크.

> 비밀값(비밀번호·시크릿)은 저장소에 넣지 않는다. 운영 서버의 환경변수/시크릿에만 둔다.
> **로컬 시험 데이터는 운영에 넣지 않는다**(단계 변경·업로드 파일·사양서 분석/판정·AI 요약·테스트 프로젝트 등). 운영엔 스키마 + 기준 코드만 — DEPLOY.md 0장.

## 0. 운영 데이터 원칙 점검 (매 배포)
- [ ] 로컬 DB 덤프·복원이나 테이블 데이터 복사로 운영에 넣지 않았다(스키마 스크립트 + `seed_reference_codes.sql`만 실행)
- [ ] `.filestore`(로컬 업로드 실물)를 운영 NAS로 복사하지 않았다
- [ ] 배포 직후 운영에서 `server/db/check_prod_clean.sql` 실행 → 테스트 흔적 0, 사용자 데이터 0 확인
- [ ] `app_access.sql` 실행(개발자 '모든 앱' 지정 포함, 2026-10-14 결정 — 세부 권한은 추후). **운영 조직도 동기화(OrgSync) 후에** 실행하고, `select app_key, count(*) from access.app_department group by 1` / `access.app_employee`로 지정이 들어갔는지 확인

---

## 1. 운영 dyone DB (.111:12678) 스키마 — ⚠️ 가장 먼저
- [ ] `project.project`에 컬럼 추가 (로컬 5433엔 이미 적용됨):
  ```sql
  ALTER TABLE project.project ADD COLUMN IF NOT EXISTS source_updated_at timestamp;
  ```
  - 이유: Worker가 **GW 문서 수정일(`source_updated_at`)**을 이 컬럼에 적재한다. **컬럼이 없으면 운영 적재(MERGE)가 실패**한다.
  - ⚠️ 이 컬럼을 추가하기 전에는 Worker를 **운영 타깃으로 돌리면 안 됨**.
  - (2026-10-15) API도 이 컬럼을 쓴다: 프로젝트 목록·상세·홈의 **'최근 변경' = GW 문서 수정일(`source_updated_at`)과 DY ONE 단계·진행 상황 변경(`project_stage`·`project_stage_status`의 `updated_at`) 중 더 최근 것**. 컬럼이 없으면 운영 API 프로젝트 화면 자체가 오류 → **API 배포 전에 반드시 적용**.

## 2. Worker 운영 적재 (프로젝트 + 조직도)
- [ ] 위 1번 컬럼 추가 **완료 후에만** 운영 타깃 포함해 실행.
- 운영 포함 실행 시 환경변수:
  ```
  WriteTargets__0=DyOneWrite
  WriteTargets__1=DyOneWriteProd
  ConnectionStrings__DyOneWriteProd=Host=192.168.30.111;Port=12678;Database=dyone;Username=postgres
  DyOneWriteProd__Password=<운영 비번>
  ```
  (운영 접속정보는 E:\dy-one\etl\sync.config.json 의 dyone 항목과 동일)
- [ ] **매일 08:00 자동 적재**가 필요하면, 운영 서버에 Worker를 상주(서비스)시키고 위 환경변수를 설정. (지금은 상시 실행 아님 — 수동 실행만)
- 기본값(appsettings)은 `WriteTargets=["DyOneWrite"]` = 로컬만. 운영은 명시할 때만 포함.
- (2026-10-15) GwProjectSyncJob MERGE는 **GW 값이 실제로 바뀐 행만** 갱신(`updated_at`도 그때만 바뀜). 예전 버전 Worker를 운영에 돌리면 매번 전 행 `updated_at`이 덮어써지니 **새 버전으로 배포**할 것. 추가 스키마 없음.

## 3. SmartLab (.119) — B 방식 SSO 핸드오프 배포
로컬 SmartLab(E:\dy-one-smartlab)에 넣은 변경을 운영(.119, NSSM `DY-SmartLab`)에 반영:
- [ ] `server/index.js` — `GET /sso/dyone`(dyone 토큰 검증 → SLAB_SESS 발급) 추가분
- [ ] `src/App.jsx` — 임베드 감지(`window.self!==window.top`) 시 자체 헤더 숨김 + gw 리다이렉트 회피
- [ ] `src/styles.css` — 임베드 톤(헤더 숨김/블루톤/thead 회청색)
- [ ] 재배포 후 `nssm restart DY-SmartLab`
- [ ] .119 서비스 env에 **`DYONE_SSO_SECRET`** 추가 — dyone의 `SmartLabSso:Secret`와 **동일 값**
- [ ] dyone 운영 설정에 `SmartLabSso:Secret`(동일) + `SmartLabSso:BaseUrl`(운영 SmartLab 주소)
- [ ] ⚠️ **운영은 cross-site**(dyone 도메인 ↔ .119)라, 로컬의 same-site 쿠키 트릭이 안 통함.
  - 해결: dyone이 SmartLab을 **리버스 프록시(`/smartlab`)로 same-origin** 제공 → iframe 쿠키 동작.
  - 또는 SmartLab 쿠키를 `SameSite=None; Secure`로 + 양쪽 https.

## 3b. dyone 자체 프로젝트 상태(단계) — 스키마 (.111)
로컬(5433)에만 생성됨. 운영(.111)에도 동일 생성 필요:
- [ ] 코드 그룹 + 단계 코드셋: **`server/db/seed_reference_codes.sql` 실행**(현재 로컬 기준값: 영업·수주·설계·구매·모듈라·OLT·출하·현장 설치·시운전·인수·라인 대응·종료 + 사용 안 함 spec, 문서 분류 7종)
  - ⚠️ (2026-10-14 정정) 예전에 여기 적혀 있던 코드 SQL(`sales`, `done` 등)은 **옛 체계라 쓰면 안 된다.** 시드 스크립트만 쓴다.
- [ ] dyone 상태 테이블:
  ```sql
  create table if not exists project.project_stage (
    project_id bigint primary key, stage_id bigint not null,
    updated_by bigint, updated_at timestamptz not null default now());
  ```
  - 설계: 그룹웨어 상태(영업중/수주)는 읽기전용 유지. 수주 이후 단계는 이 테이블에서 사용자가 수정. 효과적 상태 = project_stage 있으면 그 값, 없으면 GW(영업중→영업/수주→수주). 그룹웨어 마감·중단은 무시.

- [ ] 단계 변경 이력(2026-10-14 로컬 적용): 운영 dyone에 `server/db/project_stage_history.sql` 적용(기존 현재 단계는 '처음 지정' 한 줄로 채움). 적용 전에는 이력 화면이 비어 있고 단계 변경 기록이 남지 않는다 — 변경 API가 이력 테이블 없이 실패하므로 **API 배포 전에 먼저 적용**
- [ ] 단계별 진행 상황(대기·진행·완료, 동시 진행): 운영 dyone에 `server/db/project_progress.sql` 적용(project_stage_history.sql 다음). 이미 대표 단계가 있는 프로젝트는 앞=완료·그 단계=진행·뒤=대기로 채운다. 화면의 '진행 상황 변경'은 PUT /api/projects/{id}/progress를 쓴다
- [ ] 진행 단계 9→8(2026-10-14): 운영 dyone에 `server/db/project_stage_v2.sql` 적용(project_progress.sql 다음). 사양 검토·견적을 영업에 합치고(코드 spec 사용 안 함), 영업·수주는 그룹웨어 상태로 계산, 설계·구매부터 DY ONE에서 관리. **API 배포와 함께** 적용(새 API는 영업·수주 변경을 거절)
  - 같은 파일에 진행 상황 이력의 그룹웨어 상태 기록 칸(source·gw_from·gw_to)이 들어 있다. Worker(GwProjectSyncJob)가 매일 동기화 때 그룹웨어 상태가 바뀐 프로젝트를 이력에 남긴다(칸이 없으면 건너뜀)

## 3c. 프로젝트 문서 업로드 — 스키마(.111) + 파일 저장소(NAS) (2026-10-13 로컬 적용)
로컬(5433)에만 생성됨. 운영(.111)에도 동일 필요:
- [ ] 운영 dyone(.111:12678)에 `server/db/project_files_schema.sql` 적용 (table `project.project_file` + 분류 코드 `code.code(document_category)`; 여러 번 실행해도 안전)
- [ ] **파일 저장소(NAS) 경로 설정** — 운영 API 설정에 `FileStorage:Root` = 사내 NAS 공유 폴더 UNC (예: `\\nas\dyone-files`).
  - ⚠️ **실제 UNC 경로 미정 — 사용자에게 확인 후 입력.** 로컬 개발값은 `E:\dy-one-internal\.filestore`.
  - 환경변수로 줄 경우: `FileStorage__Root=\\nas\dyone-files`
- [ ] NAS 폴더가 **API 서비스 계정으로 읽기/쓰기 가능**해야 함(폴더 자동 생성·파일 쓰기·분류 변경 시 이동).
- 저장 레이아웃(A안): `<Root>\projects\<프로젝트코드 이름>\<분류>\<원본파일명>` (사람이 탐색기로도 찾게). 중복 시 ` (2)` 접미. `stored_name`=Root 기준 상대경로.
- `department_id` 컬럼 포함(`add column if not exists`, 기존 환경 멱등). 업로더 소속 부서 스냅샷 → 앱 부서별 검색.
- [ ] 운영 dyone 접속 계정이 `project.project_file`에 **INSERT/UPDATE 권한**(단계 테이블과 동일 경로 — 읽기전용 연결에서 옵션만 제거해 씀).
- 업로드 용량 상한: API에서 512MB(Kestrel `MaxRequestBodySize` + `FormOptions.MultipartBodyLengthLimit`). 필요 시 조정.

## 3d. 사양서 분석 1차 (2026-10-16 로컬, DB 변경 없음)
- [ ] 운영 API 설정 `Spec:LibraryRoots` = NAS 프로젝트 폴더 **UNC 경로**(예: `\\NAS\...\01_프로젝트`), API 서비스 계정에 **읽기 권한**
- [ ] `Spec:PythonPath` (운영 서버에 Python + PyMuPDF 있으면) — 없으면 비워 둠
- [ ] 배포 후 영업 단계 '사양서 분석' 탭에서 'NAS 다시 읽기' 1회 → 캐시 `FileStorage:Root\_spec\library.json` 생기는지 확인
- [ ] (2026-10-23 추가) 사양정리 초안·모델 결정: 운영 서버 Python에 **python-pptx·PyMuPDF** 설치(초안 pptx·사양서 쪽 그림), 빌드 출력 `python/render_spec_summary.py`·`python/spec_page.py`·`templates/spec_summary_v5.pptx` 있는지
- [ ] (2026-10-23 추가) **Worker `spec-index`**(매일 03:00): 운영 Worker 설정에 `Spec:LibraryRoots`(UNC)·`Spec:PythonPath`·`FileStorage:Root`를 **API와 같은 값**으로, Worker 계정에 NAS 읽기·캐시 폴더 쓰기 권한. 배포 직후 화면 설비 이력 '다시 읽기' 1회 → `equipment.json` 생기는지
- [ ] (선택) 사내 AI 키 `Llm:ApiKey`(user-secrets/환경변수) — 없으면 규칙 초안(빈 칸·기타 사항은 사람이 채움)
- 이전 판(pilot 이식본, `spec` 스키마)은 2026-10-15 삭제 — 운영 적용할 것 없음. 백업은 side-session-notes

## 3g. 협력사 로그인 · API 기본 차단 (2026-10-30 로컬, DEPLOY.md 14장)
- [ ] 운영 dyone(.111)에 `server/db/partner.sql` 적용 — **API 배포 전**(project_member.sql 다음). 데이터 없음
- [ ] 운영 DB 접속 계정에 `partner.company`·`partner.person` SELECT/UPDATE, `project.project_member` 새 칸 권한. Worker 쓰기 계정에 `partner.*` INSERT/UPDATE
- [ ] 운영 Worker 설정 `PartnerSync`(DailyAt 08:10, BookId 15) → 배포 후 1회 실행해 `select company_id, count(*) from partner.person where is_active group by 1` 확인(로컬 기준 71:77 · 72:19 · 439:17 · 450:14 · 640:6)
- [ ] 운영 관리자 화면 `/admin/partners`에서 5개 회사 공용 비밀번호 입력(로컬 해시 복사 금지)
- [ ] 배포 후 직원 로그인·홈·프로젝트·조직도·SmartLab 임베드가 그대로인지(기본 정책이 직원 전용으로 바뀜). 옛 토큰으로 접속 중인 직원도 그대로여야 함
- [ ] 로컬 시험 배정 행(task '[시험]…')은 운영에 없어야 함 — `check_prod_clean.sql`에 넣을지 검토
- [ ] (5단계) 현장 협력사 외부 접속 방식(https 리버스 프록시 vs VPN) 결정

## 3e. 조직도 · 프로젝트 인원(PM·투입 인원) (2026-10-15 로컬 적용)
- [ ] **결정 필요**: DY ONE 전용 테이블 추가 → 스키마 소유자(팀장님) 확인 후 운영 적용
- [ ] 운영 dyone(.111:12678)에 `server/db/project_member.sql` 적용(`project.project_member`, 여러 번 실행해도 안전). 데이터는 넣지 않음(로컬 지정 값은 시험 데이터)
- [ ] 운영 DB 접속 계정에 `project.project_member` INSERT/UPDATE 권한(단계 테이블과 같은 쓰기 경로)
- [ ] 조직도 화면은 `org.*`를 읽으므로 운영 Worker 조직도 적재(2장)가 먼저 돼 있어야 함
- [ ] 직원 연락처(휴대폰·직통): 운영 dyone에 `server/db/org_contact.sql` 적용(`org.employee_contact`). **이 표가 없으면 조직도·프로젝트 인원 API가 실패**하므로 API 배포 전에 적용. 데이터는 Worker 조직도 동기화(OrgSyncJob)가 매일 채움(표가 없는 대상은 건너뜀)
- 로컬 5433의 연락처는 2026-10-15에 그룹웨어 조회로 1회 채움 — 로컬 Worker는 `Gw:Password` 미등록이라 그룹웨어 동기화가 실패하는 상태(아래 항목)
- [ ] 소속·부서 이력(부서 자동 부여 P1, 2026-10-28 로컬 적용): 운영 dyone에 `server/db/org_history.sql` 적용. 데이터는 넣지 않음(로컬 이력은 옮기지 않음). 적용 후 운영 Worker 조직도 동기화가 처음 돌 때 그룹웨어 결재·부서 정보로 이전 이력을 한 번 채우고(로그 "소속·부서 이력: … 결재 근사 소속 이력 N건"), 다음 날부터 바뀐 사람·부서만 기록. 확인: `select source, count(*) from org.employee_dept_history group by 1` / `org.department_history`
- [ ] **결정 필요**: PM 지정·인원 편집 권한을 PM·관리자 역할로 제한할지(지금은 로그인 전원) — 부서 자동 부여 설계(P6)에서 `pm.*` 역할 + 프로젝트 PM + 관리자로 제한 예정

## 4. dyone 앱 커밋/배포
- [ ] 미커밋 변경 커밋: SmartLab 탭/모듈(`SmartLabModule`,`SmartLabPage`), 홈 위젯(최근 등록/변경), `source_updated_at`(엔티티·Worker·HomeModule), 조직도 Worker 다중타깃 등.
- [ ] 운영 dyone 설정(환경변수): `WriteTargets`, `SmartLabSso:*`, 각 `*:Password` 시크릿.
- [ ] `CLAUDE.md`(작업 가이드) 커밋 여부 결정.

## 모듈라빌드(그룹웨어 376) — `modular` 스키마 (2026-10-12 로컬 적용)
- [ ] 운영 dyone(.111:12678)에 스키마 적용: `server/db/modular_schema.sql` (여러 번 실행해도 안전)
  - 스키마가 없는 쓰기 대상은 `GwModularSyncJob`이 건너뛴다(오류 아님). 적용 전에는 운영에 모듈라 데이터가 쌓이지 않는다.
- [ ] Worker 시크릿에 그룹웨어 비번 `Gw:Password` 등록 (현재 Worker user-secrets에 없음 → GW 연결 실패)
- [ ] 운영 Worker 설정 확인: `GwModularSync:IntervalMinutes`(기본 1분, 변경 이력 피드 확인 1ms 안팎) · `FullSyncHours`(기본 24)
- [ ] 첨부 파일 본체(약 7.6GB) 복사 여부 결정: 그룹웨어 서버 파일 접근 계정 + DY ONE 저장 위치(NAS/GCS)
- [ ] API `Modular:EditEnabled` 운영 값 결정: 켜면 DY ONE에서 수정·상태 전환 가능(그룹웨어로는 안 보냄, 그룹웨어가 같은 칸을 바꾸면 덮임). 켤 때 API에 `ConnectionStrings:DyOneWrite` 필요

## 그룹웨어 앱 공통(works.*) — 281 ES 이슈 (2026-10-13 로컬 적용)
- [ ] 운영 dyone(.111:12678)에 `server/db/works_schema.sql` 적용 (스키마가 없는 쓰기 대상은 GwWorksSyncJob이 건너뛴다)
- [ ] 운영 Worker 설정 `GwWorksSync:Applets`(예: [281]) — 기본값이 비어 있어 설정하지 않으면 아무 앱도 옮기지 않는다
- [ ] API `Works:EditEnabled` 운영 값 결정 (켤 때 API에 `ConnectionStrings:DyOneWrite` 필요)

## MotoSim (MotoSim Launcher 이관) 1단계 — `motosim` 스키마 · 조회 화면 (2026-10-21 로컬 적용)
- [ ] 운영 dyone(.111:12678)에 `server/db/motosim_schema.sql` 적용(여러 번 실행해도 안전). **API 배포 전에** 적용 — 표가 없으면 MotoSim 화면이 "표가 없습니다"로 실패
- [ ] 원본 이력 복사 여부·시점 결정: `E:\dy-one\etl\etl-motosim.mjs`(원본 SmartWork_V2 `motosim_launcher.*` 읽기 전용 → `motosim.*`, 원본 id 유지, 다시 돌려도 안전). 운영 대상은 `--allow-prod`를 붙여야만 실행되고, 연결 문자열은 환경변수(`MOTOSIM_SRC_URL`, `DYONE_DST_URL`)로만 받는다
  - 이 데이터는 로컬 시험 데이터가 아니라 런처 운영 이력(Cell 파일 110·Drive 폴더 매핑 88·즐겨찾기 8·권한 26·시스템관리자 3·플러그인 2)이다. 런처가 계속 운영되는 동안은 원본이 바뀌므로 **전환 직전에** 복사한다
  - 로컬 복사 결과(2026-10-21): 원본과 건수 일치(파일 110 · 최신 26 · 삭제 61 · Zone 37). Zone id는 plant.zone과 1:1(etl-plant가 원본 id 보존)
- [ ] 공정 전용 DB 계정(`dyone_process`, dy-one-process `deploy/create-db-role.sql`)으로 바꿀 때 `motosim.cell_file(zone_id)`·`motosim.favorite_zone(zone_id)` SELECT 권한 추가 — 공정 삭제 전 참조 검사가 이 표를 본다(지금은 슈퍼유저라 해당 없음)
- 로컬 메모: 로컬 plant가 운영보다 늦어 부스 161·162, Zone 387~391(운영에 2026-10-20 추가한 것)을 운영에서 로컬로 1회 복사했다(운영 방향 아님)

## MotoSim 2~6단계 — NAS 저장 · 올리기/받기/삭제 · 실행 · Smart Cell Plugin · 허브/관리자 센터 (2026-10-28 로컬, 권한 제외)
- [ ] 운영 dyone에 `server/db/motosim_schema.sql` 다시 적용(2단계 부분: `cell_file` 열 추가·최신 기준을 Zone+프로젝트로, `upload_session`·`notice`·`activity`, `plugin_version` NAS 열). 여러 번 실행해도 안전
- [ ] API `FileStorage:Root`(NAS UNC)에 MotoSim 폴더가 생긴다: `프로젝트/<코드 연도>/<코드>_<이름>/MotoSim Cell/<공장>/<부스>/<Zone>/<YYYYMMDD-HHMM>_<파일>.zip`, 휴지통 `_trash/motosim/<날짜>/`, 조각 `_tmp/motosim-uploads/`, 플러그인 `_motosim/plugins/smartcell/<버전>/`, MotoSim 설치 파일 `_motosim/installers/`(관리자가 직접 넣음). API 실행 계정에 쓰기 권한 필요
- [ ] 리버스 프록시(.119 443)의 요청 크기 제한: 업로드는 8MB 조각(PUT `/api/motosim/uploads/{id}/chunk`)이라 **최소 10MB** 허용. 플러그인 설치 파일은 한 번에 올리므로 그 크기 이상
- [ ] Windows 앱 새로 배포(desktop: Broker `cell.*`·`motosim.*`·`smartcell.*`·`job.cancel`, bridge.js 이벤트 `on()`). 옛 앱에서는 MotoSim 화면의 PC 기능이 "허용되지 않은 명령"으로 실패한다
- [ ] 원본 런처 Google Drive 파일(사용 중 49개 25.5GB)과 Smart Cell 설치 파일을 NAS로 옮기는 스크립트(Drive 자격 증명은 런처 운영 서버에만 있음). 옮기기 전까지 화면에 "Drive에 있음"으로 보이고 받기·삭제가 안 된다
- [ ] 권한: 지금은 로그인한 직원 누구나 업로드·삭제·공지·플러그인 배포·관리자 센터를 쓸 수 있다. **운영 공개 전에 권한 단계(게스트·작업자·관리자·시스템관리자)를 반드시 넣는다**
- [ ] 실행 전 Cell 정리(원본 런처는 MODELGROUP·로봇 폴더·CONVEYOR 파일을 PC에서 지우고 실행): DY ONE은 PC 폴더를 건드리지 않아 하지 않는다. 실제 Cell로 시험한 뒤 필요하면 결정
- 원본과 다르게 한 것(2026-10-21 결정 반영): 업로드 전 정리는 PC에서 지우지 않고 zip에서만 뺀다(백업 파일 `*.vcl.bak`·`VRC.bin.bak`·`*_bk_YYYYMMDD_HHMMSS.zip`, 안 쓰는 로봇 폴더, .vcl 안의 안 쓰는 MODELGROUP). `CONVEYOR*` 폴더는 빼지 않는다. 내려받기는 기존 폴더를 지우지 않고 `이름 (2)`에 푼다. 7-Zip 대신 .NET zip. Smart Cell 제거는 원본 전용 스크립트 대신 설치 프로그램의 UninstallString(`/S`)
- 로컬 확인(2026-10-28): 20MB 3조각 업로드·SHA-256 일치·위치 어긋남 409·같은 이름 " (2)"·Range 이어받기·삭제 후 이전 파일 최신 승격·즐겨찾기·공지·통계·플러그인 업로드(이름 규칙·MZ 검사)·경로 섞기 차단. Broker는 시험용 콘솔에서 압축→업로드→받기→풀기 바이트 일치. MotoSim·Smart Cell이 이 PC에 없어 실행·설치는 실물로 못 봤다. 시험 데이터는 지웠다
- 이식 명세: side-session-notes `motosim-porting-spec.md`(원본 main.js 규칙·줄 번호)

## 그룹웨어 변경 알림 (2026-10-13 로컬 적용)
- [ ] 운영 dyone(.111:12678)에 `server/db/sync_change_notice.sql` 적용 — 없으면 Worker는 알림 없이 동기화만 하고 API 알림 전달은 1분마다 다시 확인만 한다
- [ ] API 설정 `Notifications:GwChangeRelaySeconds`(기본 5초, 0이면 끔) 확인
- [ ] 운영 dyone에 `server/db/app_access.sql` 적용(앱별 사용 부서 access.*) — 모듈라=모듈라팀 초기 값은 부서 이름으로 넣으므로 운영 조직도 id 확인
- [ ] 받는 사람(app_access.sql에 포함): 모듈라=모듈라팀, ES 이슈=ES영업실(ES영업팀·CS팀), 모든 앱('*')=함유진(DY ONE 개발). 새 앱을 붙일 때마다 받는 부서를 정해 넣는다
- [ ] 그룹웨어 알림 그대로 받기(GwNotiMirror, 30개 앱): 운영 Worker 설정에 `GwNotiMirror:Applets` 넣기. 앱별 받는 부서는 부서 권한(YTVH-432) 때 access.app_department에 넣는다 — 그 전까지는 '*' 지정자만 받는다
- [ ] 놓친 알림 읽음 표시: API에 `ConnectionStrings:DyOneWrite` 필요(없으면 읽음이 그 화면에서만 남음)

## [임시] 데모 화면 메모(포스트잇) — 운영에 넣지 않음 (2026-10-14 로컬)
- [ ] 운영에는 `server/db/demo_memo.sql`을 적용하지 않고 `DemoMemo:Enabled`도 켜지 않는다(기본 false)
- [ ] 데모가 끝나면 삭제: `web/src/components/DemoMemos.tsx`, `web/src/styles/demo-memo.css`, Layout의 `<DemoMemos />` 한 줄, `server/src/DyOne.Api/Modules/Demo/`, Program.cs의 `app.MapDemoMemos(config)` 한 줄, appsettings `DemoMemo`, `server/db/demo_memo.sql`, DB `demo` 스키마
