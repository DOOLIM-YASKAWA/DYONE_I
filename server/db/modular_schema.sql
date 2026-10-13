-- DY ONE 모듈라빌드 프로젝트 정보 (그룹웨어 WORKS applet 376 "모듈라빌드 프로젝트 정보"를 그대로 옮긴 구조)
-- 분석 문서: E:\dy-one\docs\모듈라빌드_376_분석_및_DY_ONE_이관설계.md
--
-- 단계 A(미러): DyOne.Worker GwModularSyncJob이 그룹웨어 376을 복사한다(gw_* 열이 채워진 행).
-- 단계 B(시범 입력): DY ONE에서 만든 건(gw_doc_id IS NULL)만 DY ONE에서 수정·상태 전환한다. 미러는 이 행을 건드리지 않는다.
-- 단계 C(전환): DY ONE이 원본이 되면 미러를 끄고 모든 행을 DY ONE에서 관리한다.
--
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/modular_schema.sql  (여러 번 실행해도 안전)

create schema if not exists modular;
comment on schema modular is 'DY ONE 모듈라빌드 프로젝트 정보 (그룹웨어 WORKS applet 376 이관)';

-- 선택지 (로봇기종·케이블·통신·도장기·설치위치·구분·국내해외). code = 그룹웨어 선택지 값 그대로.
create table if not exists modular.option (
  list_key   text    not null,
  code       int     not null,
  label      text    not null,
  sort       int     not null default 0,
  is_default bool    not null default false,
  is_active  bool    not null default true,
  primary key (list_key, code)
);
comment on table modular.option is '모듈라 선택지. list_key: install_location · robot_model · cable_robot_box · cable_box_rc · comm · applicator · category · region';

-- 진행 상태 (그룹웨어 워크플로 그대로)
create table if not exists modular.status (
  id          bigint primary key,           -- 그룹웨어 status id 그대로
  name        text   not null,
  sort        int    not null default 0,
  color       text,
  is_end      bool   not null default false, -- 그룹웨어 설정값 그대로(3. 모듈라 설치가 true로 되어 있음)
  is_begin    bool   not null default false
);
comment on table modular.status is '모듈라 진행 상태 11개 (그룹웨어 applet 376 프로세스)';

create table if not exists modular.transition (
  id           bigint primary key,           -- 그룹웨어 transition id 그대로
  from_status  bigint not null references modular.status(id),
  to_status    bigint not null references modular.status(id),
  action_name  text   not null,
  sort         int    not null default 0
);
comment on table modular.transition is '모듈라 상태 전환 25개 (버튼 이름 = action_name)';

-- 본문: 그룹웨어 문서 1건 = 1행
create table if not exists modular.build (
  id                   bigint generated always as identity primary key,
  gw_doc_id            bigint unique,        -- 그룹웨어 문서 id. NULL이면 DY ONE에서 만든 건
  year                 int,
  project_code         text   not null,      -- project.project.code 와 코드로 연결(외래키 없음)
  project_name         text   not null,
  status_id            bigint references modular.status(id),
  install_start        date,
  install_start_raw    text,                 -- 그룹웨어 원문(YYYYMMDD 문자열). 날짜로 못 바꾼 경우 확인용
  commissioning_start  date,
  commissioning_start_raw text,
  external_inspection  text,
  ship_due             date,
  ship_month           text,
  origin_tool_receipt  text,
  note                 text,
  customer             text,
  plant                text,
  process              text,
  process_detail       text,
  robot_count          int,
  robot_count_raw      text,
  category_code        int,                  -- option(category): 일반산업 / 자동차 / 개조
  region_code          int,                  -- option(region): 국내 / 해외
  main_issue           text,
  attachment_count     int    not null default 0,  -- 참고자료(파일) 개수. 파일 이관은 별도
  legacy               jsonb,                -- 폼에서 지워진 옛 필드 값
  created_by_name      text,
  created_by_gw_id     bigint,
  created_at           timestamptz,
  updated_by_name      text,
  updated_by_gw_id     bigint,
  updated_at           timestamptz,
  synced_at            timestamptz,          -- 미러 Worker가 마지막으로 쓴 시각 (DY ONE 입력 건은 NULL)
  is_deleted           bool   not null default false
);
create index if not exists ix_modular_build_project on modular.build (project_code);
create index if not exists ix_modular_build_status  on modular.build (status_id);
create index if not exists ix_modular_build_year    on modular.build (year);
comment on table modular.build is '모듈라빌드 프로젝트 정보 (그룹웨어 applet 376 문서 1건 = 1행)';

-- 복수 선택 값 (설치위치·로봇기종·케이블·통신·도장기)
create table if not exists modular.build_option (
  build_id  bigint not null references modular.build(id) on delete cascade,
  list_key  text   not null,
  code      int    not null,
  primary key (build_id, list_key, code)
);

-- 담당자 (PM · PL · 제어 · 설계 · 티쳐). 그룹웨어는 이름을 자유 기재 → 원문 보관 + 직원 연결(가능할 때)
create table if not exists modular.build_member (
  build_id    bigint not null references modular.build(id) on delete cascade,
  role        text   not null,               -- pm · pl · control · design · teach
  name_raw    text   not null,
  employee_id bigint,                        -- org.employee.id (이름이 정확히 한 명과 맞을 때만)
  primary key (build_id, role)
);

