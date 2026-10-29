using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어 알림 그대로 받기: 그룹웨어가 WORKS 앱에서 보낸 알림(go_noti_messages)을 DY ONE 알림(sync.change_notice)으로 옮긴다.
/// - Applets(화면 없이 알림만 받을 앱): 문서 값은 옮기지 않는다(근무관리·상조회 같은 개인 정보가 DY ONE에 쌓이지 않게).
///   알림 문구는 그룹웨어 문구 그대로, 누르면 그룹웨어 문서가 열린다. 앱 키 works:{앱번호}.
/// - SyncedApplets(데이터를 옮긴 앱 376 · 281): 등록·칸 변경·상태 변경은 동기화 작업이 직접 알리므로 빼고,
///   활동기록·댓글 같은 나머지만 받는다. 누르면 DY ONE 문서가 열린다. 앱 키 modular · works:{앱번호}.
/// 받는 사람은 API(AppAudience)가 앱 키로 정한다.
/// 감지: go_noti_messages.id(증가 번호) 색인으로 마지막으로 본 번호 뒤만 읽는다(sync.state 'gw-noti'). 처음 켤 때는 지금 번호부터 시작한다.
/// 그룹웨어에서 받을 사람이 아무도 없던 변경은 알림 자체가 만들어지지 않으므로 여기서도 오지 않는다.
/// </summary>
public partial class GwNotiMirrorJob(IOptions<GwNotiMirrorOptions> options, IConfiguration config, ILogger<GwNotiMirrorJob> logger) : IWorkerJob
{
    private const string Entity = "gw-noti";
    private readonly GwNotiMirrorOptions _opt = options.Value;

    public string Name => "gw-noti-mirror";
    public bool RunOnStartup => _opt.RunOnStartup;
    public TimeSpan UntilNext(DateTimeOffset nowUtc) => TimeSpan.FromMinutes(Math.Max(1, _opt.IntervalMinutes));

    private sealed record GwNoti(long Id, long AppletId, long DocId, string Event, string? Sender, string Message, string LinkUrl);

    // 데이터를 옮긴 앱에서 동기화 작업이 이미 알리는 그룹웨어 알림 종류
    private static readonly HashSet<string> CoveredBySync = ["APPLET_DOC_ADDED", "APPLET_DOC_EDITIED", "APPLET_DOC_STATUS_CHANGED", "APPLET_DOC_DELETED"];

    public async Task RunAsync(CancellationToken ct)
    {
        var applets = _opt.Applets.Distinct().ToArray();
        var synced = _opt.SyncedApplets.Distinct().ToArray();
        if (applets.Length == 0 && synced.Length == 0) return;
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");
        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");

        foreach (var (target, conn) in targets)
        {
            var last = await ReadWatermarkAsync(conn, ct);
            if (last is null)
            {
                // 처음: 지난 알림을 한꺼번에 보내지 않도록 지금 번호부터 본다
                var max = await MaxIdAsync(gwConn, ct);
                await WriteWatermarkAsync(conn, max, ct);
                logger.LogInformation("[{Target}] 그룹웨어 알림 받기 시작: 알림 번호 {Max} 뒤부터", target, max);
                continue;
            }

            var rows = await ReadNotisAsync(gwConn, last.Value, ct);
            if (rows.Count == 0) continue;
            var mine = rows.Where(r => applets.Contains(r.AppletId) || (synced.Contains(r.AppletId) && !CoveredBySync.Contains(r.Event))).ToList();
            var names = mine.Count == 0 ? [] : await AppletNamesAsync(gwConn, mine.Select(r => r.AppletId).Distinct().ToArray(), ct);

            if (mine.Count > 0 && !_opt.DryRun)
            {
                await using var c = new NpgsqlConnection(conn);
                await c.OpenAsync(ct);
                await using var tx = await c.BeginTransactionAsync(ct);
                var gw = _opt.GwBaseUrl.TrimEnd('/');
                foreach (var g in mine.GroupBy(r => r.AppletId))
                {
                    var app = ShortName(names.GetValueOrDefault(g.Key) ?? $"앱 {g.Key}");
                    var isSynced = synced.Contains(g.Key);
                    var entity = g.Key == 376 ? "modular" : $"works:{g.Key}";
                    var listLink = !isSynced ? $"{gw}/app/works/applet/{g.Key}/home" : g.Key == 376 ? "/modular" : $"/works/{g.Key}";
                    var notices = new List<GwNotices.Notice>();
                    foreach (var r in g)
                    {
                        // 데이터를 옮긴 앱은 DY ONE 문서로(아직 옮겨지지 않은 새 문서면 그룹웨어 문서로)
                        long? dyId = isSynced ? await DyOneDocIdAsync(c, tx, r.AppletId, r.DocId, ct) : null;
                        var link = dyId is { } id ? (g.Key == 376 ? $"/modular?doc={id}" : $"/works/{g.Key}?doc={id}") : gw + r.LinkUrl;
                        notices.Add(new GwNotices.Notice(dyId ?? 0, Kind(r.Event), $"그룹웨어 · {app}",
                            string.IsNullOrWhiteSpace(r.Sender) ? r.Message : $"{r.Message} — {r.Sender}", link));
                    }
                    await GwNotices.WriteAsync(c, tx, entity, app, listLink, notices, ct);
                }
                await WriteWatermarkAsync(c, tx, rows.Max(r => r.Id), ct);
                await tx.CommitAsync(ct);
            }
            else if (!_opt.DryRun)
            {
                await WriteWatermarkAsync(conn, rows.Max(r => r.Id), ct);
            }
            logger.LogInformation("[{Target}] 그룹웨어 알림 {All}건 중 받을 앱 {Mine}건 {Mode}", target, rows.Count, mine.Count, _opt.DryRun ? "(DryRun)" : "반영");
        }
    }

