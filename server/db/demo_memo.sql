-- [임시 · 데모 기간] 화면 메모(포스트잇): 화면마다 궁금한 점을 붙여 두고 나중에 팀원에게 묻기 위한 메모.
-- 정식 기능이 아니다. 데모가 끝나면 이 스키마와 API(Modules/Demo)·화면(components/DemoMemos.tsx)을 함께 지운다.
-- 위치: x = 본문(main) 너비 대비 %(0~100), y = 본문 맨 위에서 px. 화면 크기가 달라도 대략 같은 자리에 보이게.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/demo_memo.sql  (여러 번 실행해도 안전, 운영에는 적용하지 않는다)

create schema if not exists demo;

create table if not exists demo.memo (
  id          bigint generated always as identity primary key,
  path        text   not null,                  -- 화면 경로(예: /projects/6256)
  screen      text,                             -- 화면 이름(메뉴 이름, 목록 표시용)
  x_pct       numeric(5,2) not null default 50,
  y_px        integer not null default 0,
  body        text   not null,
  resolved    boolean not null default false,   -- 팀원에게 물어보고 답을 들었으면 해결
  answer      text,                             -- 들은 답(선택)
  created_by  bigint,                           -- org.employee.id
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);
create index if not exists ix_demo_memo_path on demo.memo (path);
comment on table demo.memo is '[임시] 데모 화면 메모(포스트잇) — 데모 종료 시 삭제';
