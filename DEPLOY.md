# 배포 가이드 — 로컬 ↔ 운영 차이 (DY ONE 내부용)

이 문서는 **지금(로컬 개발)과 운영 서버에서 달라져야 하는 모든 것**을 한곳에 모은 정본이다.
운영 배포를 준비할 때 이 문서를 처음부터 끝까지 따라가면 빠지는 설정이 없어야 한다.

> **황금 규칙**
> 1. **비밀값(비밀번호·시크릿)은 저장소에 절대 넣지 않는다.** 운영 서버의 환경변수/시크릿에만 둔다. 로컬은 user-secrets·`.env.local`·`appsettings.Development.json`(개발 전용 값만).
> 2. **그룹웨어(tims)·ERP는 어디서도 읽기 전용.** dyone DB만 쓴다. (쓰기도 dyone 전용 테이블/연결만)
> 3. **새 기능을 추가하거나 로컬·운영이 달라지는 설정을 건드리면, 반드시 이 문서(차이)와 [DEPLOY-PENDING.md](DEPLOY-PENDING.md)(할 일)를 같이 갱신한다.** ← 앞으로 하는 모든 작업 포함.

관련 체크리스트(아직 운영에 반영 안 한 구체 작업): **[DEPLOY-PENDING.md](DEPLOY-PENDING.md)**

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
| (SmartLab) `SLAB_SECRET`, `DYONE_SSO_SECRET`, `PG*` | SmartLab 서버 | 로컬 임시값 | 운영 시크릿(아래 7장) |

---

## 4. DB 연결·쓰기 권한

- **읽기**: API는 `ConnectionStrings:DyOne`(읽기 전용, `default_transaction_read_only=on`). 운영은 `.111:12678`.
- **쓰기(dyone 자체 데이터만)**: 사용자 편집(진행 단계·프로젝트 문서 등)은 읽기 연결에서 read-only 옵션만 제거해 쓴다. Worker 미러 적재는 `DyOneWrite`/`DyOneWriteProd`.
- ⚠️ **운영 DB 접속 계정에 아래 테이블 INSERT/UPDATE 권한 필요**: `project.project_stage`, `project.project_file`(+ 앞으로 추가되는 dyone 자체 테이블), 미러 테이블 전체.

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

*갱신: 새 기능·설정 변경 때마다 이 표와 DEPLOY-PENDING을 같이 수정할 것.*
