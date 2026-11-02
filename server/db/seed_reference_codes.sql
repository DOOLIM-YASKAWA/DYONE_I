-- 운영에 넣는 '기준 코드' 시드 — DY ONE이 동작하는 데 필요한 코드 목록만 담는다(사용자 데이터·테스트 데이터 없음).
-- 운영 데이터 원칙: DEPLOY.md 0장. 로컬 DB를 덤프·복원하거나 테이블 데이터를 복사해 운영에 넣지 않는다.
-- 여러 번 실행해도 안전(있으면 이름·순서·사용 여부만 맞춤). 2026-10-14 로컬 기준값.

-- ---------------------------------------------------------------- 코드 그룹
insert into code.code_group (key, name) values
  ('project_stage', '프로젝트 단계'),
  ('document_category', '문서 분류')
on conflict (key) do update set name = excluded.name;

-- ---------------------------------------------------------------- 진행 단계 (①~⑥, 2026-10-30 project_docs_v2.sql)
-- ① 영업·② 수주는 그룹웨어 상태로 표시, ③~⑥은 DY ONE에서 PM이 관리. 옛 코드(spec·ship·commission)는 지난 이력이 가리켜 사용 안 함으로 둔다.
with v(code, name, sort_order, is_active) as (values
  ('opportunity', '영업',            1, true),
  ('order',       '수주',            2, true),
  ('design',      '설계·검토',       3, true),
  ('modula',      '모듈라',          4, true),
  ('install',     '현장',            5, true),
  ('line',        '라인 대응·종료',  6, true),
  ('spec',        '사양 검토·견적', 90, false),
  ('ship',        '출하',           91, false),
  ('commission',  '시운전·인수',    92, false)
), upd as (
  update code.code c set name = v.name, sort_order = v.sort_order, is_active = v.is_active
  from v where c.group_key = 'project_stage' and c.code = v.code
  returning c.code
)
insert into code.code (group_key, code, name, sort_order, is_active)
select 'project_stage', v.code, v.name, v.sort_order, v.is_active from v
where not exists (select 1 from code.code c where c.group_key = 'project_stage' and c.code = v.code);

-- ---------------------------------------------------------------- 문서 분류(자료 종류) = NAS 프로젝트 폴더 13칸 + 현장 백업 (2026-10-30)
-- 13칸에 없는 옛 코드(contract·robot_sim·electric·photo·line)는 project_docs_v2.sql이 사용 안 함으로 둔다
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
