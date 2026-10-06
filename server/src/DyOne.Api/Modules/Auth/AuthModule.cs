using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using DyOne.Api.Platform;
using DyOne.Data;
using DyOne.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DyOne.Api.Modules.Auth;

public class AuthOptions
{
    public string Issuer { get; set; } = "dyone-internal";
    public string Audience { get; set; } = "dyone-internal";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 480;
    public GroupwareOptions Groupware { get; set; } = new();
}

/// <summary>그룹웨어 로그인 API 설정. dy-chat-agent-app(src/modules/auth.js)과 같은 방식이다.</summary>
public class GroupwareOptions
{
    public string LoginUrl { get; set; } = "https://gw.doolim-yaskawa.com/api/login";
    public string CompanyId { get; set; } = "10";
    /// <summary>로그인 성공 시 그룹웨어가 내려주는 SSO 쿠키 이름</summary>
    public string SuccessCookie { get; set; } = "GOSSOcookie";
}

public record LoginRequest(string LoginId, string Password);
public record UserDto(string Id, string EmpNo, string LoginId, string Name, string Position, string DisplayName, string Department, string Role);
public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

public enum LoginFailure { None, InvalidCredentials, NotEmployee, GroupwareUnavailable }

/// <summary>
/// 그룹웨어 인증 API로 아이디·비밀번호를 검증한다. 그룹웨어 쿠키는 성공 확인에만 쓰고 저장·전달하지 않는다.
/// 성공 판단: HTTP 2xx/3xx + 유효한 GOSSOcookie(삭제 표시 'deleteMe' 아님).
/// 주의: 그룹웨어는 로그인 실패 시에도 HTTP 400과 함께 GOSSOcookie 세션값을 내려주고 'GOSSOcookie=deleteMe'로 바로 삭제한다.
///       따라서 쿠키 존재만으로 성공을 판단하면 실패를 성공으로 오인한다(상태 코드로 판별해야 한다).
/// 비밀번호는 로그에 남기지 않는다.
/// </summary>
public class GroupwareAuthClient(HttpClient http, IOptions<AuthOptions> options, ILogger<GroupwareAuthClient> logger)
{
    public async Task<LoginFailure> VerifyAsync(string loginId, string password, CancellationToken ct)
    {
        var gw = options.Value.Groupware;
        try
        {
            using var res = await http.PostAsJsonAsync(gw.LoginUrl, new
            {
                companyId = gw.CompanyId,
                username = loginId,
                password,
                captcha = "",
                returnUrl = "",
            }, ct);

            var status = (int)res.StatusCode;
            if (status >= 500)
            {
                logger.LogWarning("그룹웨어 로그인 서버 오류 {Status}", status);
                return LoginFailure.GroupwareUnavailable;
            }
            // 4xx(특히 400)는 인증 실패. 실패 응답에도 GOSSOcookie가 붙으므로 상태 코드를 먼저 본다.
            if (status >= 400)
            {
                logger.LogInformation("그룹웨어 로그인 실패 {LoginId} (status {Status})", loginId, status);
                return LoginFailure.InvalidCredentials;
            }

            var hasValidCookie = res.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c =>
            {
                if (!c.StartsWith(gw.SuccessCookie + "=", StringComparison.Ordinal)) return false;
                var value = c.Split(';', 2)[0][(gw.SuccessCookie.Length + 1)..];
                return value.Length > 0 && !value.Equals("deleteMe", StringComparison.OrdinalIgnoreCase);
            });
            if (!hasValidCookie) logger.LogInformation("그룹웨어 로그인 실패 {LoginId} (유효 쿠키 없음, status {Status})", loginId, status);
            return hasValidCookie ? LoginFailure.None : LoginFailure.InvalidCredentials;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "그룹웨어 로그인 서버 연결 실패");
            return LoginFailure.GroupwareUnavailable;
        }
    }
}

