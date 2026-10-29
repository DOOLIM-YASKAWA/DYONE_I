-- 진행 단계 9 → 8 (2026-10-14): 사양 검토·견적을 '영업'에 합치고, 영업·수주는 그룹웨어 상태를 따른다.
--   S1 영업(opportunity) · S2 수주(order)        ← 그룹웨어 상태(영업중·수주·마감·중단)로 자동 표시, DY ONE에서 바꾸지 않음
--   S3 설계·구매 · S4 모듈라·OLT · S5 출하 · S6 현장 설치 · S7 시운전·인수 · S8 라인 대응·종료  ← DY ONE에서 대기·진행·완료로 직접 관리
-- 사양 검토·견적(spec)은 지우지 않고 사용 안 함으로 둔다(지난 이력이 이 코드를 가리킴). 그 단계로 올린 파일은 '영업'으로 옮긴다.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_stage_v2.sql  (project_progress.sql 다음, 여러 번 실행해도 안전)

update code.code set name = '영업', sort_order = 1 where group_key = 'project_stage' and code = 'opportunity';
update code.code set name = '수주', sort_order = 2 where group_key = 'project_stage' and code = 'order';
update code.code set is_active = false, sort_order = 90 where group_key = 'project_stage' and code = 'spec';
update code.code set sort_order = 3 where group_key = 'project_stage' and code = 'design';
update code.code set sort_order = 4 where group_key = 'project_stage' and code = 'modula';
update code.code set sort_order = 5 where group_key = 'project_stage' and code = 'ship';
update code.code set sort_order = 6 where group_key = 'project_stage' and code = 'install';
update code.code set sort_order = 7 where group_key = 'project_stage' and code = 'commission';
update code.code set sort_order = 8 where group_key = 'project_stage' and code = 'line';

-- 사양 검토·견적 단계로 올린 파일 → 영업
update project.project_file set stage_id = (select id from code.code where group_key = 'project_stage' and code = 'opportunity')
where stage_id = (select id from code.code where group_key = 'project_stage' and code = 'spec');

-- 영업·수주·(옛)사양 검토는 이제 그룹웨어 상태로 계산하므로 DY ONE 저장값을 지운다(이력은 남김)
delete from project.project_stage_status
where stage_id in (select id from code.code where group_key = 'project_stage' and code in ('opportunity', 'spec', 'order'));
delete from project.project_stage
where stage_id in (select id from code.code where group_key = 'project_stage' and code in ('opportunity', 'spec', 'order'));

-- 그룹웨어 프로젝트 상태 변경(영업중 → 수주 등)도 진행 상황 이력에 남긴다: Worker(GwProjectSyncJob)가 매일 동기화 때 바뀐 프로젝트마다 한 줄.
--   source = 'groupware', gw_from/gw_to = 그룹웨어 상태 이름, to_stage_id 없음. DY ONE 화면에서 바꾼 줄은 source = 'dyone'.
alter table project.project_stage_history add column if not exists source text not null default 'dyone';
alter table project.project_stage_history add column if not exists gw_from text;
alter table project.project_stage_history add column if not exists gw_to text;
alter table project.project_stage_history alter column to_stage_id drop not null;
comment on column project.project_stage_history.source is 'dyone(화면에서 변경) · groupware(그룹웨어 상태 동기화)';
