-- 프로젝트 상세 개편(2026-10-30): 진행 단계 6개 · 자료 종류 = NAS 13칸 + 현장 백업 · 자료 공유 범위 · 프로젝트별 종류 · 진행 일지
-- 근거: docs/시안/project-detail-prototype.html, 설계 노트 project-upload-design.md
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_docs_v2.sql  (여러 번 실행해도 안전)
-- 운영: 이 파일은 스키마·기준 코드만 바꾼다(시험 데이터 없음). DEPLOY.md 2장 스크립트 순서에 추가.

begin;

-- ───────── 1. 진행 단계 8 → 6 ─────────
-- ① 영업 ② 수주(둘 다 그룹웨어) ③ 설계·검토 ④ 모듈라 ⑤ 현장 ⑥ 라인 대응·종료
-- 출하(ship)·시운전·인수(commission)는 '현장'에 합친다. 코드는 지우지 않고 사용 안 함(지난 이력이 가리킴).
update code.code set name = '설계·검토', sort_order = 3 where group_key = 'project_stage' and code = 'design';
update code.code set name = '모듈라', sort_order = 4 where group_key = 'project_stage' and code = 'modula';
update code.code set name = '현장', sort_order = 5 where group_key = 'project_stage' and code = 'install';
update code.code set sort_order = 6 where group_key = 'project_stage' and code = 'line';
update code.code set is_active = false, sort_order = 91 where group_key = 'project_stage' and code = 'ship';
update code.code set is_active = false, sort_order = 92 where group_key = 'project_stage' and code = 'commission';

-- 현장 상태 = 출하·현장 설치·시운전 중 하나라도 진행이면 진행, 아니면 하나라도 완료면 완료
with ids as (
  select max(id) filter (where code = 'install') as install, max(id) filter (where code = 'ship') as ship, max(id) filter (where code = 'commission') as comm
  from code.code where group_key = 'project_stage'
), merged as (
  select s.project_id,
         case when bool_or(s.status = 'run') then 'run' when bool_or(s.status = 'done') then 'done' else 'wait' end as status,
         max(s.updated_at) as updated_at
  from project.project_stage_status s, ids
  where s.stage_id in (ids.install, ids.ship, ids.comm)
  group by s.project_id
)
insert into project.project_stage_status (project_id, stage_id, status, updated_at)
select m.project_id, ids.install, m.status, m.updated_at from merged m, ids
on conflict (project_id, stage_id) do update set status = excluded.status, updated_at = excluded.updated_at;

delete from project.project_stage_status
where stage_id in (select id from code.code where group_key = 'project_stage' and code in ('ship', 'commission'));
update project.project_stage set stage_id = (select id from code.code where group_key = 'project_stage' and code = 'install')
where stage_id in (select id from code.code where group_key = 'project_stage' and code in ('ship', 'commission'));
update project.project_file set stage_id = (select id from code.code where group_key = 'project_stage' and code = 'install')
where stage_id in (select id from code.code where group_key = 'project_stage' and code in ('ship', 'commission'));

-- ───────── 2. 자료 종류(document_category) = NAS 프로젝트 폴더 13칸 + 현장 백업 ─────────
-- Q:_프로젝트\…\<프로젝트>\ 아래 1. 제안서 ~ 13. 기타자료와 같은 이름·순서. 더 필요한 종류는 프로젝트별 직접 입력(3장)이나 나중에 여기에 추가.
-- 기존 코드는 이름만 바꾸고(올린 파일이 가리킴), 13칸에 없는 코드는 사용 안 함으로 둔다.
with v(code, name, sort_order) as (values
  ('quote',      '제안서',          1),
  ('spec',       '사양서',          2),
  ('schedule',   '수행 계획서',     3),
  ('design',     '도면',            4),
  ('modula',     '모듈라빌드',      5),
  ('teach',      '티칭 DATA',       6),
  ('plc',        'PLC DATA',        7),
  ('hmi',        'HMI DATA',        8),
  ('commission', '커미셔닝 DATA',   9),
  ('manual',     '매뉴얼',         10),
  ('report',     '회의록',         11),
  ('final',      '완료보고서',     12),
  ('etc',        '기타자료',       13),
  ('backup',     '현장 백업',      14)
), upd as (
  update code.code c set name = v.name, sort_order = v.sort_order, is_active = true
  from v where c.group_key = 'document_category' and c.code = v.code
  returning c.code
)
insert into code.code (group_key, code, name, sort_order, is_active)
select 'document_category', v.code, v.name, v.sort_order, true from v
where not exists (select 1 from code.code c where c.group_key = 'document_category' and c.code = v.code);
update code.code set is_active = false, sort_order = 90
where group_key = 'document_category' and code in ('contract', 'robot_sim', 'electric', 'photo', 'line');

