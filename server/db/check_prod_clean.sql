-- 운영(.111) 반영 직후 점검: 로컬에서 테스트로 만든 데이터가 섞여 들어오지 않았는지 확인한다. (읽기만 함)
-- 운영 데이터 원칙: DEPLOY.md 0장. 배포 직후에는 아래 '사용자 데이터' 행 수가 모두 0이어야 정상이다.
-- (운영을 실제로 쓰기 시작한 뒤에는 0이 아니어도 된다 — 그때는 테스트 흔적 항목만 본다)

-- 1) 테스트 흔적: 항상 0이어야 함
select '테스트 프로젝트(DLP-TEST%)' as 항목, count(*) as 건수 from project.project where code ilike 'DLP-TEST%'
union all
select '로컬 경로가 남은 업로드 파일', count(*) from project.project_file where stored_name ilike '%.filestore%' or stored_name ~ '^[A-Za-z]:'
union all
-- 2) 사용자 데이터: '배포 직후'에는 0이어야 함(로컬에서 복사돼 온 것이 없다는 뜻)
select '진행 단계 지정(project_stage)', count(*) from project.project_stage
union all
select '업로드 파일(project_file)', count(*) from project.project_file
union all
select '프로젝트 인원(project_member)', count(*) from project.project_member;
-- 진행 상황·이력 테이블(project.project_stage_status / project_stage_history)이 운영에 있으면 같은 방식으로 0인지 본다.
-- (사양서 분석 spec.* 테이블은 2026-10-15 삭제 — 재설계 후 다시 추가)
