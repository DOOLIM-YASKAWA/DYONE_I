-- 앱(화면)별 사용 부서·사람 — DY ONE이 직접 관리하는 권한 기준(조직도 org.*는 그룹웨어에서 동기화되므로 따로 둔다)
-- 지금은 그룹웨어 변경 알림의 받는 사람을 정하는 데 쓰고, 부서별 메뉴(⑤ 권한 단계)도 이 표를 기준으로 붙인다.
--
-- app_key: 화면 단위 키. 그룹웨어 변경 알림의 sync.change_notice.entity와 같다 (modular · works:281 …)
-- 규칙: 받는 사람 = 그 앱을 쓰는 부서(하위 부서 포함 선택) 직원 + 그 앱에 따로 지정한 사람 + 모든 앱('*')에 지정한 사람.
--       부서를 정하지 않은 앱은 '*' 지정자만 받는다(부서 권한을 줄 때 app_department에 넣는다).
--
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/app_access.sql  (여러 번 실행해도 안전)

create schema if not exists access;

create table if not exists access.app_department (
  app_key        text    not null,
  department_id  bigint  not null,              -- org.department.id
  include_sub    boolean not null default true, -- 하위 부서 직원도 포함
  note           text,
  created_at     timestamptz not null default now(),
  primary key (app_key, department_id)
);
comment on table access.app_department is '앱(화면)을 쓰는 부서 — 알림 받는 사람·부서별 메뉴 기준';

create table if not exists access.app_employee (
  app_key      text   not null,
  employee_id  bigint not null,                 -- org.employee.id (부서와 상관없이 따로 지정: 개발·관리자·겸직 등). app_key='*'는 모든 앱
  note         text,
  created_at   timestamptz not null default now(),
  primary key (app_key, employee_id)
);
comment on table access.app_employee is '앱(화면)을 부서와 상관없이 쓰는 사람';

-- 초기 값: 모듈라 = 모듈라팀, ES 이슈 = ES영업실(하위 ES영업팀·CS팀), 모든 앱 = 함유진(DY ONE 개발).
-- 운영 반영 시 부서·직원 id를 운영 조직도와 맞춰 확인한다(DEPLOY-PENDING). 운영 반영 시 부서 id를 운영 조직도와 맞춰 확인한다(DEPLOY-PENDING).
insert into access.app_department (app_key, department_id, note)
select 'modular', id, '모듈라팀' from org.department where name = '모듈라팀' and is_active
on conflict do nothing;

insert into access.app_department (app_key, department_id, note)
select 'works:281', id, 'ES영업실' from org.department where name = 'ES영업실' and is_active
on conflict do nothing;

insert into access.app_employee (app_key, employee_id, note)
select '*', id, 'DY ONE 개발 — 모든 앱 알림' from org.employee where login_id = 'yujin' and is_active
on conflict do nothing;
