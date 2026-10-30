# 배포 가이드 — 로컬 ↔ 운영 차이 (DY ONE 내부용)

이 문서는 **지금(로컬 개발)과 운영 서버에서 달라져야 하는 모든 것**을 한곳에 모은 정본이다.
운영 배포를 준비할 때 이 문서를 처음부터 끝까지 따라가면 빠지는 설정이 없어야 한다.

> **황금 규칙**
> 1. **비밀값(비밀번호·시크릿)은 저장소에 절대 넣지 않는다.** 운영 서버의 환경변수/시크릿에만 둔다. 로컬은 user-secrets·`.env.local`·`appsettings.Development.json`(개발 전용 값만).
> 2. **그룹웨어(tims)·ERP는 어디서도 읽기 전용.** dyone DB만 쓴다. (쓰기도 dyone 전용 테이블/연결만)
> 3. **새 기능을 추가하거나 로컬·운영이 달라지는 설정을 건드리면, 반드시 이 문서(차이)와 [DEPLOY-PENDING.md](DEPLOY-PENDING.md)(할 일)를 같이 갱신한다.** ← 앞으로 하는 모든 작업 포함.
> 4. **로컬에서 테스트로 만든 데이터는 운영에 절대 넣지 않는다.** 운영에는 '구조(스키마)'와 '기준 코드'만 넣는다 — 아래 0장.

관련 체크리스트(아직 운영에 반영 안 한 구체 작업): **[DEPLOY-PENDING.md](DEPLOY-PENDING.md)**

---

## 0. 운영 데이터 원칙 — 무엇을 넣고 무엇을 넣지 않나

로컬 dyone(5433)은 **개발·시험장**이다. 그룹웨어에서 가져온 실제 프로젝트를 보면서 단계를 바꿔 보고, 파일을 올려 보고, 사양서를 분석·판정해 보는 데이터는 **시험 흔적**이라 운영(.111)에 넣으면 안 된다.

**방법 원칙: 로컬 DB를 덤프·복원(pg_dump → 운영)하거나, 로컬 테이블 데이터를 복사해 넣지 않는다.** 운영에는 저장소의 `server/db/*.sql` 스크립트만 실행한다.

| 구분 | 예 | 운영에는 |
|---|---|---|
| **① 구조(스키마)** | 테이블·컬럼·인덱스 만들기 | ✅ `server/db/*_schema.sql` 등 실행 |
| **② 기준 코드** | 진행 단계 S1~S8, 문서 분류(사양서·견적…) | ✅ `server/db/seed_reference_codes.sql` 실행 |
| **③ 원천에서 오는 데이터** | 그룹웨어 프로젝트·조직도·모듈라·ES 이슈 | ✅ **운영 Worker가 그룹웨어에서 직접** 받아 쓴다(로컬에서 복사 X) |
| **⑤ 로컬 시험 데이터** | 단계 변경(`project_stage`·`project_stage_status`·`project_stage_history`), 업로드 파일(`project_file` + `.filestore` 실물), 테스트 프로젝트(`DLP-TEST-01`), 화면 메모(demo memo) | ❌ **절대 넣지 않는다** |
| **⑥ 접근 권한 초기값** | `app_access.sql`의 부서·직원 지정(모듈라팀·ES영업실 + 개발자 함유진 '모든 앱') | ✅ **그대로 넣는다**(2026-10-14 결정 — 세부 권한은 추후). 단, 운영 조직도 동기화 **후에** 실행 |

**스크립트 작성 규칙(앞으로 모든 작업)**
- `server/db/*.sql`에 데이터를 넣는 문장은 **기준 코드(②)뿐**. 특정 프로젝트·사람·테스트 행을 넣는 INSERT는 쓰지 않는다(필요하면 '로컬 전용'이라고 파일을 분리하고 운영 목록에서 뺀다).
- 기존 행을 채우는 backfill(예: `project_progress.sql`, `project_stage_history.sql`, `project_stage_v2.sql`의 정리 문장)은 **운영에선 대상이 없어 아무 일도 안 하는지** 확인하고 적는다.
- 로컬에서 시험한 데이터를 운영에서도 써야 할 일이 생기면(예: 실제 업무 파일) **운영 화면에서 다시 올린다** — DB 복사로 옮기지 않는다.