-- ───────── 3. 프로젝트별 직접 입력 종류 ─────────
-- 그 프로젝트 안에서만 보이는 종류(예: 도장 조건표). 전사 기준 코드에는 넣지 않는다.
create table if not exists project.project_category (
  id          bigint generated always as identity primary key,
  project_id  bigint not null,
  name        text   not null,
  created_by  bigint,               -- org.employee.id
  created_at  timestamptz not null default now(),
  unique (project_id, name)
);

-- ───────── 4. 자료 공유 범위 · 프로젝트별 종류 ─────────
-- share: me(올린 사람만) · dept(올린 사람 부서 = department_id 스냅샷) · project(이 프로젝트 모든 부서)
-- 기존 자료는 지금까지 모두에게 보였으므로 project. 새로 올릴 때 기본값은 화면에서 me.
alter table project.project_file add column if not exists share text not null default 'project';
alter table project.project_file drop constraint if exists project_file_share_check;
alter table project.project_file add constraint project_file_share_check check (share in ('me', 'dept', 'project'));
alter table project.project_file add column if not exists project_category_id bigint references project.project_category (id);
comment on column project.project_file.share is 'me(올린 사람만) · dept(같은 부서) · project(모든 부서). 바꾸기는 올린 사람만';
comment on column project.project_file.project_category_id is '프로젝트별 직접 입력 종류(있으면 category_id 대신 이 이름을 보인다)';

-- 폴더째 올린 자료: 한 번에 올린 폴더 = 묶음(bundle_id), 묶음 안 폴더 경로(rel_path, 맨 위 폴더 이름부터, 파일 이름 제외).
-- 목록은 묶음을 한 줄로 보이고 펼치면 rel_path로 트리를 그린다. 실물도 <종류>/<rel_path>/<파일>로 폴더 구조를 지켜 저장.
alter table project.project_file add column if not exists bundle_id uuid;
alter table project.project_file add column if not exists rel_path text;
create index if not exists ix_project_file_bundle on project.project_file (bundle_id) where bundle_id is not null and is_active;
comment on column project.project_file.bundle_id is '폴더째 올린 묶음(같은 값 = 한 번에 올린 한 폴더). 낱개 파일은 null';
comment on column project.project_file.rel_path is '묶음 안 폴더 경로(예: KaGA_20230805/R1/JOB). 낱개 파일은 null';

-- ───────── 5. 프로젝트 진행 일지 ─────────
-- 프로젝트 인원이 남기는 일지. 보기: 본인 + 같은 부서의 dept 공유 + 모든 project 공유.
-- project 공유 일지만 '진행 단계' 탭 단계별 목록에 보인다. 상태 변경 = 작성자 또는 같은 부서, 공유 범위·본문 변경 = 작성자만.
create table if not exists project.project_journal (
  id             bigint generated always as identity primary key,
  project_id     bigint not null,
  stage_id       bigint,               -- code.code(project_stage), 쓸 때 부서 기본 단계
  author_id      bigint not null,      -- org.employee.id
  department_id  bigint,               -- 작성 당시 부서 스냅샷(같은 부서 판정)
  share          text   not null default 'me' check (share in ('me', 'dept', 'project')),
  status         text   not null default 'run' check (status in ('run', 'done')),
  body           text   not null,
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  updated_by     bigint,
  is_active      boolean not null default true
);
create index if not exists ix_project_journal_project on project.project_journal (project_id, updated_at desc) where is_active;
create index if not exists ix_project_journal_author on project.project_journal (author_id, updated_at desc) where is_active;

commit;
