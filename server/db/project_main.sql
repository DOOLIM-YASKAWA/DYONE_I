-- 프로젝트 메인 화면(2026-11-04, 1차 오픈): ERP 미러 · 흔적 집계 · ★ 관심 · 회사 주요(후보·선정) · 오늘의 브리핑.
--   설계: docs/roadmap/project-main-design.md (2절 표 구조, 2-5절 매일 흐름, 3절 점수).
--   쓰는 쪽: sync.erp_project·sync.project_trace·project.featured_suggestion·project.status_brief = Worker(project-main 잡, 매일 06:30)
--            project.favorite·project.featured = API(★ 누르기, 영업 부서·관리자의 선정)
--            project.project_alias = 이 스크립트(기준 연결) + 관리자
-- 적용: docker exec -i dyone_db psql -U postgres -d dyone < server/db/project_main.sql  (여러 번 실행해도 안전)
-- 운영: 이 스크립트를 그대로 실행한다(표 + 기준 연결만, 시험 데이터 없음).

-- ── ERP 미러 (Worker만 씀. ERP NEOE는 dylab 읽기 전용) ──────────────────────
create table if not exists sync.erp_project (
  cd_prjt          text primary key,                 -- DLP-26-119
  status_code      text,                             -- WS_B_BSNSS_MAIN.WS_CM00008: 001 영업중 · 002 수주 · 003 마감(재무 월마감) · 004 중단
  status_name      text,
  name             text,                             -- SA_PROJECTH.NM_PROJECT
  partner_code     text,                             -- SA_PROJECTH.CD_PARTNER
  partner_name     text,                             -- MA_PARTNER.LN_PARTNER
  pm_name          text,                             -- WS_B_BSNSS_MAIN.NM_EMP_PM (ERP에는 이름만 있다)
  cnst_from        date,                             -- DT_FM_CNST
  cnst_to          date,                             -- DT_TO_CNST (공사 종료)
  contract_amount  numeric,                          -- SA_PROJECTH.AM_WONAMT
  project_end      date,                             -- SA_PROJECTH.ED_PROJECT
  dt_close         date,                             -- SA_PROJECTH.DT_CLOSE (재무 월마감일)
  erp_updated_at   text,                             -- 두 원천 DTS_UPDATE 중 큰 값(yyyyMMddHHmmss) — 다음 증분 기준
  synced_at        timestamptz not null default now()
);
comment on table sync.erp_project is 'ERP 프로젝트 미러: 영업 관리(WS_B_BSNSS_MAIN, 프로젝트당 DTS_UPDATE 최신 1줄) + 프로젝트 헤더(SA_PROJECTH). 매일 바뀐 줄만';

