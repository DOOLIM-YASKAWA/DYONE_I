# 프로젝트 메인 화면 — 설계안 v1 (2026-11-03, 확인 전)

> 1차 오픈 ★★★. 홈을 없애고 로그인하면 이 화면이 첫 화면이다.
> 시안: `docs/시안/project-main-prototype.html` (v2, 11-03 결정 반영). 1차 오픈 계획 초안 1절·9절을 구체화한 문서.
> DB 표 생성·마이그레이션·운영 반영·커밋은 모두 **사용자 승인 후**.

## 구현 상태 (11-04)
- **만든 것**: J안 화면(`web/src/pages/projects/ProjectsMain.tsx`, `styles/project-main.css`), 홈 제거, SVG 로고, Pretendard. 표 7개(`server/db/project_main.sql`, 11-04 사용자 승인). Worker 잡 `project-main`(`server/src/DyOne.Worker/Jobs/ProjectMain/`), API `Modules/Projects/ProjectMainModule.cs`.
- 11-04 로컬 첫 실행: ERP 3,443건 미러(상태별 건수 ERP 원본과 일치), 흔적 1,618건, 후보 206건 중 30건 저장(1위 사우디, 2위 화성5 신기술 119~122+033 … v2 목록과 같은 순서), 브리핑 10건(사내 AI 10건).
- 영업이 선정하기 전에는 회사 주요 칸에 '시스템 추천'(규칙 점수 상위)을 보여 준다(11-04 사용자).
- 남은 것: ERP에만 있는 코드(그룹웨어 미동기)를 전체 목록에 보이기, 선정 담당 부서 지정, 일정 주의(보류), 상세 화면 ★(상세 방).

- **11-04 배치 변경(사용자)**: 위에서부터 인사·오늘의 브리핑 → [내 관심 | 회사 주요 | 일정 주의(B안)] → 전체 프로젝트(**검색창은 이 칸 머리로 옮김**, 고객사·ERP 상태 거르기와 한 줄). 위쪽 띠에는 '프로젝트 찾기 /' 바로가기만 남김. ERP 수주·영업중·내 관심 숫자 버튼은 뺌. 브리핑은 자동으로 넘기지 않고, 관심·주요 카드는 7초마다 넘김.

## 0. 확정된 것 (11-03 사용자)

| # | 결정 |
|---|---|
| 1 | 오늘의 브리핑은 전 직원 대상. '내 관심'·'회사 주요' 두 장을 밀기·탭·화살표로 넘김. 매일 아침 사내 AI가 관심·주요 프로젝트의 **지금 상태**를 3줄로 쓰고 근거 링크를 붙임(11-04 정정: 어제 변화 모음이 아님) |
| 2 | 내 관심 = **내가 ★한 것만**. PM·투입 프로젝트를 자동으로 넣지 않음 |
| 3 | 회사 주요 = 시스템이 후보·이유를 **담당 부서(영업 등)에 제안** → 그 부서가 **골라서** 정함 → 고른 것만 전원에게. 고르는 사람은 부서 자동 부여의 역할 키로 정함 |
| 4 | 판단은 규칙(프로그램), 설명은 사내 AI. AI는 문장만, 숫자는 DB 값만 |
| 5 | 프로젝트 정본은 ERP. 하루 1번, 바뀐 줄만(`DTS_UPDATE`), 읽기 전용. 화면은 DY ONE 사본만 읽음 |
| 6 | 일정 주의: 기준일은 ERP. NAS 최근 문서(수행계획서 › 사양정리 › 사양서) 일정과 차이가 크면 '확인 필요'. 종료일 지나도 발주 이어지면 진행 중. 활동·발주 없으면 '마감 처리 필요'(관리자). 2주 유예. 개인 끄기·회사 전체 끄기·건별 숨기기 |
| 7 | 옛 코드 → 새 코드 연결(예: DLP-26-033 사용중지 → 119~122) |