**배포 후 점검**: 운영에서 `server/db/check_prod_clean.sql`을 실행해 테스트 흔적이 0인지 확인한다(배포 직후엔 사용자 데이터 행 수도 0이어야 정상).

### 스크립트별 운영 적용 판정 (2026-10-14)
| 파일 | 내용 | 운영 |
|---|---|---|
| `seed_reference_codes.sql` | 진행 단계·문서 분류 기준 코드 | ✅ 적용(스키마들 다음) |
| `project_files_schema.sql` | 업로드 파일 테이블 + 문서 분류 코드 | ✅ 적용 |
| `modular_schema.sql`, `works_schema.sql`, `sync_change_notice.sql` | 미러·알림 테이블 | ✅ 적용(데이터는 Worker) |
| `project_progress.sql`, `project_stage_history.sql` | 테이블 + 기존 단계 backfill | ✅ 적용 — 운영엔 기존 단계가 없어 backfill 0건 |
| `project_stage_v2.sql` | 단계 코드 이름·순서 정리 + 옛 단계 행 정리 | ✅ 적용 가능(코드 정리는 seed와 같음, 행 정리는 운영에서 0건) |
| `app_access.sql` | 앱 접근 테이블 + 초기 부서·직원 지정(개발자 '모든 앱' 포함) | ✅ 적용 — **운영 Worker가 조직도(org.*)를 먼저 채운 뒤** 실행(부서명·login_id로 찾아 넣으므로 조직도가 비어 있으면 0건). 세부 권한은 추후 |
| `org_contact.sql` | 직원 연락처 테이블(휴대폰·직통, 2026-10-15) | ✅ 적용(데이터는 운영 Worker가 그룹웨어에서) |
| `org_history.sql` | 직원 소속·부서 이름 이력 테이블(부서 자동 부여 P1, 2026-10-28) | ✅ 적용(데이터 없음 — 운영 Worker가 적용 다음 날부터 날마다 기록하고, 처음 한 번 그룹웨어 결재·부서 정보로 그 이전을 채움). 로컬 이력을 옮기지 않음 |
| `project_member.sql` | 프로젝트 인원(PM·투입 인원) 테이블 (2026-10-15) | ✅ 적용(데이터 없음 — 로컬에서 지정한 PM·인원은 시험 데이터라 옮기지 않음). **운영 적용 전 스키마 소유자(팀장님) 확인** |
| `partner.sql` | 협력사 미러(`partner.company`·`partner.person`) + `project.project_member.partner_person_id`(2026-10-30) | ✅ 적용(데이터 없음 — 명단은 운영 Worker `partner-sync`가 주소록에서, 공용 비밀번호는 **운영 관리자 화면에서 새로** 넣는다). 로컬 비밀번호 해시·시험 배정 행(협력사 1명 → 프로젝트 1건)은 옮기지 않음. **API 배포 전에 먼저 적용**(로그인·프로젝트 목록이 partner 표를 읽음) |
| `demo_memo.sql` | 데모 기간 화면 메모 | ❌ 운영에서는 기능 꺼짐(`DemoMemo:Enabled=false`) — 적용하지 않거나 데이터 없이 |

---

## 1. 호스트·포트 한눈에

