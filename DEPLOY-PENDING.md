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

## 4. dyone 앱 커밋/배포
- [ ] 미커밋 변경 커밋: SmartLab 탭/모듈(`SmartLabModule`,`SmartLabPage`), 홈 위젯(최근 등록/변경), `source_updated_at`(엔티티·Worker·HomeModule), 조직도 Worker 다중타깃 등.
- [ ] 운영 dyone 설정(환경변수): `WriteTargets`, `SmartLabSso:*`, 각 `*:Password` 시크릿.
- [ ] `CLAUDE.md`(작업 가이드) 커밋 여부 결정.