## 1. 화면

| 영역 | 누가 봄 | 내용 | 데이터 |
|---|---|---|---|
> **11-03 v4 배치(사용자)**: 위에서부터 검색 → 오늘의 프로젝트 브리핑 → [왼쪽 내 관심 | 오른쪽 회사 주요] → 전체 프로젝트. 관심·주요는 3개만 한 줄씩, 누르면 상세. **일정 주의는 내부 데이터(ERP 일정·NAS 문서 일정) 확인이 더 필요해 화면에서 뺌(보류).**

| 순서 | 영역 | 누가 봄 | 내용 | 데이터 |
|---|---|---|---|---|
| ① | 검색 (맨 위) | 전원 | 지금 화면처럼 코드·이름·발주처·PM 검색, `/`로 바로 입력. 옆에 ERP 기준 수주·영업중 건수 | `sync.erp_project` |
| ② | 오늘의 프로젝트 브리핑 | 전원 | 사내 AI가 '내 관심'·'회사 주요' 프로젝트의 지금 상태(단계·최근 진행·다음 일정)를 두 장에 3줄씩. 밀기·탭·화살표로 넘김. 이름을 누르면 상세, '근거'를 누르면 자료·기록. ★ 없으면 '회사 주요' 장부터 | `project.status_brief` |
| ③ 왼쪽 | 내 관심 프로젝트 | 본인 | ★한 것만 3개(최근 활동 순): 이름·코드·최근 활동·PM·단계. 누르면 상세, '모두 보기' 서랍. 비면 안내(자동 추가 없음) | `project.favorite` |
| ③ 오른쪽 | 회사 주요 프로젝트 | 전원 | 선정된 것 3개(순위 순): 이름·코드·계약 금액·단계·다음 일정(사실만, 상태 문장은 브리핑에). 누르면 상세, '모두 보기' 서랍 | `project.featured` |
| ③-후보 | 새 후보 검토 | 선정 담당·관리자 | 회사 주요 칸 아래 링크 → 서랍: 점수·점수 근거(DB 값)·이유(AI 문장)·[선정]·[제외] | `project.featured_suggestion` |
| ④ | 전체 프로젝트 | 전원 | 지금 목록 + ★ 열, 상태는 ERP(그룹웨어 상태는 표시하지 않음), 중단·분할된 코드는 새 코드 안내 | `sync.erp_project`, `project.project_alias` |

- 협력사 로그인은 지금처럼 배정된 프로젝트 목록만(②·③ 숨김).
- 브리핑 줄 = 지금 상태 문장(AI), 관심·주요 칸 줄 = 사실(최근 활동·금액·단계·다음 일정) → 같은 내용이 두 번 나오지 않게 나눔.
- ★ 버튼은 목록·카드에 둔다. 상세 화면의 ★는 상세 방(local_4a1f80d1)에 컴포넌트를 넘겨 붙여 달라고 한다(이 방은 상세 파일을 고치지 않음).

## 2. 데이터 — 새 표 (승인 전, 구조안)

### 2-1. ERP 미러 (Worker만 씀, `sync` 스키마)

```
sync.erp_project                 -- WS_B_BSNSS_MAIN 프로젝트당 DTS_UPDATE 최신 1줄 + SA_PROJECTH
  cd_prjt text pk                -- DLP-26-119
  status_code text               -- WS_CM00008 001 영업중·002 수주·003 마감·004 중단
  status_name text               -- 코드표 DZSN_MA_CODEDTL 디코딩
  use_yn boolean                 -- '사용중지' 여부 (위치는 구현 때 ERP에서 확인)
  name text, partner_name text
  cnst_from date, cnst_to date   -- DT_FM_CNST, DT_TO_CNST
  contract_amount numeric        -- SA_PROJECTH.AM_WONAMT
  project_end date               -- SA_PROJECTH.ED_PROJECT
  erp_updated_at timestamp       -- DTS_UPDATE (다음 증분의 기준)
  synced_at timestamptz

sync.erp_project_po              -- PU_POH/PU_POL 프로젝트별 집계만(발주 줄은 안 가져옴)
  cd_pjt text pk
  last_po_date date, po_30d int, po_90d int, po_total int
  erp_updated_at timestamp, synced_at timestamptz
```