| 구성요소 | 로컬(지금) | 운영 | 설정 위치 |
|---|---|---|---|
| dyone DB | Docker `dyone_db` `localhost:5433` (PG18 복사본) | **`192.168.30.111:12678`** dyone | ConnectionStrings:DyOne / DyOneWrite* |
| API (DyOne.Api) | `http://localhost:5080` (`--launch-profile http`) | 운영 호스트:포트(HTTPS 권장) | launchSettings(로컬) / 운영은 `--urls` 또는 리버스 프록시 |
| 웹(Vite dev) | `http://localhost:5173` (`npm run dev`, `/api`→5080 프록시) | **정적 빌드(`npm run build`) 후 API가 서빙하거나 웹서버 배포** | vite.config 프록시(로컬 전용) |
| 데스크톱 앱 | `DYONE_APP_URL`/appsettings `AppUrl`=localhost | `AppUrl`=**운영 https 주소**(비 localhost는 https 강제) | desktop/appsettings.json |
| SmartLab (임베드) | web `:4174` / api `:4000` | `.119` (NSSM `DY-SmartLab`) | SmartLabSso:BaseUrl |
| SRS Analyzer (임베드) | `:4175` | 운영 주소 | SrsAnalyzerSso:BaseUrl |
| Process (임베드) | web `:4176` / api `:5082` | 운영 주소 | ProcessSso:BaseUrl / VITE_PROCESS_API_TARGET |
| 그룹웨어 tims(읽기) | `121.139.49.10:5432` | 동일(읽기 전용) | Worker Gw 연결 |

> 참고: 네이티브 postgres `5432`는 구버전(쓰지 않음). 라이브 SmartWork는 `.111:24789`.

---

## 2. 실행 환경 (Environment)

| | 로컬 | 운영 |
|---|---|---|
| ASPNETCORE_ENVIRONMENT | `Development` | **`Production`** |
| 적용 설정 파일 | `appsettings.Development.json` | `appsettings.json` + **`appsettings.Production.json`(현재 없음 → 운영 전 생성)** 또는 환경변수 |
| 실행 방식 | `dotnet run` (API/Worker) | **`dotnet publish` 후 서비스 상주**(API 상시, Worker는 스케줄 상주) |

⚠️ 개발 전용 값들이 `appsettings.Development.json`에만 있으므로, **운영에서는 같은 키를 `appsettings.Production.json`이나 환경변수로 다시 줘야 한다**(아래 3·4장).

---

## 3. 운영에서 반드시 "환경변수/시크릿"으로 넣어야 하는 값

로컬은 개발 전용 값이 들어 있지만 **운영에선 전부 새 값으로** 줘야 한다. (`__`는 .NET 환경변수 구분자 `:` 대체)

| 키 | 용도 | 로컬 현재 | 운영 |
|---|---|---|---|
| `Auth__SigningKey` | JWT 서명 | `DEV-ONLY-...`(개발용) | **운영 전용 32자+ 랜덤** |
| `ConnectionStrings__DyOne` | API 읽기 연결 | user-secrets(localhost:5433) | **.111:12678** |
| `SmartLabSso__Secret` | SmartLab SSO 공유키 | `dev-...-secret` | **운영 값**(SmartLab의 `DYONE_SSO_SECRET`와 동일) |
| `SmartLabSso__BaseUrl` | SmartLab 임베드 주소 | `http://localhost:4174` | 운영 SmartLab 주소 |
| `SrsAnalyzerSso__Secret` / `__BaseUrl` | SRS 임베드 | 개발값 | 운영값 |
| `ProcessSso__Secret` / `__BaseUrl` | Process 임베드 | 개발값 | 운영값 |
| `FileStorage__Root` | 업로드 문서 저장 경로 | `E:\dy-one-internal\.filestore` | **사내 NAS UNC(예 `\\nas\dyone-files`) — 실제 경로 미정** |
| `ConnectionStrings__DyOneWrite`(+`DyOneWriteProd`) + `*__Password` | Worker/쓰기 대상 | user-secrets(로컬) | 운영 .111 + 비번 |
| `Gw__Password` | Worker 그룹웨어 읽기 | user-secrets | 운영 시크릿 |
| `Auth__PartnerTokenMinutes` | 협력사 토큰 만료(분) | 기본 240(직원 480) | 그대로 또는 더 짧게 |
| (SmartLab) `SLAB_SECRET`, `DYONE_SSO_SECRET`, `PG*` | SmartLab 서버 | 로컬 임시값 | 운영 시크릿(아래 7장) |

---

## 4. DB 연결·쓰기 권한

