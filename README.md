# DY ONE 내부용

두림야스카와 임직원용 업무 통합 플랫폼. 사내 서버의 ASP.NET Core 백엔드 하나에 PC 웹 · Windows 앱 · 모바일 앱이 같은 React 화면으로 접속한다.
설계 근거는 `E:\dy-one\DY_ONE_내부용_설계안.md`(설계안)와 `DY_ONE_내부용_설계_논의_정리.md`(논의 정리)에 있다.

> **현재 단계:** 기존 **dyone DB(PostgreSQL 18)를 읽기 전용으로 연결**했다. 화면 디자인은 바탕화면 DY ONE Demo와 같다.
> 그룹웨어 인증·ERP·로컬 LLM·Google Drive/GCS·FCM·실제 MotoSim 실행은 아직 연결하지 않았다.

## 데이터

| 화면 | dyone DB 원천 | 상태 |
| --- | --- | --- |
| 로그인(그룹웨어 계정) · 역할 | 그룹웨어 인증 API + `org.employee`(login_id), `org.department`, `org.position`, `org.role_mapping` | 연결 |
| 홈 · 프로젝트 진행 상태 | `project.project`(그룹웨어 applet285 미러) + `code.code`(project_status · project_type) | 연결(읽기) |
| 공정 배당 | `project.project_assignment` → `plant.zone` → `booth` → `factory` → `customer` | 연결(읽기) |
| 랩 테스트 | `smartlab.lab_test` | 연결(읽기) |
| 원천 적재 상태 | `sync.state` | 연결(읽기, Worker) |
| 모듈라(376) · 업무일지(296) · ES 이슈(281) | 없음 | 미연결 — DY ONE 전용 스키마 확정 후 |
| IT 요청 · 식단 · 방문 차량 · 알림함 · 감사 로그 | 없음 | 미연결 — 알림은 실시간 전송만, 감사는 서버 로그만 |

- **읽기 전용을 두 겹으로 막는다:** 연결 문자열 `default_transaction_read_only=on`(DB가 쓰기 거부) + `DyOneDbContext.SaveChanges` 차단.
- dyone DB는 SmartLab·SRS 등과 함께 쓰는 정본이라 **EF 마이그레이션을 만들지 않는다.** DY ONE 전용 테이블은 스키마 소유자(팀장님)와 확정 후 추가한다.

## 구성

| 폴더 | 내용 | 기술 |
| --- | --- | --- |
| `server/src/DyOne.Api` | REST API + SignalR(실시간 알림), 인증(JWT), 권한, AI(규칙형) | ASP.NET Core .NET 10 |
| `server/src/DyOne.Data` | dyone DB 기존 테이블 매핑(읽기 전용) | EF Core 10 + Npgsql |
| `server/src/DyOne.Worker` | 주기 작업. **GW 프로젝트 동기화(매일 08:00)** + 추후 모바일 푸시(FCM) | .NET 10 Worker |
| `web` | 세 채널 공통 화면 | React 19.3 + Vite 8 + TypeScript |
| `desktop` | Windows 앱: WPF 셸 + WebView2 + 브리지 + Local Broker 골격 | .NET 10 WPF |
| `mobile` | 모바일 앱: `web` 빌드를 감싼 Capacitor 프로젝트(Android 생성 완료) | Capacitor 8 |

## GW 프로젝트 동기화 Worker (매일 08:00)

`DyOne.Worker`가 매일 08:00(KST)에 그룹웨어 WORKS applet 285(기초정보-프로젝트코드 관리)를 조회해 `dyone` DB `project.project` 미러를 갱신한다.

```
GW tims(EAV, 조회 전용) → applet285 피벗(코드·프로젝트명·발주처·ERP날짜·상태)
  → project.project upsert(code 기준) · 없어진 DLP는 soft-delete · sync.state 기록
```

- **원천(GW):** tims(121.139.49.10:5432, 조회 전용). 상태 0~3 → `code.code` project_status의 sort_order로 매핑. DLP 접두어만 취급.
- **대상(dyone):** 쓰기 연결 `ConnectionStrings:DyOneWrite`. API는 읽기 전용 그대로, **쓰기는 이 Worker만** 한다(원천은 읽기만, 미러만 쓴다는 원칙과 일치).
- **대상 DB 주의:** 개발은 dev 복사본(localhost:5433). 운영(.111:12678)은 건드리지 않는다.
- 설정: `GwProjectSync` (DailyAt · TimeZone · RunOnStartup · DryRun), `appsettings.Development.json`.

**연결 정보(user-secrets, 저장소에 넣지 않음)**

```powershell
cd server
# dyone 쓰기 연결(개발: dev 복사본)
dotnet user-secrets set "ConnectionStrings:DyOneWrite" "Host=localhost;Port=5433;Database=dyone;Username=postgres;Password=<dev-pw>" --project src/DyOne.Worker
# 그룹웨어 tims 읽기 연결 — 비밀번호만 실제 값으로 교체
dotnet user-secrets set "ConnectionStrings:Gw" "Host=121.139.49.10;Port=5432;Database=tims;Username=mailadm;Password=<gw-pw>" --project src/DyOne.Worker
```