- 실행: 매일 06:30(브리핑 07:00 전), `dylab` 읽기 전용, `WITH(NOLOCK)`, `DTS_UPDATE > 마지막 값`만. 첫 실행만 전체.
- 비밀번호는 Worker user-secrets `Erp:Password`(이미 있음). 운영은 환경변수 — DEPLOY 문서에 추가.
- `project.project`와는 `code = cd_prjt`로 붙인다(project.project는 그대로 그룹웨어 미러).
- **그룹웨어 ↔ ERP 차이는 일시적인 것으로 본다(11-04 사용자).** 그룹웨어 프로젝트(285) 동기화가 고쳐지면 상태·누락 코드가 따라 맞춰진다. 그래서
  - 화면은 상태를 **ERP 값만** 보여 주고 'GW와 다름' 같은 표시는 하지 않는다.
  - ERP에만 있는 코드(예: DLP-26-227)는 ERP 미러로 목록에 보이게 하고(이름·고객사도 ERP에서), 그룹웨어에 같은 코드가 생기면 코드로 자동으로 합친다. 따로 연결 작업을 하지 않는다.
  - 그룹웨어 차이를 사람에게 확인 요청하지 않는다(영업 확인 목록에서도 제외).

### 2-2. 옛 코드 → 새 코드

```
project.project_alias
  old_code text, new_code text, pk(old_code, new_code)
  source text                    -- erp(사용중지 + 이름 유사로 제안) | manual
  confirmed_by bigint, confirmed_at timestamptz, note text
```
- 옛 코드로 올라온 자료·모듈라·NAS 경로를 새 코드 화면에서도 보이게 하는 연결. 자동 제안 → 관리자 확인.

### 2-3. 사용자가 쓰는 표 (API가 씀, `project` 스키마)

```
project.favorite                 -- ★
  employee_id bigint, project_id bigint, sort int, created_at, pk(employee_id, project_id)

project.featured_suggestion      -- 매일 규칙 점수 결과(Worker)
  run_date date, project_id bigint, pk(run_date, project_id)
  score int, parts jsonb         -- [{key:'amount', pts:4, value:'37억'}] 숫자는 DB 값
  reason text                    -- 사내 AI 문장(숫자 넣지 않음)

project.featured                 -- 선정 결과(이력 보존: 해제 시 지우지 않고 닫음)
  id, project_id, rank int, note text
  status text                    -- selected | excluded | held
  decided_by bigint, decided_role text, decided_at
  ended_by bigint null, ended_at null

project.schedule_basis           -- 일정 주의 계산 결과(Worker) + PM 선택
  project_id pk
  erp_end date, doc_date date, doc_kind text, doc_path text   -- NAS 문서 일정과 근거
  chosen text                    -- erp | doc | manual (PM이 고름, 기본 erp)
  manual_date date, chosen_by, chosen_at
  verdict text                   -- ok | mismatch(확인 필요) | running_late(지연 의심) | needs_close(마감 처리 필요)
  verdict_since date             -- 2주 유예 계산용
  computed_at

project.schedule_hide            -- 건별 숨기기
  project_id, employee_id null   -- null = 회사 전체(관리자), 값 = 나만

project.main_pref                -- 개인 끄기·회사 전체 끄기
  employee_id bigint null        -- null = 회사 전체
  key text, value text           -- 'schedule.visible' = 'false'
```

### 2-4. 브리핑 — **프로젝트의 지금 상태** (11-04 사용자 정정)