-- ── 흔적 집계 (Worker만 씀, 매일 전체 다시 계산) ──────────────────────────
create table if not exists sync.project_trace (
  code          text primary key,                    -- 프로젝트 코드(옛 코드도 그대로 한 줄, 합산은 점수 계산 때 project_alias로)
  po_30d        int not null default 0,              -- ERP 발주(PU_POH 번호) 최근 30일
  po_90d        int not null default 0,
  po_total      int not null default 0,
  last_po_date  date,                                -- 오늘 이후 날짜(입력 오류)는 제외
  nas_30d       int not null default 0,              -- NAS 작업 폴더(2~9) 최근 30일 수정 파일 수(사진·동영상·소음 파일 제외, v3)
  nas_14d       int not null default 0,
  nas_last_at   timestamptz,
  nas_dir       text,                                -- 찾은 폴더(연도\분류\폴더명)
  nas_top       jsonb,                               -- 최근 파일이 많은 하위 폴더 [{"dir":"4. 도면","n":120}]
  nas_recent    jsonb,                               -- 최근 파일 이름 몇 개 [{"name":"…","dir":"…","at":"…"}] (내용은 열지 않음)
  act_30d       int not null default 0,              -- 모듈라·ES 기록(이력·활동글·댓글·파일) 최근 30일
  act_last_at   timestamptz,
  computed_at   timestamptz not null default now()
);
alter table sync.project_trace add column if not exists nas_1d int not null default 0;   -- 어제 수정 파일(브리핑 '어제 +n')
alter table sync.project_trace add column if not exists act_1d int not null default 0;   -- 어제 모듈라·ES 기록
-- v3(11-04): 발주 연속성 · NAS 작업 패턴(작업 폴더 2~9만, 사진·동영상 제외)
alter table sync.project_trace add column if not exists po_31_60d int not null default 0;  -- 발주 31~60일 전
alter table sync.project_trace add column if not exists po_61_90d int not null default 0;  -- 발주 61~90일 전
alter table sync.project_trace add column if not exists nas_days_30 int not null default 0; -- 최근 30일 중 파일이 바뀐 날 수
alter table sync.project_trace add column if not exists nas_days_14 int not null default 0; -- 최근 14일 중 파일이 바뀐 날 수
alter table sync.project_trace add column if not exists nas_top_share numeric not null default 0; -- 가장 많이 바뀐 하루의 비율(한 번에 올림 판정)
-- 근거 화면 흐름(11-05): 합계만으로는 '언제 무엇을'을 쓸 수 없어 Worker가 계산하며 보던 내용을 남긴다
alter table sync.project_trace add column if not exists nas_flow jsonb;   -- 최근 30일 [{d:날짜, dir:작업 폴더 3단, n:수정 파일 수, files:[대표 이름 ≤3]}] 새 날짜부터 ≤60칸
alter table sync.project_trace add column if not exists po_recent jsonb;  -- 최근 90일 발주서 [{d:발주일, partner:거래처, items:[품목 이름 ≤5], lines:품목 줄 수}] 새 것부터 ≤40건, 금액 없음
comment on table sync.project_trace is '프로젝트별 실제 작업 흔적: ERP 발주 · NAS 최근 수정 파일 · 모듈라/ES 기록. 회사 주요 점수와 브리핑 입력';

-- ── 옛 코드 → 새 코드 ────────────────────────────────────────────────────
create table if not exists project.project_alias (
  old_code      text not null,
  new_code      text not null,
  source        text not null default 'manual' check (source in ('erp', 'manual')),
  note          text,
  confirmed_by  bigint,                               -- org.employee.id (기준 연결은 null)
  confirmed_at  timestamptz not null default now(),
  primary key (old_code, new_code)
);
comment on table project.project_alias is '옛 코드 → 새 코드(ERP에서 중단하고 나눈 코드 등). 옛 코드의 발주·NAS·모듈라 흔적을 새 코드에 합산한다';

-- 11-04 확인한 연결(ERP 중단 → 새 코드로 나눔 / 같은 일 이어받은 코드). 영업 확인 목록 E시트와 같음.
insert into project.project_alias (old_code, new_code, source, note) values
  ('DLP-26-033', 'DLP-26-119', 'erp', '화성5 신기술: 033 중단 → 119~122로 나눔'),
  ('DLP-26-033', 'DLP-26-120', 'erp', '화성5 신기술: 033 중단 → 119~122로 나눔'),
  ('DLP-26-033', 'DLP-26-121', 'erp', '화성5 신기술: 033 중단 → 119~122로 나눔'),
  ('DLP-26-033', 'DLP-26-122', 'erp', '화성5 신기술: 033 중단 → 119~122로 나눔'),
  ('DLP-24-111', 'DLP-25-005', 'manual', '추정(영업 확인 전): 화성4 샌딩 폴리싱, 다음 해 새 코드'),
  ('DLP-25-235', 'DLP-25-229', 'manual', '추정(영업 확인 전): GMK 부평1 베이스 외판 중복 등록'),
  ('DLP-24-285', 'DLP-24-234', 'manual', '추정(영업 확인 전): KCN 중상도 내판 2차 → 1차로 합침'),
  ('DLP-24-286', 'DLP-24-234', 'manual', '추정(영업 확인 전): KCN 중상도 내판 3차 → 1차로 합침'),
  ('DLP-24-056', 'DLP-25-025', 'manual', '추정(영업 확인 전): 현대 전주 상도 → 상도2부스')
