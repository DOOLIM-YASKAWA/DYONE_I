-- 그룹웨어 변경 알림 대기열 (Worker → API)
-- Worker(GwModularSyncJob · GwWorksSyncJob)가 그룹웨어에서 바뀐 것을 DY ONE에 반영하면 한 줄씩 남기고,
-- API(GwChangeNoticeRelay)가 몇 초마다 새 줄을 읽어 실시간 알림(SignalR → 화면 토스트 · 알림함)으로 보낸다.
-- 최근 30일은 알림함(놓친 알림 보기)으로도 쓴다. 30일 지난 줄은 Worker가 지운다. 받는 사람은 access.app_department · app_employee(app_access.sql)로 정한다.
--
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/sync_change_notice.sql  (여러 번 실행해도 안전)

create table if not exists sync.change_notice (
  id          bigint generated always as identity primary key,
  source      text   not null,            -- groupware
  entity      text   not null,            -- modular · works:281 …
  doc_id      bigint,                     -- DY ONE 문서 id (modular.build.id · works.doc.id)
  kind        text   not null,            -- changed · created · deleted
  title       text   not null,
  body        text   not null,
  link        text,                       -- 화면 경로 (예: /es?doc=73)
  created_at  timestamptz not null default now()
);
create index if not exists ix_sync_change_notice_created on sync.change_notice (created_at);
comment on table sync.change_notice is '그룹웨어 변경 알림 대기열 — Worker가 쓰고 API가 실시간 알림으로 보낸다';

-- 놓친 알림 보기(알림함): 위 대기열을 최근 30일 알림함으로도 쓴다. 사람별 읽음 표시.
-- 한 건씩 읽음 + "모두 읽음"(이 번호까지 읽음) 두 가지. API가 ConnectionStrings:DyOneWrite로 쓴다.
create table if not exists sync.change_notice_read (
  employee_id  bigint not null,                -- org.employee.id
  notice_id    bigint not null references sync.change_notice (id) on delete cascade,
  read_at      timestamptz not null default now(),
  primary key (employee_id, notice_id)
);
create table if not exists sync.change_notice_seen (
  employee_id  bigint primary key,             -- org.employee.id
  read_upto    bigint not null,                -- 이 번호까지 모두 읽음
  read_at      timestamptz not null default now()
);
