-- 직원 연락처(2026-10-15): 그룹웨어 프로필(go_user_profiles)의 휴대폰·직통 번호. 조직도·프로젝트 인원 화면에 표시.
--   org.employee(SmartLab·SRS와 같이 쓰는 표)는 건드리지 않고 따로 둔다. Worker OrgSyncJob이 매일 조직도와 함께 채운다(표가 없으면 건너뜀).
--   번호는 그룹웨어에 적힌 그대로 저장(대부분 010-0000-0000). 퇴직자는 org.employee.is_active로 걸러진다.
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/org_contact.sql  (여러 번 실행해도 안전)

create table if not exists org.employee_contact (
  employee_id  bigint primary key references org.employee(id) on delete cascade,
  mobile       text,     -- 휴대폰(go_user_profiles.mobile_no)
  direct_tel   text,     -- 회사 직통(go_user_profiles.direct_tel)
  updated_at   timestamptz not null default now()
);
comment on table org.employee_contact is '직원 연락처(그룹웨어 프로필 미러, Worker 조직도 동기화가 채움)';