- **읽기**: API는 `ConnectionStrings:DyOne`(읽기 전용, `default_transaction_read_only=on`). 운영은 `.111:12678`.
- **쓰기(dyone 자체 데이터만)**: 사용자 편집(진행 단계·프로젝트 문서 등)은 읽기 연결에서 read-only 옵션만 제거해 쓴다. Worker 미러 적재는 `DyOneWrite`/`DyOneWriteProd`.
- ⚠️ **운영 DB 접속 계정에 아래 테이블 INSERT/UPDATE 권한 필요**: `project.project_stage`, `project.project_file`, `project.project_member`, `partner.company`(공용 비밀번호 해시·로그인 중지)·`partner.person`(마지막 로그인)(+ 앞으로 추가되는 dyone 자체 테이블), 미러 테이블 전체.

---

## 5. DB 스키마 적용 (운영 .111) — 로컬에만 적용된 것들

운영 배포 전 `.111:12678` dyone에 아래를 적용(모두 여러 번 실행해도 안전하게 작성됨). **파일 경로는 저장소 `server/db/`**.

| 항목 | 적용 내용 | 파일/SQL |
|---|---|---|
| source_updated_at | `ALTER TABLE project.project ADD COLUMN IF NOT EXISTS source_updated_at timestamp;` | DEPLOY-PENDING 1 |
| 진행 단계 | `project.project_stage` + 코드셋 `project_stage` | DEPLOY-PENDING 3b |
| **프로젝트 문서** | `project.project_file` + 분류코드 `document_category` | `server/db/project_files_schema.sql` |
| 모듈라빌드(376) | `modular.*` | `server/db/modular_schema.sql` |
| WORKS 공통(281 등) | `works.*` | `server/db/works_schema.sql` |
| **프로젝트 인원(PM·투입 인원)** | `project.project_member`(조직도 `org.employee` 참조) | `server/db/project_member.sql` |
| **직원 연락처(휴대폰·직통)** | `org.employee_contact`(Worker 조직도 동기화가 채움) — API 배포 전 필수 | `server/db/org_contact.sql` |
| **소속·부서 이력(부서 자동 부여)** | `org.employee_dept_history`·`org.department_history`(Worker 조직도 동기화가 채움, 표가 없으면 건너뜀). 운영은 적용한 날이 sync 이력 시작일이 되고 그 이전은 결재 근사 이력 | `server/db/org_history.sql` |
| **협력사(주소록 미러 + 공용 비밀번호)** | `partner.company`·`partner.person` + `project.project_member.partner_person_id`(employee_id nullable, 둘 중 하나 check) — API 배포 전 필수 | `server/db/partner.sql` |
| **MotoSim(런처 이관)** | `motosim.*`(Cell 파일 이력·NAS 업로드 조각·공지·활동·Drive 폴더 매핑·즐겨찾기·런처 권한·플러그인 버전) — API 배포 전 필수. NAS 폴더 규칙·프록시 크기 제한은 DEPLOY-PENDING. 이력은 전환 직전 `E:\dy-one\etl\etl-motosim.mjs --allow-prod`로 복사(DEPLOY-PENDING) | `server/db/motosim_schema.sql` |

> ⚠️ `source_updated_at` 컬럼을 **먼저** 안 만들면 Worker 운영 적재(MERGE)가 실패한다.

---

## 6. 파일 저장소 (NAS) — 2026-10-13 추가

- 레이아웃(A안, 사람이 탐색기로도 찾게): `FileStorage:Root\projects\<프로젝트코드 이름>\<분류>\<원본파일명>`
  - 폴더 기준 = 프로젝트 **code**(안정·고유), 가독성 위해 이름 덧붙임. 분류 = 하위폴더(미분류는 `_미분류`). 같은 이름 중복 시 ` (2)` 접미.
  - DB `project.project_file.stored_name` = Root 기준 **상대경로**. 분류 변경 시 실물 파일을 새 분류 폴더로 **이동**하고 경로 갱신.
  - 금지문자(`\ / : * ? " < > |`)만 `_`로 치환, 한글·공백·괄호는 유지. 프로젝트명 rename 시 기존 파일은 옛 폴더에 남음(드문 경우, 추후 정리 가능).
