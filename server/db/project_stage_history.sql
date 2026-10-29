-- 프로젝트 진행 단계 변경 이력 — 누가 · 언제 · 어떤 단계에서 어떤 단계로 · 메모
-- project.project_stage(현재 단계 한 줄)를 바꿀 때마다 API(PUT /api/projects/{id}/stage)가 같은 트랜잭션으로 한 줄씩 남긴다.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_stage_history.sql  (여러 번 실행해도 안전)

create table if not exists project.project_stage_history (
  id             bigint generated always as identity primary key,
  project_id     bigint not null,                 -- project.project.id
  from_stage_id  bigint,                          -- 바꾸기 전 단계(code.code, project_stage). null = DY ONE에서 처음 지정(그 전엔 그룹웨어 상태 기준)
  to_stage_id    bigint not null,                 -- 바꾼 단계
  changed_by     bigint,                          -- org.employee.id
  changed_at     timestamptz not null default now(),
  note           text                             -- 변경 사유(선택)
);
create index if not exists ix_project_stage_history_project on project.project_stage_history (project_id, changed_at desc);
comment on table project.project_stage_history is '프로젝트 진행 단계 변경 이력(누가·언제·전→후·메모)';

-- 이력 테이블을 만들기 전에 지정된 현재 단계는 '처음 지정' 한 줄로 채운다(그 전 이력은 남아 있지 않음)
insert into project.project_stage_history (project_id, from_stage_id, to_stage_id, changed_by, changed_at, note)
select s.project_id, null, s.stage_id, s.updated_by, s.updated_at, '이력 기록 시작 전 지정'
from project.project_stage s
where not exists (select 1 from project.project_stage_history h where h.project_id = s.project_id);
