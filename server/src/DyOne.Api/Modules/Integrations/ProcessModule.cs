using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DyOne.Api.Modules.Auth;

namespace DyOne.Api.Modules.Integrations;

// Independent process web: internal authenticates the employee; the process web validates
// this two-minute, audience-bound handoff and applies its own editor allowlist.
public static class ProcessModule
{
    public static void MapProcess(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/integrations/process").WithTags("Process").RequireAuthorization()
            .MapGet("/token", async (ClaimsPrincipal principal, UserDirectory users, IConfiguration config, CancellationToken ct) =>
            {
                var secret = config["ProcessSso:Secret"];
                if (string.IsNullOrEmpty(secret) || secret.Length < 32)
                    return Results.Problem("공정 관리 로그인 연결 설정이 없습니다.", statusCode: 503);
                var baseUrl = (config["ProcessSso:BaseUrl"] ?? "http://localhost:4176").TrimEnd('/');
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    return Results.Problem("공정 관리 주소 설정을 확인해 주세요.", statusCode: 503);
                if (!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var employeeId))
                    return Results.Unauthorized();
                var user = await users.FindAsync(employeeId, ct);
                if (user is null || string.IsNullOrEmpty(user.EmpNo)) return Results.Unauthorized();
                var payload = new
                {
                    empNo = user.EmpNo, name = user.DisplayName, role = user.Role,
                    aud = "dy-one-process-handoff", exp = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds(),
                    nonce = Guid.NewGuid().ToString("N"),
                };
                var encoded = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var token = $"{encoded}.{Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(encoded)))}";
                return Results.Ok(new { handoffUrl = $"{baseUrl}/sso/dyone?token={Uri.EscapeDataString(token)}", baseUrl });
            });
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
