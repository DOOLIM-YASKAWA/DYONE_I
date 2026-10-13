using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DyOne.Api.Modules.Auth;

namespace DyOne.Api.Modules.Integrations;

/// <summary>
/// SmartLab 임베드용 SSO 핸드오프(B). 로그인된 dyone 사용자의 사번을 공유 시크릿으로 서명한
/// 단기 토큰을 발급한다. 프론트는 이 핸드오프 URL을 SmartLab iframe src로 걸어, SmartLab이
/// /sso/dyone 에서 토큰을 검증하고 자체 세션(SLAB_SESS)을 발급하도록 한다.
/// (그룹웨어 재로그인 리다이렉트 회피. 운영은 리버스 프록시 뒤에서 동일 경로 사용.)
/// 시크릿: SmartLabSso:Secret (SmartLab 서버의 DYONE_SSO_SECRET 와 동일 값).
/// </summary>
public static class SmartLabModule
{
    public static void MapSmartLab(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/smartlab").WithTags("SmartLab").RequireAuthorization();

        g.MapGet("/token", async (ClaimsPrincipal principal, UserDirectory users, IConfiguration config, CancellationToken ct) =>
        {
            var secret = config["SmartLabSso:Secret"];
            var baseUrl = config["SmartLabSso:BaseUrl"] ?? "http://localhost:4174";
            if (string.IsNullOrEmpty(secret)) return Results.Problem("SmartLabSso:Secret 미설정", statusCode: 500);

            var idStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!long.TryParse(idStr, out var empId)) return Results.Unauthorized();
            var user = await users.FindAsync(empId, ct);
            if (user is null || string.IsNullOrEmpty(user.EmpNo)) return Results.Unauthorized();

            var exp = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(); // 짧은 TTL(재생 방지)
            var payload = $"{user.EmpNo}.{exp}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var sig = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            var token = $"{payload}.{sig}";
            return Results.Ok(new { handoffUrl = $"{baseUrl}/sso/dyone?token={Uri.EscapeDataString(token)}", baseUrl });
        });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
