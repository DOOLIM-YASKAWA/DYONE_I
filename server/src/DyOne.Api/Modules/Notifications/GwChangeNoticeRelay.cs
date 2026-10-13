using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace DyOne.Api.Modules.Notifications;

/// <summary>
/// 그룹웨어 변경 알림 전달: Worker가 sync.change_notice에 남긴 새 줄을 몇 초마다 읽어 실시간 알림으로 보낸다
/// (화면 토스트 · 알림함, Windows 앱은 Windows 토스트). 받는 사람은 그 앱을 쓰는 부서·사람 중 지금 접속한 사람이다(AppAudience).
/// 접속하지 않았던 사람은 다음에 들어와 알림함(GET /api/notifications)에서 본다.
/// API는 dyone DB를 읽기만 한다(읽기 전용 연결). 켤 때 이미 있던 알림은 다시 보내지 않는다.
/// 설정: Notifications:GwChangeRelaySeconds (기본 5초, 0이면 끔)
/// </summary>
public class GwChangeNoticeRelay(IHubContext<NotificationHub> hub, AppAudience audience, IConfiguration config, ILogger<GwChangeNoticeRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var seconds = config.GetValue("Notifications:GwChangeRelaySeconds", 5);
        var connString = config.GetConnectionString("DyOne");
        if (seconds <= 0 || string.IsNullOrWhiteSpace(connString)) return;

        long? last = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connString);
                await conn.OpenAsync(ct);
                if (last is null)
                {
                    await using var start = new NpgsqlCommand("select coalesce(max(id), 0) from sync.change_notice", conn);
                    last = (long)(await start.ExecuteScalarAsync(ct))!;
                }
                await using var cmd = new NpgsqlCommand(
                    "select id, title, body, link, created_at, entity from sync.change_notice where id > @last order by id limit 100", conn);
                cmd.Parameters.AddWithValue("last", last.Value);
                var rows = new List<(string Entity, NotificationDto Dto)>();
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct))
                        rows.Add((r.GetString(5), new NotificationDto(r.GetInt64(0), r.GetString(1), r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), null)));
                foreach (var (entity, n) in rows)
                {
                    last = n.Id;
                    // 허브의 사용자 식별자 = JWT NameIdentifier = org.employee.id
                    var to = await audience.RecipientsAsync(entity, ct);
                    if (to is null) await hub.Clients.All.SendAsync("notification", n, ct);
                    else if (to.Count > 0) await hub.Clients.Users(to.Select(x => x.ToString()).ToList()).SendAsync("notification", n, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // 아직 sync.change_notice가 없는 DB(server/db/sync_change_notice.sql 적용 전): 1분 뒤 다시 본다
                logger.LogDebug("sync.change_notice 없음 — 그룹웨어 변경 알림 꺼짐");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                continue;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "그룹웨어 변경 알림 읽기 실패");
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
    }
}
