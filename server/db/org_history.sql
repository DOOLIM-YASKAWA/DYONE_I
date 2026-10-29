-- 소속·부서 이력(2026-10-28, 부서 자동 부여 P1): 조직 개편 때 역할 ↔ 부서 연결 후보를 '사람이 옮겨 간 방향'으로 내기 위한 근거.
--   그룹웨어는 부서를 지우면 소속(go_dept_members) 행도 지워 과거 소속이 남지 않는다. 그래서 DY ONE이 직접 쌓는다.
--   org.employee·org.department(SmartLab·SRS와 같이 쓰는 표)는 건드리지 않고 따로 둔다. Worker OrgSyncJob이 매일 채운다(표가 없으면 건너뜀).
--
--   source
--     sync     : 조직도 동기화가 날마다 비교해 기록(정확). 처음 실행한 날부터.
--     approval : 그 이전을 결재 문서의 기안 당시 부서로 채운 근사 이력(사람·월별 가장 많이 기안한 부서). 처음 한 번만.
--     gw       : 그룹웨어 go_departments의 생성·삭제일(삭제된 부서 포함, 이름은 마지막 이름). 처음 한 번만.
--     approval : (부서) 같은 id의 이름 변경을 결재 기안 부서 이름으로 채운 근사 이력. 그룹웨어는 개편 때 새 id보다 같은 id 이름 변경이 더 많다
--                (도장사업부→도장영업실, 자동화사업부→자동화영업실, 기술1부→공정품질실, 영호남지사→영업사업부). 처음 한 번만.
--   기간은 양끝 포함(valid_from ~ valid_to), valid_to null = 지금도 유효. 부서 id = 그룹웨어 id(삭제된 부서는 org.department에 없을 수 있어 FK 없음).
--
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/org_history.sql  (여러 번 실행해도 안전)

create table if not exists org.employee_dept_history (
  id               bigint generated always as identity primary key,
  employee_id      bigint not null,     -- 그룹웨어 사용자 id(= org.employee.id, 퇴직자 포함)
  department_id    bigint,              -- 그룹웨어 부서 id
  department_name  text,                -- 그때 부서 이름(스냅샷)
  duty_id          bigint,              -- 그때 직책(org.duty.id)
  valid_from       date   not null,
  valid_to         date,
  source           text   not null check (source in ('sync', 'approval')),
  created_at       timestamptz not null default now(),
  check (valid_to is null or valid_to >= valid_from)
);
comment on table org.employee_dept_history is '직원 소속 이력 — sync(동기화 비교) · approval(결재 기안 부서 근사)';
create index if not exists ix_emp_dept_hist_emp on org.employee_dept_history (employee_id, valid_from);
create index if not exists ix_emp_dept_hist_dept on org.employee_dept_history (department_id, valid_from);
create unique index if not exists ux_emp_dept_hist_open on org.employee_dept_history (employee_id) where valid_to is null and source = 'sync';

create table if not exists org.department_history (
  id             bigint generated always as identity primary key,
  department_id  bigint not null,       -- 그룹웨어 부서 id
  name           text   not null,
  parent_id      bigint,
  code           text,
  valid_from     date   not null,
  valid_to       date,                  -- sync 행의 마지막 valid_to = 동기화에서 빠진(없어진) 전날
  source         text   not null check (source in ('sync', 'gw', 'approval')),
  created_at     timestamptz not null default now(),
  check (valid_to is null or valid_to >= valid_from)
);
comment on table org.department_history is '부서 이름·상위 부서 이력 — sync(동기화 비교) · gw(그룹웨어 생성·삭제일) · approval(결재 기안 부서 이름)';
create index if not exists ix_dept_hist_dept on org.department_history (department_id, valid_from);
create unique index if not exists ux_dept_hist_open on org.department_history (department_id) where valid_to is null and source = 'sync';