**실행**

```powershell
cd server
dotnet run --project src/DyOne.Worker          # 매일 08:00 대기
```

바로 확인하려면 `appsettings.Development.json`에서 `GwProjectSync:RunOnStartup=true`(시작 즉시 1회), `DryRun=true`(조회·집계만, 적재 안 함)로 두고 실행한다.

## 로그인 (그룹웨어 인증)

dy-chat-agent-app(`src/modules/auth.js`)과 같은 방식이다.

```
아이디·비밀번호 → 서버가 그룹웨어 로그인 API(gw.doolim-yaskawa.com/api/login, companyId=10)로 검증
  → org.employee.login_id 로 직원 확인 → DY ONE 자체 JWT 발급
```

- 그룹웨어 비밀번호는 저장·전달하지 않고 검증에만 쓴다. 그룹웨어 쿠키(GOSSOcookie)도 성공 확인용으로만 읽는다.
- **성공 판단은 HTTP 상태로 한다(쿠키 존재 아님).** 그룹웨어는 로그인 실패(HTTP 400) 응답에도 `GOSSOcookie` 세션값을 내려주고 곧바로 `GOSSOcookie=deleteMe`로 삭제하므로, 쿠키 존재만 보면 실패를 성공으로 오인한다. 성공 = 2xx/3xx + 유효한(=deleteMe 아님) GOSSOcookie.
- 로그인 무차별 대입 방지: IP당 1분 10회 제한.
- 로그인하려면 org.employee에 해당 login_id가 있어야 한다(현재 재직자 150명 모두 등록됨).

## 실행

필요: .NET 10 SDK, Node 22, Docker Desktop(기존 `dyone_db` 컨테이너 실행 중), 그룹웨어 서버 접근 가능한 네트워크

**처음 한 번: DB 연결 정보를 user-secrets에 등록**(저장소에 비밀번호를 남기지 않는다)

```powershell
$cs = "Host=localhost;Port=5433;Database=dyone;Username=postgres;Password=<비밀번호>;Options=-c default_transaction_read_only=on"
cd server
dotnet user-secrets set "ConnectionStrings:DyOne" $cs --project src/DyOne.Api
dotnet user-secrets set "ConnectionStrings:DyOne" $cs --project src/DyOne.Worker
```

**실행**

```powershell
.\dev.ps1            # API(5080) + 웹(5173)
.\dev.ps1 -Desktop   # 위 + Windows 앱
```

브라우저에서 http://localhost:5173 → 개발용 로그인(직원 선택).

## 화면과 API

| 화면 | API |
| --- | --- |
| 로그인(개발용) | `POST /api/auth/login`, `GET /api/auth/dev-users`, `GET /api/auth/me` |
| 홈 | `GET /api/home/summary` |
| 프로젝트 | `GET /api/projects?search=&statusId=&page=`, `GET /api/projects/lookup?search=`, `GET /api/projects/{id}`(공정 배당·앱·랩 건수), `GET /api/codes/{group}` |
| 랩 테스트 | `GET /api/lab-tests?projectId=&page=` |
| 프로젝트 AI · AI 챗봇 | `POST /api/ai/ask` (projectId 또는 질문 속 프로젝트 코드로 범위 지정) |
| 알림 | 허브 `/hubs/notifications`, `POST /api/notifications/test`(개발용) |

## 다음에 바꿀 곳

| 항목 | 지금 | 바꿀 곳 |
| --- | --- | --- |
| 모듈라·업무일지·ES·IT·알림함 등 | 화면 자리만 | dyone DB에 DY ONE 전용 스키마 확정 → 테이블 매핑·쓰기 허용 범위 결정 |
| AI | `RuleBasedAssistant`(dyone DB 조회) | 로컬 LLM + pgvector RAG(권한 필터)로 교체 |
| 모바일 푸시 | 없음(웹·Windows는 SignalR) | `PushDispatchJob`을 FCM 발송으로 |
| Windows 앱 Broker | 폴더 선택·설치 확인만 | Cell 업로드(복사본 처리)·MotoSim 실행·Plugin 설치 이식 |

## 참고

- 프로젝트 데이터에 **담당자 항목이 없다**(applet285 미러). 담당자 원천이 정해지면 추가한다.
- `org.role_mapping`이 비어 있어 지금은 모든 직원의 역할이 `user`다.
- 참고: dy-chat-agent-app의 로그인은 GOSSOcookie 존재만 확인해 실패를 성공으로 오인하는 허점이 있다. 여기서는 HTTP 상태로 판별하도록 고쳤다(위 로그인 절 참고).
- `appsettings.Development.json`의 서명 키는 로컬 개발 전용이다. 운영 값은 환경 변수로 넣는다.
- `mobile`의 `npm audit` 경고 3건(moderate)은 Capacitor CLI 빌드 도구 의존성이며 앱에 포함되지 않는다.
- 앱 아이콘(`dy-one.ico`)은 WPF가 읽지 못하는 형식이라 정식 아이콘을 새로 만들어야 한다.