- `project.project_file.department_id` = 업로더 소속 부서 스냅샷 → 앱에서 **부서별 검색**(폴더 축 없이 메타로).
- 로컬=`.filestore`(gitignore), **운영=NAS 공유 폴더 UNC**.
- ⚠️ **실제 UNC 경로 미정 → 사용자 확인 필요.** 입력 후 운영 `FileStorage:Root`에 설정.
- NAS는 **API 서비스 계정으로 읽기/쓰기(이동 포함) 가능**해야 함.
- 업로드 상한 512MB(Kestrel `MaxRequestBodySize` + `FormOptions.MultipartBodyLengthLimit`).
- 비프로젝트(프로젝트 미연결) 공용 문서함은 추후(현재 프로젝트 귀속만).

---

## 7. 임베드 통합(SmartLab/SRS/Process) — cross-site 주의

로컬은 전부 localhost 다른 포트 = **same-site** 라서 쿠키 트릭이 통하지만, **운영은 도메인이 달라 cross-site** 가 된다.

- 각 통합: dyone가 사번을 공유 시크릿으로 서명한 단기 토큰 발급 → 대상 앱이 `/sso/dyone`로 검증 → 자기 세션 쿠키 발급.
- ⚠️ 운영 cross-site 해결(둘 중 하나):
  1. **dyone가 대상 앱을 리버스 프록시(`/smartlab` 등)로 same-origin 제공** (권장), 또는
  2. 대상 앱 세션 쿠키를 `SameSite=None; Secure` + 양쪽 **https**.
- SmartLab(.119): `server/index.js`의 `/sso/dyone`, `src/App.jsx` 임베드 감지, `styles.css` 톤 배포 + `nssm restart DY-SmartLab` + env `DYONE_SSO_SECRET`(dyone `SmartLabSso:Secret`와 동일), `SLAB_SECRET` 운영값, `PG*` 운영 DB. (상세 DEPLOY-PENDING 3)

---

## 8. Worker (미러 적재) 운영

- 로컬: 상시 아님(수동 실행), `WriteTargets=["DyOneWrite"]`(로컬만), `RunOnStartup=false`.
- 운영:
  - `WriteTargets__1=DyOneWriteProd` 추가 + `ConnectionStrings__DyOneWriteProd`(.111) + `DyOneWriteProd__Password`.
  - `Gw__Password`(그룹웨어 읽기) 시크릿.
  - **매일 08:00 자동 적재**가 필요하면 운영 서버에 Worker를 **서비스로 상주**(Project/Org Sync `DailyAt=08:00`, Modular/Works Sync는 `IntervalMinutes=1`로 상시 변경 감지).
  - 협력사 명단 `PartnerSync`(`DailyAt=08:10`, `BookId=15` 그룹웨어 회사 공용 주소록) — `partner.sql` 적용 전에는 건너뜀.
- ⚠️ 5장 `source_updated_at` 적용 **후에만** 운영 타깃 포함 실행.

---

## 9. 프런트/데스크톱

- 웹: 로컬은 Vite dev + `/api`→5080 프록시. **운영은 `npm run build` 정적 산출물**을 API가 서빙하거나 별도 웹서버로 배포. 다른 출처에서 API를 부를 땐 `VITE_API_BASE` 지정 + API `Cors:Origins`에 그 출처 추가.
- `Cors:Origins`: 로컬 `http://localhost:5173` 등. **운영 도메인으로 교체/추가.**
- 데스크톱 `AppUrl`: **운영은 https 필수**(localhost만 http 허용). `DYONE_APP_URL` 환경변수가 우선.

---

## 10. 기능 플래그

| 플래그 | 로컬 | 운영 |
|---|---|---|
| `Modular:EditEnabled` | false | 결정 필요(켜면 `ConnectionStrings:DyOneWrite` 필요, 그룹웨어로는 안 보냄) |
| `Works:EditEnabled` | false | 동일 |

---

## 11. 배포 순서 요약

