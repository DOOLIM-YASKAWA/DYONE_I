-- 협력사(2026-10-30, docs/partner-access): 그룹웨어 회사 공용 주소록(tims go_contact_books id 15)의 미러 + 회사별 공용 비밀번호.
--   partner.company = 주소록 그룹(협력사 회사 하나), partner.person = 그 그룹의 연락처. id는 그룹웨어 id 그대로.
--   이름·이메일·소속 같은 명단 칸은 Worker(PartnerSyncJob)만 쓴다. 비밀번호 해시·로그인 중지는 API 관리자 화면만 쓴다.
--   주소록에서 빠지면 is_active = false → 로그인·조회 즉시 정지(지우지 않고 남긴다).
--   우리 직원 메일(doolim-yaskawa.com)이거나 그룹웨어 계정과 겹치는 연락처는 미러하지 않는다.
-- 프로젝트 배정: project.project_member에 협력사 칸(partner_person_id)을 더한다. 한 행은 직원 또는 협력사 중 하나.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/partner.sql  (여러 번 실행해도 안전)
-- 데이터를 넣는 문장은 없다(명단은 Worker가 채우고, 비밀번호는 운영에서 관리자 화면으로 넣는다).

create schema if not exists partner;

create table if not exists partner.company (
  id                  bigint primary key,                    -- 그룹웨어 go_contact_groups.id
  name                text   not null,
  is_active           boolean not null default true,         -- 주소록에서 그룹이 빠지면 false
  password_hash       text,                                  -- 회사 공용 비밀번호 해시(ASP.NET PasswordHasher). null = 로그인 불가
  password_changed_at timestamptz,
  password_changed_by bigint,                                -- org.employee.id(관리자)
  login_disabled      boolean not null default false,        -- 회사 단위 로그인 중지
  synced_at           timestamptz not null default now(),
  updated_at          timestamptz not null default now()
);
comment on table partner.company is '협력사 회사(그룹웨어 회사 공용 주소록의 그룹). 명단은 Worker 미러, 비밀번호·중지는 관리자 화면';
comment on column partner.company.password_hash is '회사 공용 비밀번호 해시. 원문은 어디에도 저장하지 않는다';

create table if not exists partner.person (
  id              bigint primary key,                        -- 그룹웨어 go_contacts.id
  company_id      bigint not null references partner.company(id),
  name            text   not null,
  email           text   not null,                           -- 로그인 아이디
  department_name text,                                      -- 주소록 기재(자유 텍스트)
  position_name   text,
  is_active       boolean not null default true,             -- 주소록에서 빠지면 false
  last_login_at   timestamptz,
  synced_at       timestamptz not null default now(),
  updated_at      timestamptz not null default now()
);
comment on table partner.person is '협력사 사람(그룹웨어 회사 공용 주소록 연락처). 아이디 = 이메일';

create unique index if not exists ux_partner_person_email on partner.person (lower(email)) where is_active;
create index if not exists ix_partner_person_company on partner.person (company_id);

-- 프로젝트 배정: 직원(employee_id) 또는 협력사(partner_person_id) 중 하나
alter table project.project_member add column if not exists partner_person_id bigint references partner.person(id);
alter table project.project_member alter column employee_id drop not null;
do $$ begin
  alter table project.project_member add constraint ck_project_member_who
    check ((employee_id is null) <> (partner_person_id is null));
exception when duplicate_object then null; end $$;
comment on column project.project_member.partner_person_id is '협력사 사람(partner.person.id). employee_id와 둘 중 하나만';
create unique index if not exists ux_project_member_partner_once on project.project_member (project_id, partner_person_id) where is_active and partner_person_id is not null;
create index if not exists ix_project_member_partner on project.project_member (partner_person_id) where is_active;