/// <summary>직원 정보(org.employee)를 화면용 사용자로 바꾸고, org.role_mapping으로 역할을 정한다.</summary>
public class UserDirectory(DyOneDbContext db)
{
    public async Task<UserDto?> FindAsync(long employeeId, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == employeeId && x.IsActive, ct);
        return e is null ? null : await ToDtoAsync(e, ct);
    }

    /// <summary>그룹웨어 로그인 ID → 직원. org.employee.login_id가 그룹웨어 계정과 같은 식별키다.</summary>
    public async Task<UserDto?> FindByLoginIdAsync(string loginId, CancellationToken ct)
    {
        var id = loginId.Trim().ToLowerInvariant();
        var e = await db.Employees.FirstOrDefaultAsync(x => x.IsActive && x.LoginId != null && x.LoginId.ToLower() == id, ct);
        return e is null ? null : await ToDtoAsync(e, ct);
    }

    private async Task<UserDto> ToDtoAsync(Employee e, CancellationToken ct)
    {
        var dept = e.DepartmentId is { } d ? (await db.Departments.FirstOrDefaultAsync(x => x.Id == d, ct))?.Name ?? "" : "";
        var pos = e.PositionId is { } p ? (await db.Positions.FirstOrDefaultAsync(x => x.Id == p, ct))?.Name ?? "" : "";
        var mappings = await db.RoleMappings.Where(m => m.IsActive).ToListAsync(ct);
        return new UserDto(e.Id.ToString(), e.EmpNo, e.LoginId ?? "", e.Name, pos, $"{e.Name} {pos}".Trim(), dept, ResolveRole(e, mappings));
    }

    // (부서+직책) > (부서만) > (직책만) — 가장 구체적인 매핑 우선
    private static string ResolveRole(Employee e, List<RoleMapping> mappings) =>
        (mappings.FirstOrDefault(m => m.DepartmentId == e.DepartmentId && m.PositionId == e.PositionId && m.DepartmentId != null && m.PositionId != null)
         ?? mappings.FirstOrDefault(m => m.DepartmentId == e.DepartmentId && m.DepartmentId != null && m.PositionId == null)
         ?? mappings.FirstOrDefault(m => m.PositionId == e.PositionId && m.PositionId != null && m.DepartmentId == null))
        ?.RoleKey ?? "user";
}

public class TokenService(IOptions<AuthOptions> options)
{
    public LoginResponse Issue(UserDto user)
    {
        var o = options.Value;
        var expires = DateTimeOffset.UtcNow.AddMinutes(o.AccessTokenMinutes);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("login_id", user.LoginId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, expires: expires.UtcDateTime, signingCredentials: creds);
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, user);
    }
}

public static class AuthModule
{
    public const string LoginRateLimit = "login";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, GroupwareAuthClient groupware, UserDirectory users, TokenService tokens, AuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LoginId) || string.IsNullOrEmpty(req.Password))
                return Results.Problem("아이디와 비밀번호를 입력해주세요.", statusCode: 400);

            var loginId = req.LoginId.Trim();
            switch (await groupware.VerifyAsync(loginId, req.Password, ct))
            {
                case LoginFailure.GroupwareUnavailable:
                    return Results.Problem("그룹웨어 로그인 서버에 연결하지 못했습니다. 잠시 후 다시 시도해주세요.", statusCode: 502);
                case LoginFailure.InvalidCredentials:
                    audit.Record("login.fail", "user", loginId, "그룹웨어 인증 실패");
                    return Results.Problem("아이디 또는 비밀번호를 확인해주세요.", statusCode: 401);
            }

            var user = await users.FindByLoginIdAsync(loginId, ct);
            if (user is null)
            {
                audit.Record("login.fail", "user", loginId, "그룹웨어 인증 성공 · 조직도(org.employee)에 없음");
                return Results.Problem("그룹웨어 인증은 됐지만 조직도에서 직원 정보를 찾지 못했습니다. 관리자에게 문의해주세요.", statusCode: 403);
            }

            audit.Record("login", "user", user.Id, user.DisplayName);
            return Results.Ok(tokens.Issue(user));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        g.MapGet("/me", async (ClaimsPrincipal p, UserDirectory users, CancellationToken ct) =>
            long.TryParse(p.Current().Id, out var id) && await users.FindAsync(id, ct) is { } u ? Results.Ok(u) : Results.Unauthorized());
    }
}
