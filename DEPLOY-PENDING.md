# 운영 반영 대기 체크리스트 (DEPLOY PENDING)

로컬에서 검증한 변경을 **운영에 반영할 때 빠뜨리면 안 되는 것들**. 반영하면 체크.

> 비밀값(비밀번호·시크릿)은 저장소에 넣지 않는다. 운영 서버의 환경변수/시크릿에만 둔다.

---

## 1. 운영 dyone DB (.111:12678) 스키마 — ⚠️ 가장 먼저
- [ ] `project.project`에 컬럼 추가 (로컬 5433엔 이미 적용됨):
  ```sql
  ALTER TABLE project.project ADD COLUMN IF NOT EXISTS source_updated_at timestamp;
  ```
  - 이유: Worker가 **GW 문서 수정일(`source_updated_at`)**을 이 컬럼에 적재한다. **컬럼이 없으면 운영 적재(MERGE)가 실패**한다.
  - ⚠️ 이 컬럼을 추가하기 전에는 Worker를 **운영 타깃으로 돌리면 안 됨**.

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
- [ ] 코드 그룹 + 단계 코드셋:
  ```sql
  insert into code.code_group (key, name)
  select 'project_stage','프로젝트 단계' where not exists (select 1 from code.code_group where key='project_stage');
  insert into code.code (group_key, code, name, sort_order, is_active)
  select 'project_stage', v.code, v.name, v.sort_order, true from (values
    ('sales','영업',1),('spec','사양 검토·견적',2),('order','수주',3),('design','설계',4),
    ('modula','모듈라(OLT)',5),('install','현장 설치',6),('line','라인 대응',7),('done','완료',8)
  ) v(code,name,sort_order)
  where not exists (select 1 from code.code c where c.group_key='project_stage' and c.code=v.code);
  ```
- [ ] dyone 상태 테이블:
  ```sql
  create table if not exists project.project_stage (
    project_id bigint primary key, stage_id bigint not null,
    updated_by bigint, updated_at timestamptz not null default now());
  ```
  - 설계: 그룹웨어 상태(영업중/수주)는 읽기전용 유지. 수주 이후 단계는 이 테이블에서 사용자가 수정. 효과적 상태 = project_stage 있으면 그 값, 없으면 GW(영업중→영업/수주→수주). 그룹웨어 마감·중단은 무시.

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

## 그룹웨어 변경 알림 (2026-10-13 로컬 적용)
- [ ] 운영 dyone(.111:12678)에 `server/db/sync_change_notice.sql` 적용 — 없으면 Worker는 알림 없이 동기화만 하고 API 알림 전달은 1분마다 다시 확인만 한다
- [ ] API 설정 `Notifications:GwChangeRelaySeconds`(기본 5초, 0이면 끔) 확인
- [ ] 운영 dyone에 `server/db/app_access.sql` 적용(앱별 사용 부서 access.*) — 모듈라=모듈라팀 초기 값은 부서 이름으로 넣으므로 운영 조직도 id 확인
- [ ] 받는 사람(app_access.sql에 포함): 모듈라=모듈라팀, ES 이슈=ES영업실(ES영업팀·CS팀), 모든 앱('*')=함유진(DY ONE 개발). 새 앱을 붙일 때마다 받는 부서를 정해 넣는다
- [ ] 놓친 알림 읽음 표시: API에 `ConnectionStrings:DyOneWrite` 필요(없으면 읽음이 그 화면에서만 남음)
