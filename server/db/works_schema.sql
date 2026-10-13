-- DY ONE 공통 그룹웨어 앱 저장소 (그룹웨어 WORKS applet을 앱 번호만 지정해 그대로 옮기는 구조)
-- 첫 대상: applet 281 "04. ES_ 이슈사항 보고". 이후 다른 앱(296 업무일지 등)도 같은 테이블을 쓴다.
-- 모듈라(376)는 먼저 만든 전용 스키마 modular.* 를 쓴다(같은 동작).
--
-- 두 벌 관리: works.doc = DY ONE 값(화면·수정 대상), works.doc_gw = 마지막으로 읽은 그룹웨어 값(비교용).
--   동기화 때 '그룹웨어 값이 지난번과 달라진 칸'만 DY ONE 값에 덮어쓴다. DY ONE 변경은 그룹웨어로 보내지 않는다.
--   managed_in_dyone = true 인 문서는 그룹웨어 변경을 더 이상 반영하지 않는다(DY ONE 관리 전환).
-- 칸 값(values)은 { 필드 cid: 비교용 문자열 }, 화면 표시(display)는 { 필드 cid: 표시 글자 } (선택지 이름·사용자 이름·참조 문서 제목).
--
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/works_schema.sql  (여러 번 실행해도 안전)

create schema if not exists works;
comment on schema works is 'DY ONE 그룹웨어 앱 저장소 (그룹웨어 WORKS applet 이관, 앱 번호 기준 공통 구조)';

-- 앱 정의 (그룹웨어 그대로)
create table if not exists works.app (
  applet_id     bigint primary key,           -- 그룹웨어 applet id
  name          text   not null,
  layout        jsonb,                         -- 입력 폼 배치 { type, cid, label, count, children[] }
  view_columns  jsonb,                         -- 목록 보기 열(cid 목록, 'status' 포함)
  synced_at     timestamptz
);

-- 필드 정의
create table if not exists works.field (
  applet_id   bigint not null references works.app(applet_id) on delete cascade,
  cid         text   not null,
  label       text   not null,
  field_type  text   not null,                 -- text · textarea · date · datetime · number · select · radio · checkbox · listbox · org · applet_docs · field_mapping · file · creator …
  value_type  text,                            -- STEXT · TEXT · DATE · DATETIME · NUMBER · SELECT · SELECTS · USERS · USER · APPLETDOCS · FILES
  required    bool   not null default false,
  seq         int    not null default 0,
  options     jsonb,                           -- 선택지 [{ code, label, isDefault }]
  props       jsonb,                           -- 참조 앱 번호 등 원본 속성 일부
  editable    bool   not null default false,   -- DY ONE에서 고칠 수 있는 형식인지
  primary key (applet_id, cid)
);

create table if not exists works.status (
  id         bigint primary key,               -- 그룹웨어 status id 그대로
  applet_id  bigint not null references works.app(applet_id) on delete cascade,
  name       text   not null,
  sort       int    not null default 0,
  color      text,
  is_end     bool   not null default false,
  is_begin   bool   not null default false
);

create table if not exists works.transition (
  id           bigint primary key,
  applet_id    bigint not null references works.app(applet_id) on delete cascade,
  from_status  bigint not null references works.status(id),
  to_status    bigint not null references works.status(id),
  action_name  text   not null,
  sort         int    not null default 0
);

-- 문서 (DY ONE 값)
create table if not exists works.doc (
  id                  bigint generated always as identity primary key,
  applet_id           bigint not null references works.app(applet_id),
  gw_doc_id           bigint unique,             -- NULL이면 DY ONE에서 만든 문서
  status_id           bigint references works.status(id),
  "values"            jsonb  not null default '{}',
  display             jsonb  not null default '{}',
  created_by_name     text,
  created_at          timestamptz,
  updated_by_name     text,
  updated_at          timestamptz,
  synced_at           timestamptz,
  is_deleted          bool   not null default false,
  managed_in_dyone    bool   not null default false,
  managed_in_dyone_at timestamptz,
  managed_in_dyone_by text,
  gw_deleted_at       timestamptz
);
create index if not exists ix_works_doc_applet on works.doc (applet_id, created_at desc);
create index if not exists ix_works_doc_status on works.doc (status_id);

-- 마지막으로 읽은 그룹웨어 값 (비교용)
create table if not exists works.doc_gw (
  doc_id         bigint primary key references works.doc(id) on delete cascade,
  gw_doc_id      bigint not null unique,
  status_id      bigint,
  "values"       jsonb  not null,
  display        jsonb  not null,
  gw_updated_at  timestamptz,
  synced_at      timestamptz not null default now()
);

-- 칸별 변경 기록. field = 필드 cid 또는 'status' · 'managed_in_dyone'
create table if not exists works.field_change (
  id           bigint generated always as identity primary key,
  doc_id       bigint not null references works.doc(id) on delete cascade,
  field        text   not null,
  before_value text,
  after_value  text,
  overwritten  text,
  source       text   not null check (source in ('groupware', 'dyone')),
  by_name      text,
  at           timestamptz not null default now()
);
create index if not exists ix_works_field_change_doc on works.field_change (doc_id, at desc);

-- DY ONE에서 한 상태 전환
create table if not exists works.status_history (
  id          bigint generated always as identity primary key,
  doc_id      bigint not null references works.doc(id) on delete cascade,
  from_status bigint,
  to_status   bigint not null,
  action_name text,
  by_name     text,
  at          timestamptz not null default now(),
  note        text
);
create index if not exists ix_works_status_history_doc on works.status_history (doc_id);

-- 활동 글 · 댓글 · 그룹웨어 변경 이력 · 첨부 정보
create table if not exists works.activity (
  id              bigint generated always as identity primary key,
  doc_id          bigint not null references works.doc(id) on delete cascade,
  gw_activity_id  bigint unique,
  body_html       text,
  body_text       text,
  created_by_name text,
  created_at      timestamptz
);
create index if not exists ix_works_activity_doc on works.activity (doc_id);

create table if not exists works.comment (
  id             bigint generated always as identity primary key,
  activity_id    bigint not null references works.activity(id) on delete cascade,
  gw_comment_id  bigint unique,
  body           text,
  author_name    text,
  created_at     timestamptz
);
create index if not exists ix_works_comment_activity on works.comment (activity_id);

create table if not exists works.history (
  id           bigint generated always as identity primary key,
  doc_id       bigint not null references works.doc(id) on delete cascade,
  gw_log_id    bigint unique,
  kind         text   not null,
  title        text,
  detail       text,
  status_from  text,
  status_to    text,
  actor_name   text,
  actor_gw_id  bigint,
  at           timestamptz
);
create index if not exists ix_works_history_doc on works.history (doc_id, at desc);

create table if not exists works.file (
  id            bigint generated always as identity primary key,
  doc_id        bigint not null references works.doc(id) on delete cascade,
  activity_id   bigint references works.activity(id) on delete cascade,
  source        text   not null,               -- doc(파일 필드) · activity(활동 글 첨부)
  gw_attach_id  bigint,
  name          text,
  ext           text,
  size_bytes    bigint,
  gw_path       text,
  gw_host       text,
  stored_path   text,
  created_at    timestamptz,
  unique (source, gw_attach_id)
);
create index if not exists ix_works_file_doc on works.file (doc_id);