> 브리핑은 "어제 바뀐 것" 모음이 아니라, **내 관심·회사 주요 프로젝트가 지금 어디까지 와 있고 다음에 뭐가 있는지**를 보여 준다. 어제 변화는 줄 끝 작은 표시로만.

```
project.status_brief             -- 프로젝트별 '지금 상태' 한 줄(최신 1행 + 이력)
  project_id bigint, made_at timestamptz, pk(project_id, made_at)
  facts jsonb                    -- 입력 사실(아래 표). 숫자·날짜는 DB 값
  facts_hash text                -- 입력이 안 바뀌면 다시 만들지 않음
  sentence text                  -- 사내 AI 한 줄(약 60자)
  changed_yesterday int          -- 어제 변화 건수(줄 끝 '어제 +n' 표시용)
  evidence jsonb                 -- [{kind:'journal', id}, {kind:'file', id}, …]
```

**입력 사실(프로그램이 모음)** — "지금 어디·다음 무엇·최근 무엇"
| 묶음 | 내용 | 출처 |
|---|---|---|
| 지금 어디 | 진행 단계(DY ONE), ERP 상태, 모듈라 빌드 상태 | `project_stage`, `sync.erp_project`, `modular.*` |
| 다음 무엇 | 다음 일정(모듈라 일정·ERP 공사 종료), 남은 단계 | `modular.*`, `sync.erp_project` |
| 최근 무엇 | 최근 2주 업무 일지 내용, 자료 업로드 종류·건수, 발주 건수, 인원 변화 | `project_journal`, `project_file`, `sync.erp_project_po`, `project_member` |

**문장 틀**: `[지금 단계·상태] + [최근 핵심 진행] + [다음 일정]`
예) "모듈라 단계로 OLT가 진행 중이며, 최근 2주 설계 도면 14건이 올라왔습니다. 다음은 11/18 모듈라 일정입니다."

- **대상** = 누군가 ★한 프로젝트 ∪ 회사 주요(전체 3,114건이 아님). 매일 07:00에 입력을 다시 모아 **facts_hash가 바뀐 것만** 사내 AI로 다시 쓴다.
- 사람마다 AI를 부르지 않는다. '내 관심' 장 = 내 ★ 중 최근 활동 순 3줄, '회사 주요' 장 = 선정 순위 3줄.
- 줄 끝에 `어제 +n`(변화 건수)·`근거` 링크. 활동이 오래 없으면 "최근 30일 활동 없음"처럼 사실 그대로.
- 문장 속 숫자·날짜가 facts에 없으면 버리고 규칙 문장("모듈라 단계 · 최근 2주 일지 5건 · 다음 11/18")으로 대신한다.
- 회사 주요 칸의 줄은 브리핑과 겹치지 않게 **사실(계약 금액·단계·다음 일정)** 만 보여 준다.

### 2-5. 매일 흐름 (11-04 정리 — 11-04 데이터 확인 결과 반영)

DY ONE 안의 진행 단계·업무 일지는 아직 거의 비어 있다(로컬 확인). 그래서 '지금 상태'는 **ERP·발주·모듈라·ES·NAS**에서 모으고, 글로 된 자료는 사내 AI가 읽는다.