on conflict do nothing;

-- ── ★ 내 관심 (API가 씀) ─────────────────────────────────────────────────
create table if not exists project.favorite (
  employee_id  bigint not null references org.employee(id),
  project_id   bigint not null references project.project(id) on delete cascade,
  sort         int,
  created_at   timestamptz not null default now(),
  primary key (employee_id, project_id)
);
create index if not exists ix_favorite_project on project.favorite (project_id);
comment on table project.favorite is '내 관심 프로젝트(★). 본인이 누른 것만 — PM·투입 프로젝트를 자동으로 넣지 않는다';

-- ── 회사 주요: 매일 후보 (Worker가 씀) ───────────────────────────────────
create table if not exists project.featured_suggestion (
  run_date     date not null,
  code         text not null,                         -- 묶음의 대표 코드(옛 코드는 새 코드로 합산)
  codes        text[] not null,                       -- 묶음에 든 코드 전부
  project_id   bigint,                                -- project.project(그룹웨어 미러)에 있으면
  name         text not null,
  rank         int not null,
  score        int not null,                          -- rule_score + trace_score
  rule_score   int not null,                          -- 규모: 계약·로봇·신공장·해외·종료 임박·소규모
  trace_score  int not null,                          -- 흔적: 발주·NAS·모듈라/ES
  category     text not null,                         -- 도장 · 비도장 · 일반 산업
  category_basis text,                                -- 모듈라 공정 · PM 부서 · 이름으로 추정
  parts        jsonb not null,                        -- [{"key":"amount","pts":5,"label":"계약 128억"}] 숫자는 DB 값
  reason       text,                                  -- 사내 AI 문장(숫자 없음). 없으면 화면이 parts로 만든다
  signals      jsonb,                                 -- 사내 AI 보조 꼬리표 ["일정 압박","사양 변경"] (점수는 안 바꿈)
  created_at   timestamptz not null default now(),
  primary key (run_date, code)
);
comment on table project.featured_suggestion is '회사 주요 후보(매일 규칙 점수 + 사내 AI 이유). 영업 부서가 이 중에서 고른다';

-- ── 회사 주요: 선정 (API가 씀, 이력 보존) ────────────────────────────────
create table if not exists project.featured (
  id            bigint generated always as identity primary key,
  code          text not null,
  rank          int,
  status        text not null check (status in ('selected', 'excluded')),
  note          text,
  decided_by    bigint not null,                      -- org.employee.id
  decided_at    timestamptz not null default now(),
  ended_by      bigint,
  ended_at      timestamptz
);
create unique index if not exists ux_featured_open on project.featured (code) where ended_at is null;
comment on table project.featured is '회사 주요 선정·제외(영업 부서·관리자). 해제는 지우지 않고 ended_at으로 닫는다. 선정이 하나도 없으면 화면은 시스템 추천 상위 3건을 "추천"으로 보여 준다';

-- ── 오늘의 브리핑: 프로젝트별 지금 상태 한 줄 (Worker가 씀) ─────────────
create table if not exists project.status_brief (
  code               text not null,
  made_at            timestamptz not null default now(),
  facts              jsonb not null,                  -- 입력 사실(지금 어디·최근 무엇·다음 무엇). 숫자·날짜는 DB 값
  facts_hash         text not null,                   -- 입력이 안 바뀌면 다시 만들지 않는다
  sentence           text not null,
  source             text not null check (source in ('ai', 'rule')),  -- rule = AI가 없거나 검사에 걸려 규칙 문장으로 대신함
  changed_yesterday  int not null default 0,
  evidence           jsonb,                           -- [{"kind":"nas","label":"4. 도면 12개"},{"kind":"modular","label":"…"}]
  primary key (code, made_at)
);
create index if not exists ix_status_brief_latest on project.status_brief (code, made_at desc);
comment on table project.status_brief is '오늘의 프로젝트 브리핑: ★ ∪ 회사 주요 프로젝트의 지금 상태 한 줄(사내 AI, 숫자·날짜 검사 후 저장). 최신 1행 + 이력';

