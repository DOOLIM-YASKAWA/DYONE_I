using DyOne.Api.Modules.Integrations;
using System.Text;
using System.Text.Json.Serialization;
using DyOne.Api.Modules.Ai;
using DyOne.Api.Modules.Demo;
using DyOne.Api.Modules.Auth;
using DyOne.Api.Modules.Home;
using DyOne.Api.Modules.Modular;
using DyOne.Api.Modules.Works;
using DyOne.Api.Modules.Notifications;
using DyOne.Api.Modules.Projects;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- 데이터: 기존 dyone DB(PostgreSQL 18)를 읽기 전용으로 사용. 개발 연결 정보는 user-secrets ---
builder.Services.AddDbContext<DyOneDbContext>(o => DyOneDb.Configure(o, config.GetConnectionString("DyOne")));

// --- 프로젝트 문서 업로드 저장소(운영=NAS UNC, 개발=로컬 폴더; FileStorage:Root). 대용량 설계 문서 대비 512MB 허용 ---
builder.Services.AddSingleton<DyOne.Api.Modules.Projects.FileStorage>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

// --- 사양서 분석: 문서 읽기(DyOne.Spec) + NAS 사양정리 라이브러리(읽기 전용, 캐시 파일). PDF 표 인식은 Spec:PythonPath(PyMuPDF)가 있으면 사용 ---
builder.Services.AddSingleton(new DyOne.Spec.Extraction.SpecExtractor(new DyOne.Spec.Extraction.SpecExtractorOptions
{
    PythonPath = config["Spec:PythonPath"],
}));
builder.Services.AddSingleton<DyOne.Api.Modules.Spec.SpecLibrary>();
// 사양정리 초안: 설비 이력 색인(NAS 도면 제목·사양정리 표, 캐시 파일) + 사내 AI(Llm:BaseUrl·Model·ApiKey, 키 없으면 규칙 초안)
builder.Services.AddSingleton<DyOne.Api.Modules.Spec.SpecEquipment>();
builder.Services.AddHttpClient("llm");
builder.Services.AddSingleton<DyOne.Api.Modules.Spec.SpecAi>();

// --- 인증: 그룹웨어 로그인 API로 검증 → org.employee(login_id)로 직원 확인 → DY ONE 자체 JWT ---
var authSection = config.GetSection("Auth");
builder.Services.Configure<AuthOptions>(authSection);
var auth = authSection.Get<AuthOptions>() ?? new AuthOptions();
if (auth.SigningKey.Length < 32)
    throw new InvalidOperationException("Auth:SigningKey가 없거나 너무 짧습니다(32자 이상). 운영에서는 환경 변수로 지정하세요.");

builder.Services.AddHttpClient<GroupwareAuthClient>(c => c.Timeout = TimeSpan.FromSeconds(15))
    // 리다이렉트를 따라가지 않아야 로그인 응답의 Set-Cookie(GOSSOcookie)를 확인할 수 있다.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<UserDirectory>();
builder.Services.AddSingleton<TokenService>();
// 무차별 대입 방지: 같은 IP에서 1분에 10회까지 로그인 시도
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthModule.LoginRateLimit, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = auth.Issuer,
        ValidAudience = auth.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.SigningKey)),
        ClockSkew = TimeSpan.FromMinutes(1),
    };
    // SignalR은 WebSocket 연결 시 헤더를 못 붙이므로 허브 경로에 한해 쿼리의 토큰을 받는다.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token))
                ctx.Token = token;
            return Task.CompletedTask;
        },
    };
});
// 명시적으로 AllowAnonymous가 없는 모든 엔드포인트는 로그인 필요 + 기본은 직원 전용.
// 협력사(kind=partner)는 .PartnerAllowed()를 붙인 엔드포인트와 배정된 프로젝트만(Platform/ProjectAccess.cs, docs/partner-access)
builder.Services.AddAuthorizationBuilder().AddProjectAccessPolicies();
builder.Services.AddScoped<ProjectScope>();
builder.Services.AddScoped<DyOne.Api.Modules.Partners.PartnerDirectory>(); // 협력사 로그인(주소록 이메일 + 회사 공용 비밀번호)

// --- 공통 ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// --- 모듈 서비스 ---
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<NotificationService>();
// 그룹웨어 변경 알림(Worker가 sync.change_notice에 남긴 것)을 실시간 알림으로 보낸다
builder.Services.AddSingleton<AppAudience>();
builder.Services.AddHostedService<GwChangeNoticeRelay>();
builder.Services.AddScoped<IAiAssistant, RuleBasedAssistant>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapAuth();
app.MapHome();
app.MapProjects();
app.MapProjectFiles();
DyOne.Api.Modules.Spec.SpecModule.MapSpec(app);
DyOne.Api.Modules.Spec.SpecDraftModule.MapSpecDraft(app);
DyOne.Api.Modules.Spec.SpecDeptModule.MapSpecDept(app);
DyOne.Api.Modules.Org.OrgModule.MapOrg(app);
app.MapProjectTestData();
app.MapModular();
app.MapWorks();
app.MapAi();
app.MapNotifications();
app.MapSmartLab();
app.MapSrsAnalyzer();
app.MapProcess();
DyOne.Api.Modules.Meals.MealsModule.MapMeals(app); // 수담푸드 식단(Worker 캐시 파일 읽기)
DyOne.Api.Modules.Motosim.MotosimModule.MapMotosim(app); // MotoSim Launcher 이관 1단계(조회)
app.MapDemoMemos(config); // [임시 · 데모] 화면 메모(포스트잇). DemoMemo:Enabled일 때만
app.MapHub<NotificationHub>("/hubs/notifications");
DyOne.Api.Modules.Partners.PartnersModule.MapPartners(app); // 협력사 회사별 공용 비밀번호(관리자)
if (app.Environment.IsDevelopment()) ProjectAccess.WarnIfPartnerSurfaceChanged(app);

app.Run();
