-- project.project_file — DY ONE 프로젝트별 업로드 문서(사양서/설계/모듈라/라인 대응 등).
-- 실물 파일은 파일 저장소(운영=NAS UNC, 개발=로컬 폴더; FileStorage:Root)에 저장하고, 여기엔 메타데이터만 둔다.
-- 분류(category_id)는 code.code(group_key='document_category') — 코드만 추가/수정하면 분류가 바뀐다(코드 배포 불필요).
-- 쓰기 주체: DY ONE API(사용자 업로드 전용). 그룹웨어/ERP 미러와 무관한 dyone 자체 테이블.
create table if not exists project.project_file (
  id            bigint generated always as identity primary key,
  project_id    bigint not null,
  category_id   bigint,            -- code.code(group_key=document_category)
  stage_id      bigint,            -- code.code(group_key=project_stage), 선택(업로드 당시 단계)
  file_name     text   not null,   -- 원본 파일명(표시·다운로드)
  stored_name   text   not null,   -- 저장소 Root 기준 상대경로(projects/<코드 이름>/<분류>/<원본파일명>). 사람이 탐색기로도 찾게.
  content_type  text,
  size_bytes    bigint not null default 0,
  description   text,
  uploaded_by   bigint,            -- org.employee.id
  department_id bigint,            -- 업로더 소속 부서(org.department.id) 스냅샷. 앱에서 부서별 검색용.
  uploaded_at   timestamptz not null default now(),
  is_active     boolean not null default true
);
-- 이미 테이블이 있던 환경을 위한 멱등 컬럼 추가
alter table project.project_file add column if not exists department_id bigint;

create index if not exists project_file_project_idx on project.project_file (project_id) where is_active;

-- 문서 분류 코드(그룹). 이름만 둔다.
insert into code.code_group (key, name)
  values ('document_category', '문서 분류')
  on conflict (key) do nothing;

-- 분류 항목. (group_key, code)에 유니크 제약이 없어 NOT EXISTS로 멱등 처리.
insert into code.code (group_key, code, name, sort_order, is_active)
select v.code_group, v.code, v.name, v.sort_order, true
from (values
  ('document_category','spec','사양서',0),
  ('document_category','quote','견적',1),
  ('document_category','design','설계',2),
  ('document_category','modula','모듈라',3),
  ('document_category','line','라인 대응',4),
  ('document_category','report','보고·회의록',5),
  ('document_category','etc','기타',9)
) as v(code_group, code, name, sort_order)
where not exists (
  select 1 from code.code c where c.group_key = v.code_group and c.code = v.code
);