1. `.111` DB에 5장 스키마 적용(특히 `source_updated_at` 먼저).
2. 운영 시크릿/환경변수 세팅(3·4장) + `FileStorage:Root`(NAS, 6장) + `Cors:Origins`/`AppUrl`(9장).
3. API `dotnet publish` → `ASPNETCORE_ENVIRONMENT=Production`으로 상시 구동.
4. 웹 `npm run build` 배포.
5. Worker 운영 타깃 포함 상주(8장).
6. 통합 앱(SmartLab 등) 운영 배포 + cross-site 프록시/쿠키(7장).
7. 데스크톱 `AppUrl`을 운영 https로.
8. 검증 후 [DEPLOY-PENDING.md](DEPLOY-PENDING.md) 항목 체크.

---

## 12. 사양서 분석 (2026-10-16 다시 만듦 · 2026-10-23 사양정리 초안·모델 결정)

- 기능: 영업 단계 '사양서 분석' 탭.
  - **사양정리 초안**(영업부 V5 양식): 값은 이번 고객 사양서에서만(근거 쪽 표시). 사내 AI(`Llm:*`)가 있으면 AI가 작성, 없으면 규칙으로 읽을 수 있는 칸만.
  - **모델 결정**(로봇·오프너·도장기·FGP, `DyOne.Spec/Draft/ModelPicker`): 사양서 지정 → 기설치 이력('기설치와 동일'이면 같은 공장 최근) → 같은 공장 이력 → 경향 규칙(도장기 = 도료·하전, 오프너 = 도어 MPX2600·후드 MPX3500, 스프레이·FGP = 같은 외/내판·공정 2022년 이후 보통) → 선정 필요. 근거 사례·원본 파일과 함께 보여 주고 사람이 채택. 규칙 근거 자료: `E:\dy-one\spec-draft\분석\`(pick_models.py·도장 설비 모델 경향 정리본).
  - 초안 pptx: `python/render_spec_summary.py` + 빈 틀 `templates/spec_summary_v5.pptx`(python-pptx·PyMuPDF 필요). 초안·결정은 `FileStorage:Root\_spec\drafts\{프로젝트}_{파일}.json`.
  - 비슷한 과거 프로젝트(고객 사양서끼리 비교) — 참고용.
  - 코드: `server/src/DyOne.Spec`(Library·Equipment·Draft), API `Modules/Spec`(SpecModule·SpecDraftModule·SpecLibrary·SpecEquipment·SpecAi), 웹 `SpecAnalysis.tsx`·`SpecSummaryDraft.tsx`.
- **DB 변경 없음.** NAS 색인 두 가지를 캐시 파일(JSON)로 둔다: ① 사양서 라이브러리 `library.json`(고객 사양서 특징·값, 사양정리 표, 회의록 목록) ② 설비 이력 `equipment.json`(사양정리 표를 공정(열) 단위로 + 사양정리 없는 프로젝트는 도면 파일 제목 → 공장·공정·외/내판·도료·하전·로봇·도장기·FGP).
- **Worker `spec-index` 작업**(매일 03:00, `SpecIndex` 설정)이 ① → ② 순서로 새로 만들어 같은 캐시 파일에 쓰고, API는 파일이 바뀌면 다음 조회 때 다시 읽는다. 화면의 '다시 읽기' 버튼도 같은 코드(`LibraryBuilder`·`EquipmentBuilder`) — **관리자만**(`access.app_employee`에 `app_key='*'`로 지정한 사람, 2026-10-27). 일반 사용자에게는 기준 시각만 보이고, API(`/api/spec/library/refresh`·`/api/spec/equipment/refresh`)도 403을 돌려준다.

| 항목 | 로컬 | 운영 |
|---|---|---|
| `Spec:LibraryRoots` | `["Q:\\01_프로젝트"]` (매핑 드라이브) | API·Worker 서비스 계정이 읽을 수 있는 **NAS UNC 경로**(드라이브 문자는 서비스 계정에 없음). **읽기 권한만** |
| `Spec:LibraryCache`·`Spec:EquipmentCache` | 비움 → `FileStorage:Root\_spec\library.json`·`equipment.json` | 같음. **API와 Worker가 같은 경로**여야 함, 두 계정 모두 쓰기 가능 |
| `Spec:PythonPath` | `python` (PyMuPDF·python-pptx 설치됨) | PDF 표 인식·초안 pptx·사양서 쪽 그림에 필요. 없으면 PDF는 .NET(PdfPig) 대체, pptx·쪽 그림은 안 됨 |
| PDF가 아닌 사양서(pptx·xlsx·docx) | 이 PC의 Microsoft Office(COM, pywin32)로 PDF로 바꿔 `{저장 파일}.view.pdf`로 둠(한 번만, 약 20초) | API 서비스 계정에서 Office COM이 안 되면 **LibreOffice 설치 + `Spec:SofficePath`**(soffice.exe 경로). 둘 다 없으면 글만 읽고 쪽 그림·하이라이트 없음 |
| `Llm:BaseUrl`·`Llm:Model`·`Llm:ApiKey` | BaseUrl·Model은 appsettings, **ApiKey 없음**(user-secrets) → 규칙 초안 | 사내 AI 키(user-secrets/환경변수). 없으면 규칙 초안 |
| Worker `SpecIndex` | `DailyAt 03:00`, `RunOnStartup false` + Worker에도 `Spec:*`·`FileStorage:Root` 설정 | 같음 |
| 색인 만들기 | Worker 매일 + 화면 '다시 읽기'(관리자만)(라이브러리 수 분, 설비 이력 NAS 프로젝트 약 1,100개 수 분) | 배포 직후 1회 버튼 또는 Worker `RunOnStartup` 한 번 |

- 이전 판(2026-10-15 삭제한 pilot 이식본)과 로컬 `spec` 스키마는 쓰지 않는다. 백업: side-session-notes의 spec-analysis-backup-2026-10-15.zip, spec-tables-backup-2026-10-15.sql.

---

## 14. 협력사 로그인 · API 기본 차단 (2026-10-30, docs/partner-access)

- **API 기본 차단**: 인증 Default·Fallback 정책 = `staff`(직원 전용). 협력사 토큰(`kind=partner`)은 `.PartnerAllowed()`를 붙인 9개 API(내 정보·기준 코드·배정된 프로젝트 목록/상세/단계 이력/인원/자료 목록·내려받기)만 통과하고, 배정 안 된 프로젝트는 404. 개발 환경은 시작할 때 허용 목록과 실제가 같은지 로그로 확인(`ProjectAccess`). kind 없는 옛 토큰은 직원으로 본다 → 배포해도 직원 로그인이 끊기지 않음.
- **협력사 로그인**: 아이디 = 그룹웨어 주소록 이메일(`partner.person`), 비밀번호 = 회사 공용 비밀번호(`partner.company.password_hash`, 해시만). 주소록에 있고·회사 로그인 중지가 아니고·**배정된 진행 프로젝트가 1개 이상**일 때만. 계정별 5회 실패 15분 잠금(메모리 — API 재시작 시 풀림), 토큰 240분.
- **관리자 화면** `/admin/partners`(관리자 = `access.app_employee` app_key='*'): 회사별 공용 비밀번호 설정·교체, 회사 로그인 중지.
- 이번 단계에서 협력사는 **보기만**(올리기·단계 변경·인원 배정은 4단계). 협력사 배정 화면이 아직 없어 운영에서는 4단계 전까지 실제로 로그인할 수 있는 협력사가 없다.

| 항목 | 로컬 | 운영 |
|---|---|---|
| 스키마 | `partner.sql` 적용(2026-10-30) | API 배포 전 적용 |
| 명단 | Worker 1회 실행 — 5개 회사 133명(구매처 0명, 이메일 없는 1명·우리 직원 1명 제외) | 운영 Worker `partner-sync`가 채움(로컬 복사 X) |
| 공용 비밀번호 | 5개 회사 같은 초기값(사용자 결정, 값은 저장소에 없음) | **운영 관리자 화면에서 새로 입력**. DB 복사 금지 |
| 시험 배정 | 협력사 1명 → 프로젝트 1건(`project_member.task`가 '[시험]'로 시작) | ❌ 옮기지 않음 |
| 외부 접속 | 사내망만 | 현장 협력사용 https/VPN — 미정(5단계) |

---

*갱신: 새 기능·설정 변경 때마다 이 표와 DEPLOY-PENDING을 같이 수정할 것.*