    private static string Kind(string ev) => ev switch
    {
        "APPLET_DOC_ADDED" => "created",
        "APPLET_DOC_DELETED" => "deleted",
        _ => "changed",
    };

    /// <summary>그룹웨어 앱 이름에서 앞 번호를 뗀다 (예: '04. ES_ 이슈사항 보고' → 'ES_ 이슈사항 보고')</summary>
    private static string ShortName(string name) => LeadingNo().Replace(name, "").Trim();

    [GeneratedRegex(@"^\s*\d+\.\s*")] private static partial Regex LeadingNo();
    [GeneratedRegex(@"^/app/works/applet/(\d+)/doc/(\d+)")] private static partial Regex DocLink();

    // ── 그룹웨어(읽기 전용) ─────────────────────────────────────────────────
    private static async Task<long> MaxIdAsync(string gwConn, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(gwConn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select coalesce(max(id), 0) from go_noti_messages", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<List<GwNoti>> ReadNotisAsync(string gwConn, long after, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(gwConn);
        await c.OpenAsync(ct);
        // (id, noti_category) 색인 범위 조회 — 마지막 번호 뒤 WORKS 알림만. 한 번에 최대 500건.
        await using var cmd = new NpgsqlCommand(@"select id, noti_event, sender_name, message, link_url from go_noti_messages
            where id > @after and noti_category = 'WORKS' order by id limit 500", c);
        cmd.Parameters.AddWithValue("after", after);
        var list = new List<GwNoti>();
        var maxSeen = after;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt64(0);
            maxSeen = id;
            var link = r.IsDBNull(4) ? "" : r.GetString(4);
            var m = DocLink().Match(link);
            if (!m.Success) continue;
            list.Add(new GwNoti(id, long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value),
                r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3).Trim(), link));
        }
        // 링크 형식이 다른 WORKS 알림만 있던 경우에도 번호는 앞으로 옮긴다
        if (list.Count == 0 && maxSeen > after) list.Add(new GwNoti(maxSeen, 0, 0, "", null, "", ""));
        return list;
    }

    private static async Task<Dictionary<long, string>> AppletNamesAsync(string gwConn, long[] ids, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(gwConn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select id, name from go_applets where id = any(@ids)", c);
        cmd.Parameters.AddWithValue("ids", ids);
        var map = new Dictionary<long, string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetString(1);
        return map;
    }

    // ── DY ONE ─────────────────────────────────────────────────────────────
    private static async Task<long?> DyOneDocIdAsync(NpgsqlConnection c, NpgsqlTransaction tx, long applet, long gwDocId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(applet == 376
            ? "select id from modular.build where gw_doc_id = @d"
            : "select id from works.doc where applet_id = @a and gw_doc_id = @d", c, tx);
        cmd.Parameters.AddWithValue("a", applet);
        cmd.Parameters.AddWithValue("d", gwDocId);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    private static async Task<long?> ReadWatermarkAsync(string conn, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select watermark from sync.state where source = 'groupware' and entity = @e", c);
        cmd.Parameters.AddWithValue("e", Entity);
        return await cmd.ExecuteScalarAsync(ct) is string s && s.StartsWith("noti:") && long.TryParse(s[5..], out var n) ? n : null;
    }

    private static async Task WriteWatermarkAsync(string conn, long id, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await WriteWatermarkAsync(c, null, id, ct);
    }

    private static async Task WriteWatermarkAsync(NpgsqlConnection c, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', @e, @w, now())
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", c, tx);
        cmd.Parameters.AddWithValue("e", Entity);
        cmd.Parameters.AddWithValue("w", $"noti:{id}");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