| 시각 | 단계 | 누가 | 내용 |
|---|---|---|---|
| 06:30 | ① ERP 미러 | Worker(읽기 전용) | 영업 관리 상태·공사 기간, 프로젝트 헤더 계약 금액·마감일(`DT_CLOSE`), 발주 집계 — 바뀐 줄만 |
| 06:40 | ② NAS 훑기 | Worker(읽기 전용) | 프로젝트 폴더별 최근 수정 파일 목록(이름·종류·시각). **Thumbs.db·CAD 잠금(.dwl)·.log·.bak 제외**. 옛 코드 폴더는 연결된 새 코드로 합산 |
| 06:50 | ③ 흔적 점수·후보 | 프로그램 | 규칙 점수(3절) + **최근 2주 발주·NAS 작업·모듈라/ES 기록**. ERP 상태는 회계 마감(재무팀)·중단(대부분 영업 접음)이라 '진행 중' 판단은 흔적과 함께 본다 |
| 07:00 | ④ 글 읽기 | **사내 AI** | 대상 프로젝트(★ ∪ 회사 주요 ∪ 새 후보)의 최근 2주 **모듈라 활동 글·ES 이슈 글·업무 일지·새로 올라온 문서 제목(수행계획서·사양정리 등)** 을 읽고 사실 목록으로 요약: 지금 공정, 다음 일정(문서에 적힌 날짜), 막힌 일·고객 요청 |
| 07:05 | ⑤ 상태 문장 | **사내 AI** | ①~④ 사실만 주고 `[지금]+[최근]+[다음]` 한 줄. 숫자·날짜 검사 후 저장(`project.status_brief`) |
| 07:10 | ⑥ 보조 신호 | **사내 AI** | ④에서 "지연·클레임·사양 변경·고객 독촉" 같은 신호를 꼬리표로(후보 표에만, 점수는 안 바꿈) |
| 출근 | ⑦ 화면 | 프로그램 | 브리핑 두 장·관심·주요 목록. 사람마다 AI를 다시 부르지 않음 |

- 사내 AI = `Llm:*`(사내 SGLang, 데이터가 회사 밖으로 나가지 않음). Worker는 이미 수담푸드 메뉴 읽기(`SudamMenuJob`)에서 같은 방식으로 부르고 있다.
- AI 호출 수 ≈ 대상 프로젝트 수(입력이 바뀐 것만) × 2~3번/일. 대상은 수십 건 수준.
- AI가 멈춰도 ①~③ 규칙 문장으로 화면은 나온다.
- NAS 문서 **내용**까지 읽는 것(④의 문서 본문)은 2단계: 처음엔 파일 이름·폴더(예: `4. 도면/제작도`, `12. 완료보고서`)만으로 "도면 작업 중 / 완료보고서 등록" 정도를 만든다.

## 3. 주요 후보 점수 (초안 1절 표 그대로, Worker가 매일 계산)

| 항목 | 점수 | 출처 |
|---|---|---|
| 계약 금액 | 100억↑ +5 · 30억↑ +4 · 10억↑ +2 | `sync.erp_project.contract_amount` |
| 로봇 대수 | 30↑ +3 · 10↑ +2 · 4↑ +1 | 모듈라 미러(`modular.*`) |
| 신공장·신설 | +2 | 이름 |
| 해외 | +1 | 이름·발주처 |
| 최근 활동 | 30일 10건↑ +2 · 있으면 +1 | DY ONE 활동 |
| 종료 임박 | 수주 & 종료 120일 이내 +2 | ERP 공사 종료 |
| 소규모 유지보수 | −2 | 이름(수리·점검·교체…) & 10억 미만 |
| 제외 | ERP 마감·중단·사용중지, 종료일 30일 지나고 활동 없음 | ERP |

- 상위 10건을 후보로. 이미 선정·제외된 것은 다시 올리지 않되, 점수가 크게 오르면(+3) 다시 제안.

## 4. 일정 주의 판단 (Worker, 매일) — **보류(11-03)**: 화면에서 뺌. ERP 일정·NAS 문서 일정 내부 데이터를 더 확인한 뒤 다시 정한다. 아래는 그때 출발점.

