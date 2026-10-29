-- 프로젝트 인원(2026-10-15): 프로젝트마다 PM 1명 + 투입 인원 여러 명. 사람은 조직도(org.employee, 그룹웨어 미러)에서 고른다.
--   role = pm(프로젝트 매니저, 프로젝트당 1명) · member(투입 인원). task = 맡은 일(설계·모듈라·시운전 등, 자유 입력).
--   빼거나 PM을 바꿔도 지우지 않고 is_active = false + removed_at으로 남긴다(누가 언제 맡았는지 이력).
--   PM을 다른 사람으로 바꾸면 기존 PM은 투입 인원(member)으로 남는다 — 필요 없으면 화면에서 뺀다.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_member.sql  (여러 번 실행해도 안전)

create table if not exists project.project_member (
  id           bigint generated always as identity primary key,
  project_id   bigint not null references project.project(id) on delete cascade,
  employee_id  bigint not null references org.employee(id),
  role         text   not null check (role in ('pm', 'member')),
  task         text,
  is_active    boolean not null default true,
  assigned_by  bigint,                                  -- org.employee.id
  assigned_at  timestamptz not null default now(),
  updated_at   timestamptz not null default now(),
  removed_by   bigint,                                  -- org.employee.id
  removed_at   timestamptz
);
comment on table project.project_member is '프로젝트 인원(PM · 투입 인원). 빠진 사람은 is_active = false로 이력만 남긴다';
comment on column project.project_member.role is 'pm(프로젝트 매니저, 프로젝트당 1명) · member(투입 인원)';
comment on column project.project_member.task is '맡은 일(자유 입력)';

create unique index if not exists ux_project_member_pm on project.project_member (project_id) where is_active and role = 'pm';
create unique index if not exists ux_project_member_once on project.project_member (project_id, employee_id) where is_active;
create index if not exists ix_project_member_employee on project.project_member (employee_id) where is_active;