-- ── 일정 주의 (11-04 승인) ───────────────────────────────────────────────
-- 사람이 올리거나 저장한 사양정리·수행계획서·사양서(NAS 2·3칸, 운영 뒤 DY ONE 자료함)에서 읽은 일정 → 지금 상태와 비교 → 꼬리표.
-- 근거: docs/roadmap/회사 주요 프로젝트 선정 기준 v3 (2026-11-04).md 10절. 화면: 관심 | 주요 | 일정 주의(B안)

create table if not exists project.schedule_milestone (            -- 문서에서 읽은 일정, 한 줄에 하나 (Worker)
  id          bigint generated always as identity primary key,
  code        text not null,                         -- 묶음 대표 코드
  stage       text not null check (stage in ('olt', 'ship', 'install', 'commission', 'pre', 'sop')),  -- OLT·선적·설치·시운전·시생산·양산(SOP)
  date_from   date,
  date_to     date,                                  -- 기간이면 끝, 하루면 date_from과 같음
  raw_text    text not null,                         -- 원문 줄(이 줄에 없는 숫자는 버림)
  doc_name    text not null,
  doc_path    text,
  doc_kind    text not null check (doc_kind in ('summary', 'plan', 'spec')),  -- 사양정리 · 수행계획서 · 사양서
  doc_mtime   timestamptz,
  page        text,                                  -- p2 · 시트 이름
  read_at     timestamptz not null default now()
);
create index if not exists ix_schedule_milestone_code on project.schedule_milestone (code);
comment on table project.schedule_milestone is '문서(사양정리·수행계획서·사양서)에서 읽은 일정. 문서가 바뀐 프로젝트만 다시 읽어 통째로 바꾼다';

create table if not exists project.schedule_alert (                -- 매일 판정 결과 (Worker). 화면 '일정 주의' 칸이 읽는다
  code        text not null,
  kind        text not null check (kind in ('late', 'soon', 'after_sop', 'erp')),  -- 지연 의심 · 일정 임박 · 양산 후 작업 중 · ERP 기간 갱신 필요(화면에 안 띄움)
  stage       text not null,
  due_date    date not null,                         -- 기준 일정
  days        int not null,                          -- 오늘 - 기준일(+면 지남, −면 남음)
  reason      text not null,                         -- 쉬운 말 한 줄
  evidence    jsonb,                                 -- {doc, page, line, modular, po}
  first_seen  date not null,                         -- 처음 나온 날(유예 계산)
  last_seen   date not null,
  primary key (code, kind, stage)
);
comment on table project.schedule_alert is '일정 주의 판정(매일). 지난번에 있다가 사라진 판정은 지운다. first_seen부터 유예(기본 14일, 일정 임박은 바로) 뒤 화면에 보인다';

create table if not exists project.schedule_hide (                 -- 숨기기 (API)
  id          bigint generated always as identity primary key,
  code        text not null,
  kind        text not null,
  employee_id bigint,                                -- 나만 숨기기 = 직원 id, 회사 전체 숨기기 = null(관리자)
  note        text,
  hidden_at   timestamptz not null default now()
);
create unique index if not exists ux_schedule_hide on project.schedule_hide (code, kind, coalesce(employee_id, 0));
comment on table project.schedule_hide is '일정 주의 숨기기. 나만(직원 id) 또는 회사 전체(null, 관리자)';
