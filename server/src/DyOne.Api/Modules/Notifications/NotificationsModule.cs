using System.Security.Claims;
using DyOne.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace DyOne.Api.Modules.Notifications;

/// <summary>
/// 실시간 알림 허브(서버 1대 기준, Redis backplane 없음). Clients.User(userId)로 보낸다.
/// 알림함(놓친 알림 보기) = 그룹웨어 변경 알림 sync.change_notice 최근 30일 중 내가 쓰는 앱 것(AppAudience).
/// 읽음 표시는 sync.change_notice_read(한 건) · change_notice_seen(모두 읽음)에 ConnectionStrings:DyOneWrite로 쓴다.
/// 쓰기 연결이 없으면 읽음은 이 화면에서만 남는다. platform.notification은 "폐기 예정"이라 쓰지 않는다.
/// </summary>
[Authorize]
public class NotificationHub : Hub;

public record NotificationDto(long Id, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public class NotificationService(IHubContext<NotificationHub> hub)
{
    public Task PushAsync(string userId, string title, string body, string? link, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("notification",
            new NotificationDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), title, body, link, DateTimeOffset.UtcNow, null), ct);
}

public static class NotificationsModule
{
    private const int Limit = 100;

    private static string? WriteConn(IConfiguration config) => config.GetConnectionString("DyOneWrite") is { Length: > 0 } c ? c : null;

    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").WithTags("Notifications");

        // 알림함: 최근 30일 그룹웨어 변경 알림 중 내가 쓰는 앱 것, 최신 100건
        g.MapGet("/", async (ClaimsPrincipal p, AppAudience audience, IConfiguration config, ILogger<NotificationHub> logger, CancellationToken ct) =>
        {
            if (!long.TryParse(p.Current().Id, out var me)) return Results.Ok(Array.Empty<NotificationDto>());
            if (config.GetConnectionString("DyOne") is not { Length: > 0 })
            {
                logger.LogWarning("ConnectionStrings:DyOne이 없어 알림함을 비워 보낸다(개발: user-secrets)");
                return Results.Ok(Array.Empty<NotificationDto>());
            }
            var rows = new List<(string Entity, NotificationDto Dto)>();
            try
            {
                await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    select n.id, n.title, n.body, n.link, n.created_at, n.entity,
                           case when n.id <= coalesce(s.read_upto, 0) then s.read_at else r.read_at end
                    from sync.change_notice n
                    left join sync.change_notice_seen s on s.employee_id = @me
                    left join sync.change_notice_read r on r.employee_id = @me and r.notice_id = n.id
                    where n.created_at > now() - interval '30 days'
                    order by n.id desc limit 500
                    """, conn);
                cmd.Parameters.AddWithValue("me", me);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    rows.Add((r.GetString(5), new NotificationDto(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6))));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                logger.LogDebug("알림함 테이블 없음(server/db/sync_change_notice.sql 적용 전)");
                return Results.Ok(Array.Empty<NotificationDto>());
            }
            var list = new List<NotificationDto>();
            foreach (var (entity, dto) in rows)
            {
                if (!await audience.CanSeeAsync(me, entity, ct)) continue;
                list.Add(dto);
                if (list.Count == Limit) break;
            }
            return Results.Ok(list);
        });

        // 한 건 읽음
        g.MapPost("/{id:long}/read", async (long id, ClaimsPrincipal p, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConn(config) is not { } cs || !long.TryParse(p.Current().Id, out var me)) return Results.NoContent();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                insert into sync.change_notice_read (employee_id, notice_id)
                select @me, id from sync.change_notice where id = @id
                on conflict do nothing
                """, conn);
            cmd.Parameters.AddWithValue("me", me);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        // 모두 읽음: 지금까지 온 알림 번호까지
        g.MapPost("/read-all", async (ClaimsPrincipal p, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConn(config) is not { } cs || !long.TryParse(p.Current().Id, out var me)) return Results.NoContent();
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                insert into sync.change_notice_seen (employee_id, read_upto)
                select @me, coalesce(max(id), 0) from sync.change_notice
                on conflict (employee_id) do update set read_upto = excluded.read_upto, read_at = now();
                delete from sync.change_notice_read where employee_id = @me;
                """, conn);
            cmd.Parameters.AddWithValue("me", me);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        // 개발용: 내게 테스트 알림을 보낸다(웹 화면 안 알림 · Windows 토스트 확인용).
        g.MapPost("/test", async (ClaimsPrincipal p, NotificationService notifications, CancellationToken ct) =>
        {
            await notifications.PushAsync(p.Current().Id, "테스트 알림", "실시간 알림(SignalR)이 정상 동작합니다. 누르면 모듈라 화면으로 갑니다.", "/modular", ct);
            return Results.NoContent();
        });
    }
}