1. 기준일 = PM이 고른 값이 있으면 그것, 없으면 ERP `cnst_to`.
2. NAS 최근 문서 일정(수행계획서 › 사양정리 › 사양서 순)과 **차이 ≥ 30일**이면 `mismatch` → "확인 필요", PM에게 [기준일 고르기].
3. 기준일 지남 + 최근 30일 발주 또는 활동 있음 → `running_late`(지연 의심, 진행 중으로 봄).
4. 기준일 지남 + 14일 넘게 발주·활동 없음 → `needs_close` → 관리자 목록만(일정 주의 칸엔 안 띄움).
5. 판단이 처음 나온 날부터 14일 유예 뒤에 화면에 띄운다(`mismatch`는 유예 없이).
6. NAS 문서 일정 읽기는 11-03 시험 스크립트(sched_scan) 방식을 Worker로 옮김 — 2단계에 둔다.

## 5. 선정 담당 역할

- 부서 자동 부여 설계(역할 키 2단)를 따른다: 기능 키 `project.featured` → 역할 `sales.*`(도장·자동화·GI 영업, 선행검토) + 관리자.
- 지금 `access.role`·`access.app_role`은 아직 없다(부서 자동 부여 P2 미완). 그래서 **우선 `access.app_department`에 `project.featured` → 영업 부서 id를 넣어 쓰고**, P2가 끝나면 `access.app_role`로 옮긴다(같은 판정 함수 하나로 감싸서 바꿀 곳은 한 군데).

## 6. 만드는 순서

| 단계 | 내용 | DB 변경 | 비고 |
|---|---|---|---|
| S0 | 새 메인 화면 뼈대(`ProjectsMainPage`), 로그인 첫 화면을 `/projects`로, 전체 목록은 지금 표 재사용 | 없음 | `routes.ts`·`HomePage.tsx`는 YTVH-437 작업과 겹침 → 확인 후 |
| S1 | ERP 미러 Worker 잡 + 목록에 ERP 상태(ERP에만 있는 코드 포함) + 옛→새 코드 | `sync.erp_project`·`erp_project_po`·`project.project_alias` | ERP 접속 코드 새로 만듦(읽기 전용) |
| S2 | ★ 관심: API + 목록·카드 ★ + ② 영역 | `project.favorite` | 상세 ★는 상세 방에 넘김 |
| S3 | 회사 주요: 점수 계산 잡 + 후보 표 + 선정 + ③ 영역 | `featured_suggestion`·`featured` | 선정 권한은 5절 |
| S4 (보류) | 일정 주의(ERP·발주·활동만) + 끄기·숨기기 + 마감 처리 필요 | `schedule_basis`·`schedule_hide`·`main_pref` | |
| S5 (보류) | NAS 문서 일정 비교 + PM 기준일 고르기 | (S4 표 사용) | NAS 읽기 전용 |
| S6 | 브리핑: 관심·주요 프로젝트의 지금 상태 모음 + 사내 AI 문장 + 근거 | `project.status_brief` | `Platform/Llm.cs` 사용 |

- 단계마다 `DEPLOY.md`·`DEPLOY-PENDING.md` 갱신(새 스키마·ERP 시크릿·Worker 시각).
- 운영엔 스키마만, 로컬 시험 데이터(★·선정·브리핑)는 넣지 않는다.

## 7. 결정이 필요한 것

1. **저장 위치**: ERP 미러는 `sync.*`, ★·주요·일정은 `project.*` 새 표. CLAUDE.md "쓰기 대상"에 새 `project.*` 표를 더해야 함.
2. **선정 담당**: 영업 4곳(도장영업·자동화영업·GI영업·선행검토) + 관리자로 할지. 주요 개수 상한(예: 8)과 재확인 주기(예: 분기).
3. *(보류)* **일정 주의 보이는 범위**: 전원에게 회사 전체 건 vs 내 관심·회사 주요에 든 건만. 기본 켜짐 여부.
4. *(보류)* **'확인 필요' 차이 기준**: 30일로 시작할지.
5. *(보류)* **건별 숨기기 권한**: 나만 숨기기는 누구나, 회사 전체 숨기기는 관리자·PM.
6. **홈 제거 시점**: S0에서 바로 첫 화면을 바꿀지, S2(관심)까지 끝난 뒤 바꿀지.
