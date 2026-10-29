using Npgsql;

namespace DyOne.Api.Modules.Notifications;

/// <summary>
/// 앱(화면)별로 누가 쓰는지 = 누가 알림을 받는지 (access.app_department · app_employee, server/db/app_access.sql).
/// 받는 사람 = 그 앱을 쓰는 부서(하위 부서 포함 선택) 재직자 + 그 앱에 따로 지정한 사람 + 모든 앱에 지정한 사람(app_key='*', 개발·관리자).
/// 부서를 정하지 않은 앱은 '*' 지정자만 받는다 — 부서 권한(YTVH-432)을 줄 때 access.app_department에 넣으면 그 부서도 받는다.
/// 규칙·조직도는 자주 바뀌지 않으므로 앱마다 1분 동안 기억한다.
/// </summary>
public class AppAudience(IConfiguration config, ILogger<AppAudience> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, (DateTimeOffset At, HashSet<long>? Ids)> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    private const string Sql = """
        with recursive dept as (
            select department_id as id, include_sub from access.app_department where app_key = @app
            union
            select c.id, dept.include_sub from org.department c join dept on c.parent_id = dept.id and dept.include_sub
        )
        select e.id from org.employee e where e.is_active and e.department_id in (select id from dept)
        union
        select employee_id from access.app_employee where app_key = @app or app_key = '*'
        """;

    /// <summary>받는 사람 직원 id. null = access 스키마가 없는 DB(적용 전) — 모두에게 보낸다.</summary>
    public async Task<HashSet<long>?> RecipientsAsync(string appKey, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(appKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) return hit.Ids;
            var ids = await LoadAsync(appKey, ct);
            cache[appKey] = (DateTimeOffset.UtcNow, ids);
            return ids;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CanSeeAsync(long employeeId, string appKey, CancellationToken ct) =>
        await RecipientsAsync(appKey, ct) is not { } ids || ids.Contains(employeeId);

    /// <summary>관리자 = 모든 앱에 지정한 사람(access.app_employee app_key='*'). access 스키마가 없으면 아무도 아니다.</summary>
    public async Task<bool> IsAdminAsync(long employeeId, CancellationToken ct) =>
        await RecipientsAsync("*", ct) is { } ids && ids.Contains(employeeId);

    private async Task<HashSet<long>?> LoadAsync(string appKey, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(Sql, conn);
            cmd.Parameters.AddWithValue("app", appKey);
            var ids = new HashSet<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
            return ids;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // access 스키마 적용 전(server/db/app_access.sql): 받는 사람을 가릴 수 없으므로 모두
            logger.LogDebug("access.app_department 없음 — 앱별 받는 사람 규칙 없이 모두에게 보낸다");
            return null;
        }
    }
}
