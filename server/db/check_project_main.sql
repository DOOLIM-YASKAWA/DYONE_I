-- 프로젝트 메인 Worker(project-main 잡) 실행 뒤 점검 — 읽기만 한다. 결과가 '문제' 줄로 나오면 확인한다.
-- 실행: docker exec -i dyone_db psql -U postgres -d dyone < server/db/check_project_main.sql
-- 설명: docs/roadmap/프로젝트 메인 로직·개발 현황 (2026-11-04).md 6절(검증)

\pset footer off
\echo '== 1. 오늘 돌았는가(각 표가 오늘 갱신됐는지) =='
select '① ERP 미러' as 단계, count(*) as 건수, (max(synced_at) at time zone 'Asia/Seoul')::timestamp(0) as 마지막_한국시간,
       case when max(synced_at)::date = current_date then '정상' else '문제: 오늘 안 돌았음' end as 판정 from sync.erp_project
union all select '② 흔적', count(*), (max(computed_at) at time zone 'Asia/Seoul')::timestamp(0), case when max(computed_at)::date = current_date then '정상' else '문제: 오늘 안 돌았음' end from sync.project_trace
union all select '③ 회사 주요 후보', count(*), (max(created_at) at time zone 'Asia/Seoul')::timestamp(0), case when count(*) between 1 and 30 then '정상' else '문제: 0건 또는 30건 초과' end from project.featured_suggestion where run_date = current_date
union all select '④ 브리핑(최신)', count(distinct code), (max(made_at) at time zone 'Asia/Seoul')::timestamp(0), '참고' from project.status_brief
union all select '⑤ 일정 주의 판정', count(*), max(last_seen)::timestamp(0), case when count(*) = 0 or max(last_seen) = current_date then '정상' else '문제: 지난 판정이 남음' end from project.schedule_alert;

\echo '== 2. ERP 미러: 상태별 건수(ERP 원본과 대조할 값) =='
select coalesce(status_name, '(비어 있음)') as 상태, count(*) as 건수 from sync.erp_project group by 1 order by 2 desc;

\echo '== 3. 회사 주요 후보: 순위·근거·AI 이유 =='
select count(*) filter (where rank between 1 and 30) as 순위_정상, count(distinct rank) as 순위_중복없음,
       count(*) filter (where reason is null and rank <= 10) as 상위10_이유없음,
       count(*) filter (where reason ~ '\d') as 이유에_숫자_문제,
       count(*) filter (where exists (select 1 from jsonb_array_elements(parts) p where coalesce(p->>'plain', '') = '')) as 쉬운말_빠짐_문제
  from project.featured_suggestion where run_date = current_date;

\echo '== 4. 브리핑: AI·규칙 비율, 사실에 없는 숫자(문제) =='
with latest as (select distinct on (code) * from project.status_brief order by code, made_at desc),
nums as (select l.code, l.sentence, m[1] as n from latest l, regexp_matches(l.sentence, '\d+', 'g') m)
select (select count(*) from latest where source = 'ai') as ai_문장, (select count(*) from latest where source = 'rule') as 규칙_문장,
       (select count(*) from latest where length(sentence) > 140) as 너무_긺_문제,
       (select count(distinct n.code) from nums n join latest l using (code) where position(ltrim(n.n, '0') in l.facts::text) = 0 and ltrim(n.n, '0') <> '') as 숫자불일치_문제;

\echo '== 5. 일정: 읽은 일정의 날짜가 말이 되는가 =='
select count(*) as 읽은_일정, count(distinct code) as 프로젝트, count(*) filter (where raw_text like '[그림]%') as 그림에서,
       count(*) filter (where date_to < date_from) as 끝이_시작보다_앞_문제,
       count(*) filter (where date_from < date '2022-01-01' or date_to > date '2030-12-31') as 연도_이상_문제
  from project.schedule_milestone;

\echo '== 6. 일정 주의: 종류별 =='
select kind as 종류, count(*) as 건수, min(days) as 최소_일, max(days) as 최대_일 from project.schedule_alert group by 1 order by 1;

\echo '== 7. 중복(같은 것이 두 번 저장됐는지) =='
select '후보' as 표, count(*) - count(distinct (run_date, code)) as 중복 from project.featured_suggestion
union all select '선정(열린 것)', count(*) - count(distinct code) from project.featured where ended_at is null
union all select '일정 주의', count(*) - count(distinct (code, kind, stage)) from project.schedule_alert;
