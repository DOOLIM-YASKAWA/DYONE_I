-- 프로젝트 진행 상황: 단계(S1~S9)마다 대기 · 진행 · 완료를 따로 둔다 — 여러 단계가 동시에 진행일 수 있다
-- (예: 모듈라 진행 중 설계 오류로 설계를 다시 진행). 완료 → 진행으로 되돌리면 화면은 '재작업'으로 표시한다.
-- project.project_stage(대표 단계 한 줄)는 목록·홈 표시용으로 API가 함께 맞춘다: 진행 중 가장 뒤 단계, 없으면 완료 중 가장 뒤 단계.
-- 변경 이력은 project.project_stage_history에 단계별로(to_stage_id = 바뀐 단계, from_status → to_status) 남긴다.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_progress.sql  (project_stage_history.sql 다음, 여러 번 실행해도 안전)

create table if not exists project.project_stage_status (
  project_id  bigint not null,                                         -- project.project.id
  stage_id    bigint not null,                                         -- code.code(group_key=project_stage).id
  status      text   not null check (status in ('wait', 'run', 'done')), -- 대기 · 진행 · 완료
  updated_by  bigint,                                                  -- org.employee.id
  updated_at  timestamptz not null default now(),
  primary key (project_id, stage_id)
);
comment on table project.project_stage_status is '프로젝트 단계별 진행 상황(대기·진행·완료, 여러 단계 동시 진행 가능)';

alter table project.project_stage_history add column if not exists from_status text;
alter table project.project_stage_history add column if not exists to_status text;
comment on column project.project_stage_history.to_status is '단계별 진행 상황 변경이면 바뀐 상태(wait·run·done), 대표 단계 이동(예전 방식)이면 null';

-- 이미 대표 단계가 지정된 프로젝트: 그 앞은 완료, 그 단계는 진행, 뒤는 대기로 채운다
insert into project.project_stage_status (project_id, stage_id, status, updated_by, updated_at)
select s.project_id, c.id,
       case when c.sort_order < cur.sort_order then 'done' when c.id = s.stage_id then 'run' else 'wait' end,
       s.updated_by, s.updated_at
from project.project_stage s
join code.code cur on cur.id = s.stage_id
cross join code.code c
where c.group_key = 'project_stage' and c.is_active
  and not exists (select 1 from project.project_stage_status x where x.project_id = s.project_id)
on conflict do nothing;
