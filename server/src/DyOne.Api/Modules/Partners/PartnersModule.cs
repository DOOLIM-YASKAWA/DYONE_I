using System.Collections.Concurrent;
using System.Security.Claims;
using DyOne.Api.Modules.Auth;
using DyOne.Api.Modules.Notifications;
using DyOne.Api.Platform;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace DyOne.Api.Modules.Partners;

public record PartnerCompanyDto(long Id, string Name, bool IsActive, int People, int Assigned, bool HasPassword,
    DateTimeOffset? PasswordChangedAt, string? PasswordChangedBy, bool LoginDisabled);
public record PartnerPasswordSet(long[] CompanyIds, string Password);
public record PartnerLoginDisabledSet(bool Disabled);

public enum PartnerLoginResult { NotPartner, Ok, InvalidCredentials, Locked, Blocked, NoAssignment }

/// <summary>
/// 협력사 로그인(docs/partner-access 3-2). 아이디 = 주소록 이메일(partner.person), 비밀번호 = 그 회사의 공용 비밀번호(해시만 저장).
/// 사람·회사가 살아 있고(주소록에 있음), 회사 로그인 중지가 아니고, 배정된 진행 프로젝트가 1개 이상일 때만 통과.
/// </summary>
public class PartnerDirectory(IConfiguration config, ILogger<PartnerDirectory> logger)
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashUser = new();
    // 계정별 실패 제한(IP별 제한은 로그인 rate limit). 공용 비밀번호라 사람 하나보다 회사 전체가 걸린 값이다.
    private static readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> Failures = new();
    private const int MaxFails = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    public static string Hash(string password) => Hasher.HashPassword(HashUser, password);

    /// <summary>아이디가 협력사 이메일이 아니면 NotPartner(그룹웨어 로그인으로 넘긴다).</summary>
    public async Task<(PartnerLoginResult Result, UserDto? User, long CompanyId)> LoginAsync(string loginId, string password, CancellationToken ct)
    {
        var email = loginId.Trim().ToLowerInvariant();
        if (!email.Contains('@')) return (PartnerLoginResult.NotPartner, null, 0);

        await using var conn = await OpenAsync(ct);
        long personId, companyId;
        string name, companyName, hash;
        string? dept, pos;
        bool ok;
        await using (var cmd = new NpgsqlCommand(@"
            select ps.id, ps.company_id, ps.name, ps.department_name, ps.position_name, c.name, c.password_hash,
                   ps.is_active and c.is_active and not c.login_disabled
            from partner.person ps join partner.company c on c.id = ps.company_id
            where lower(ps.email) = @email
            order by ps.is_active desc limit 1", conn))
        {
            cmd.Parameters.AddWithValue("email", email);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return (PartnerLoginResult.NotPartner, null, 0);
            (personId, companyId, name) = (r.GetInt64(0), r.GetInt64(1), r.GetString(2));
            dept = r.IsDBNull(3) ? null : r.GetString(3);
            pos = r.IsDBNull(4) ? null : r.GetString(4);
            companyName = r.GetString(5);
            hash = r.IsDBNull(6) ? "" : r.GetString(6);
            ok = r.GetBoolean(7);
        }

        if (Failures.TryGetValue(email, out var f) && f.Fails >= MaxFails && f.Until > DateTimeOffset.UtcNow)
            return (PartnerLoginResult.Locked, null, companyId);
        if (hash.Length == 0 || Hasher.VerifyHashedPassword(HashUser, hash, password) == PasswordVerificationResult.Failed)
        {
            Failures.AddOrUpdate(email, _ => (1, DateTimeOffset.UtcNow + LockFor),
                (_, old) => (old.Until > DateTimeOffset.UtcNow ? old.Fails + 1 : 1, DateTimeOffset.UtcNow + LockFor));
            return (PartnerLoginResult.InvalidCredentials, null, companyId);
        }
        Failures.TryRemove(email, out _);
        if (!ok) return (PartnerLoginResult.Blocked, null, companyId);

        var assigned = await ProjectScope.LoadAsync(config, personId, ct);
        if (assigned is not { Count: > 0 }) return (PartnerLoginResult.NoAssignment, null, companyId);

        try
        {
            await using var w = await OpenWriteAsync(ct);
            await using var touch = new NpgsqlCommand("update partner.person set last_login_at = now() where id = @id", w);
            touch.Parameters.AddWithValue("id", personId);
            await touch.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is NpgsqlException) { logger.LogWarning(ex, "협력사 마지막 로그인 기록 실패"); }

        return (PartnerLoginResult.Ok, ToDto(personId, email, name, pos, dept, companyName), companyId);
    }

    /// <summary>/api/auth/me — 토큰이 살아 있어도 주소록에서 빠졌거나 회사가 중지되면 null.</summary>
    public async Task<UserDto?> FindAsync(long personId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            select ps.email, ps.name, ps.position_name, ps.department_name, c.name
            from partner.person ps join partner.company c on c.id = ps.company_id
            where ps.id = @id and ps.is_active and c.is_active and not c.login_disabled", conn);
        cmd.Parameters.AddWithValue("id", personId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return ToDto(personId, r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
    }

    private static UserDto ToDto(long id, string email, string name, string? pos, string? dept, string company) =>
        new(id.ToString(), "", email, name, pos ?? "", $"{name} {pos}".Trim(), dept is null ? company : $"{company} · {dept}", "partner",
            Kind: CurrentUser.Partner);

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>읽기 전용 연결에서 read-only 옵션만 뺀 쓰기 연결(마지막 로그인·비밀번호·중지 — DY ONE 자체 데이터)</summary>
    public async Task<NpgsqlConnection> OpenWriteAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>관리자: 협력사 회사별 공용 비밀번호 설정 · 로그인 중지. 관리자 = access.app_employee app_key='*'.</summary>
public static class PartnersModule
{
    public static void MapPartners(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/partners").WithTags("Partners"); // 기본 정책 = 직원 전용, 아래에서 관리자 확인

        g.MapGet("/companies", async (ClaimsPrincipal p, AppAudience audience, PartnerDirectory partners, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(p, audience, ct)) return Forbidden();
            await using var conn = await partners.OpenWriteAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                select c.id, c.name, c.is_active,
                       (select count(*) from partner.person ps where ps.company_id = c.id and ps.is_active)::int,
                       (select count(distinct m.partner_person_id) from project.project_member m join partner.person ps on ps.id = m.partner_person_id
                         where ps.company_id = c.id and m.is_active)::int,
                       c.password_hash is not null, c.password_changed_at, e.name, c.login_disabled
                from partner.company c left join org.employee e on e.id = c.password_changed_by
                order by c.is_active desc, c.id", conn);
            var list = new List<PartnerCompanyDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new PartnerCompanyDto(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5),
                    r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8)));
            return Results.Ok(list);
        });

        // 회사 공용 비밀번호 설정·교체. 여러 회사에 같은 값을 한 번에 넣을 수 있다. 원문은 해시로만 남기고 로그·감사에 쓰지 않는다.
        g.MapPost("/password", async (PartnerPasswordSet body, ClaimsPrincipal p, AppAudience audience, PartnerDirectory partners, AuditLog audit, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(p, audience, ct)) return Forbidden();
            if (body.CompanyIds is not { Length: > 0 }) return Results.BadRequest(new { message = "회사를 하나 이상 고르세요." });
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length < 8 || body.Password.Length > 100)
                return Results.BadRequest(new { message = "비밀번호는 8~100자로 정하세요." });
            await using var conn = await partners.OpenWriteAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                update partner.company set password_hash = @hash, password_changed_at = now(), password_changed_by = @by, updated_at = now()
                where id = any(@ids) and is_active", conn);
            cmd.Parameters.AddWithValue("hash", PartnerDirectory.Hash(body.Password)); // 회사마다 같은 값이어도 해시 하나를 같이 쓴다(소금 포함)
            cmd.Parameters.AddWithValue("by", long.Parse(p.Current().Id));
            cmd.Parameters.AddWithValue("ids", body.CompanyIds);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            audit.Record("partner.password", "partner.company", string.Join(",", body.CompanyIds), $"공용 비밀번호 설정 {n}개 회사");
            return Results.Ok(new { ok = true, changed = n });
        });

        g.MapPut("/companies/{id:long}/login-disabled", async (long id, PartnerLoginDisabledSet body, ClaimsPrincipal p, AppAudience audience, PartnerDirectory partners, AuditLog audit, CancellationToken ct) =>
        {
            if (!await IsAdminAsync(p, audience, ct)) return Forbidden();
            await using var conn = await partners.OpenWriteAsync(ct);
            await using var cmd = new NpgsqlCommand("update partner.company set login_disabled = @d, updated_at = now() where id = @id", conn);
            cmd.Parameters.AddWithValue("d", body.Disabled);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            audit.Record("partner.login-disabled", "partner.company", id.ToString(), body.Disabled ? "로그인 중지" : "로그인 다시 허용");
            return Results.Ok(new { ok = true });
        });
    }

    private static Task<bool> IsAdminAsync(ClaimsPrincipal p, AppAudience audience, CancellationToken ct) =>
        Spec.SpecDraftModule.IsAdminAsync(p, audience, ct);

    private static IResult Forbidden() => Results.Json(new { message = "협력사 관리는 관리자만 할 수 있습니다." }, statusCode: 403);
}
