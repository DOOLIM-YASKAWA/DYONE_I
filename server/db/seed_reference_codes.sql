-- 운영에 넣는 '기준 코드' 시드 — DY ONE이 동작하는 데 필요한 코드 목록만 담는다(사용자 데이터·테스트 데이터 없음).
-- 운영 데이터 원칙: DEPLOY.md 0장. 로컬 DB를 덤프·복원하거나 테이블 데이터를 복사해 운영에 넣지 않는다.
-- 여러 번 실행해도 안전(있으면 이름·순서·사용 여부만 맞춤). 2026-10-14 로컬 기준값.

-- ---------------------------------------------------------------- 코드 그룹
insert into code.code_group (key, name) values
  ('project_stage', '프로젝트 단계'),
  ('document_category', '문서 분류')
on conflict (key) do update set name = excluded.name;

-- ---------------------------------------------------------------- 진행 단계 (S1~S8)
-- S1 영업·S2 수주는 그룹웨어 상태로 표시, S3~S8은 DY ONE에서 관리. spec(사양 검토·견적)은 옛 코드라 사용 안 함으로 둔다.
with v(code, name, sort_order, is_active) as (values
  ('opportunity', '영업',            1, true),
  ('order',       '수주',            2, true),
  ('design',      '설계·구매',       3, true),
  ('modula',      '모듈라·OLT',      4, true),
  ('ship',        '출하',            5, true),
  ('install',     '현장 설치',       6, true),
  ('commission',  '시운전·인수',     7, true),
  ('line',        '라인 대응·종료',  8, true),
  ('spec',        '사양 검토·견적', 90, false)
), upd as (
  update code.code c set name = v.name, sort_order = v.sort_order, is_active = v.is_active
  from v where c.group_key = 'project_stage' and c.code = v.code
  returning c.code
)
insert into code.code (group_key, code, name, sort_order, is_active)
select 'project_stage', v.code, v.name, v.sort_order, v.is_active from v
where not exists (select 1 from code.code c where c.group_key = 'project_stage' and c.code = v.code);

-- ---------------------------------------------------------------- 문서 분류
with v(code, name, sort_order) as (values
  ('spec',   '사양서',      0),
  ('quote',  '견적',        1),
  ('design', '설계',        2),
  ('modula', '모듈라',      3),
  ('line',   '라인 대응',   4),
  ('report', '보고·회의록', 5),
  ('etc',    '기타',        9)
), upd as (
  update code.code c set name = v.name, sort_order = v.sort_order, is_active = true
  from v where c.group_key = 'document_category' and c.code = v.code
  returning c.code
)
insert into code.code (group_key, code, name, sort_order, is_active)
select 'document_category', v.code, v.name, v.sort_order, true from v
where not exists (select 1 from code.code c where c.group_key = 'document_category' and c.code = v.code);