-- 활동 기록 (그룹웨어 활동 글). 본문은 HTML 원문 + 화면용 글자
create table if not exists modular.activity (
  id             bigint generated always as identity primary key,
  build_id       bigint not null references modular.build(id) on delete cascade,
  gw_activity_id bigint unique,
  body_html      text,
  body_text      text,
  comment_count  int    not null default 0,
  attach_count   int    not null default 0,
  created_by_name text,
  created_at     timestamptz
);
create index if not exists ix_modular_activity_build on modular.activity (build_id);

-- 상태 변경 이력 (DY ONE에서 전환한 것부터 기록)
create table if not exists modular.status_history (
  id          bigint generated always as identity primary key,
  build_id    bigint not null references modular.build(id) on delete cascade,
  from_status bigint,
  to_status   bigint not null,
  action_name text,
  by_name     text,
  by_employee bigint,
  at          timestamptz not null default now(),
  note        text
);
create index if not exists ix_modular_status_history_build on modular.status_history (build_id);

-- ── 2026-10-12 추가: 그룹웨어 데이터 전체 이관(변경 이력 · 댓글 · 첨부 정보) ─────────────

-- 변경 이력 (그룹웨어 go_action_logs). DY ONE에서 한 상태 전환은 status_history에 따로 쌓인다.
create table if not exists modular.history (
  id           bigint generated always as identity primary key,
  build_id     bigint not null references modular.build(id) on delete cascade,
  gw_log_id    bigint unique,
  kind         text   not null,               -- status · edit · created · activity · comment · etc
  title        text,                          -- 화면용 제목 (예: 상태 변경, 출하 월 변경)
  detail       text,                          -- 바뀐 내용 (예: 4. 시운전 -> 5. 외부검수)
  status_from  text,
  status_to    text,
  actor_name   text,
  actor_gw_id  bigint,
  at           timestamptz
);
create index if not exists ix_modular_history_build on modular.history (build_id, at desc);

-- 활동 글의 댓글
create table if not exists modular.comment (
  id             bigint generated always as identity primary key,
  activity_id    bigint not null references modular.activity(id) on delete cascade,
  gw_comment_id  bigint unique,
  body           text,
  author_name    text,
  created_at     timestamptz
);
create index if not exists ix_modular_comment_activity on modular.comment (activity_id);

-- 첨부 파일 정보. 파일 본체는 그룹웨어 서버 디스크(gw_path)에 있다 → stored_path가 채워지기 전까지는 그룹웨어에서 연다.
create table if not exists modular.file (
  id            bigint generated always as identity primary key,
  build_id      bigint not null references modular.build(id) on delete cascade,
  activity_id   bigint references modular.activity(id) on delete cascade,
  source        text   not null,               -- doc(참고자료 필드) · activity(활동 글 첨부)
  gw_attach_id  bigint,
  name          text,
  ext           text,
  size_bytes    bigint,
  gw_path       text,
  gw_host       text,
  stored_path   text,                          -- DY ONE 저장소로 복사한 뒤 채운다
  created_at    timestamptz,
  unique (source, gw_attach_id)
);
create index if not exists ix_modular_file_build on modular.file (build_id);

-- ── 2026-10-12 추가: 두 벌 관리(그룹웨어 사본 + DY ONE 값) ─────────────────────────────────
-- 규칙: 동기화 때 '그룹웨어 값이 지난번 사본과 달라진 칸'만 DY ONE 값(modular.build)에 덮어쓴다.
--       그룹웨어에서 안 바뀐 칸은 DY ONE에서 고친 값이 그대로 남는다. DY ONE 변경은 그룹웨어로 보내지 않는다.
--       managed_in_dyone = true 인 건은 그룹웨어 변경을 더 이상 반영하지 않는다(DY ONE 관리 전환).

alter table modular.build add column if not exists managed_in_dyone    bool not null default false;
alter table modular.build add column if not exists managed_in_dyone_at timestamptz;
alter table modular.build add column if not exists managed_in_dyone_by text;
alter table modular.build add column if not exists gw_deleted_at       timestamptz;  -- 그룹웨어에서 문서가 지워진 시각(DY ONE에서는 지우지 않고 표시만)

-- 마지막으로 읽은 그룹웨어 값(비교용). values = { 칸 키: 값 문자열 } — 키 목록은 DyOne.Data ModularFieldSet
create table if not exists modular.build_gw (
  build_id       bigint primary key references modular.build(id) on delete cascade,
  gw_doc_id      bigint not null unique,
  "values"       jsonb  not null,
  gw_updated_at  timestamptz,
  synced_at      timestamptz not null default now()
);

-- 칸별 변경 기록. source = groupware(동기화로 반영) · dyone(DY ONE에서 수정)
-- overwritten = 그룹웨어 변경을 반영하면서 덮인 DY ONE 값(양쪽에서 같은 칸을 고친 경우)
create table if not exists modular.field_change (
  id           bigint generated always as identity primary key,
  build_id     bigint not null references modular.build(id) on delete cascade,
  field        text   not null,
  before_value text,
  after_value  text,
  overwritten  text,
  source       text   not null check (source in ('groupware', 'dyone')),
  by_name      text,
  at           timestamptz not null default now()
);
create index if not exists ix_modular_field_change_build on modular.field_change (build_id, at desc);

-- 그룹웨어에서 사라진 문서를 예전에는 is_deleted로 표시했다 → gw_deleted_at으로 옮긴다
update modular.build set gw_deleted_at = coalesce(synced_at, now()), is_deleted = false
 where gw_doc_id is not null and is_deleted and gw_deleted_at is null;
