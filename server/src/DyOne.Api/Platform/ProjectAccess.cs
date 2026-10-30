using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace DyOne.Api.Platform;

/// <summary>
/// 협력사 접근 제어(docs/partner-access). 기본은 직원 전용, 협력사는 허용 목록 API + 배정된 프로젝트만.
/// - 정책 staff: Default·Fallback 정책. 아무것도 안 건 엔드포인트와 인자 없는 RequireAuthorization()이 모두 직원 전용이 된다.
/// - 정책 partner-ok: 협력사도 받는 엔드포인트에만 <see cref="PartnerAllowed"/>로 붙인다(배정 확인 필터가 같이 걸린다).
///   그룹 정책과 엔드포인트 정책은 AND라서, 허용 엔드포인트가 있는 그룹에는 그룹 수준 RequireAuthorization()을 걸지 않는다.
/// </summary>
public static class ProjectAccess
{
    public const string StaffPolicy = "staff";
    public const string PartnerOkPolicy = "partner-ok";

    /// <summary>협력사에게 열린 엔드포인트(메서드 + 경로). 시작할 때 실제 목록과 비교해 다르면 경고한다.</summary>
    public static readonly string[] PartnerAllowList =
    [
        "GET /api/auth/me",
        "GET /api/codes/{group}",
        "GET /api/projects",
        "GET /api/projects/lookup",
        "GET /api/projects/{id:long}",
        "GET /api/projects/{id:long}/stage-history",
        "GET /api/projects/{id:long}/members",
        "GET /api/projects/{projectId:long}/files",
        "GET /api/projects/{projectId:long}/files/{fileId:long}/download",
    ];

    public static AuthorizationBuilder AddProjectAccessPolicies(this AuthorizationBuilder b)
    {
        var staff = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .RequireAssertion(ctx => !ctx.User.IsPartner()).Build();
        return b.AddPolicy(StaffPolicy, staff)
            .AddPolicy(PartnerOkPolicy, p => p.RequireAuthenticatedUser())
            .SetDefaultPolicy(staff)
            .SetFallbackPolicy(staff);
    }

    /// <summary>협력사도 받는다. 라우트에 프로젝트 id(id/projectId)가 있으면 배정 여부를 확인한다(직원은 그대로 통과).</summary>
    public static TBuilder PartnerAllowed<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(PartnerOkPolicy).AddEndpointFilter(ProjectScopeFilter);

    /// <summary>배정 안 된 프로젝트는 403이 아니라 404 — 있는지조차 드러내지 않는다.</summary>
    private static async ValueTask<object?> ProjectScopeFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!http.User.IsPartner()) return await next(ctx);
        var raw = http.Request.RouteValues.TryGetValue("projectId", out var v) ? v : http.Request.RouteValues.GetValueOrDefault("id");
        var scope = http.RequestServices.GetRequiredService<ProjectScope>();
        var mine = await scope.AssignedAsync(http.User, http.RequestAborted);
        if (mine is null) return Results.Unauthorized(); // 주소록에서 빠졌거나 회사 로그인 중지
        if (raw is null) return await next(ctx);
        return long.TryParse(raw.ToString(), out var pid) && mine.ContainsKey(pid) ? await next(ctx) : Results.NotFound();
    }

    /// <summary>개발 환경 시작 시: partner-ok가 붙은 실제 엔드포인트가 허용 목록과 같은지 확인.</summary>
    public static void WarnIfPartnerSurfaceChanged(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var actual = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == PartnerOkPolicy))
                .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(m => $"{m} {e.RoutePattern.RawText?.TrimEnd('/')}"))
                .ToHashSet();
            var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectAccess");
            var extra = actual.Except(PartnerAllowList).ToList();
            var missing = PartnerAllowList.Except(actual).ToList();
            if (extra.Count + missing.Count == 0) log.LogInformation("협력사 허용 API {Count}개 — 허용 목록과 같음", actual.Count);
            else log.LogWarning("협력사 허용 API가 허용 목록과 다름. 추가로 열린 것: {Extra} / 목록에만 있는 것: {Missing}",
                string.Join(", ", extra), string.Join(", ", missing));
        });
    }
}

/// <summary>
/// 협력사가 볼 수 있는 프로젝트 = project.project_member에 배정(활성)된 진행 프로젝트(project.is_active).
/// 사람·회사가 살아 있고(주소록에 있음) 회사 로그인 중지가 아닐 때만. 요청마다 한 번 읽어 HttpContext에 둔다.
/// </summary>
public class ProjectScope(IConfiguration config, IHttpContextAccessor http)
{
    private const string ItemKey = "partner-assigned";

    /// <summary>직원이면 null(전체), 협력사면 배정된 프로젝트 id(없으면 빈 집합).</summary>
    public async Task<List<long>?> VisibleIdsAsync(ClaimsPrincipal user, CancellationToken ct) =>
        !user.IsPartner() ? null : (await AssignedAsync(user, ct))?.Keys.ToList() ?? [];

    /// <summary>협력사의 배정(프로젝트 id → pm/member). 사람·회사가 막혔으면 null.</summary>
    public async Task<IReadOnlyDictionary<long, string>?> AssignedAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var items = http.HttpContext?.Items;
        if (items?[ItemKey] is IReadOnlyDictionary<long, string> cached) return cached;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var personId)) return null;
        var result = await LoadAsync(config, personId, ct);
        if (items is not null && result is not null) items[ItemKey] = result;
        return result;
    }

    /// <summary>로그인에서도 쓴다(배정된 진행 프로젝트가 1개 이상이어야 로그인).</summary>
    public static async Task<Dictionary<long, string>?> LoadAsync(IConfiguration config, long personId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
        await conn.OpenAsync(ct);
        await using (var ok = new NpgsqlCommand(@"
            select 1 from partner.person ps join partner.company c on c.id = ps.company_id
            where ps.id = @id and ps.is_active and c.is_active and not c.login_disabled", conn))
        {
            ok.Parameters.AddWithValue("id", personId);
            if (await ok.ExecuteScalarAsync(ct) is null) return null;
        }
        await using var cmd = new NpgsqlCommand(@"
            select m.project_id, m.role from project.project_member m join project.project p on p.id = m.project_id
            where m.partner_person_id = @id and m.is_active and p.is_active", conn);
        cmd.Parameters.AddWithValue("id", personId);
        var map = new Dictionary<long, string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetString(1);
        return map;
    }
}
