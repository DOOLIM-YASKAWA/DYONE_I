using System.Text.Json;
using DyOne.Data.Works;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어(tims) WORKS 앱을 앱 번호만으로 DY ONE works.* 에 옮기고 동기화한다(첫 대상: 281 ES 이슈사항 보고).
///
/// 원천(GW): 조회 전용. 앱 정의(필드·선택지·상태·전환·폼 배치·목록 열)와 문서 값(EAV), 활동 글·댓글·변경 이력·첨부 정보를 읽는다.
/// 대상(dyone): 두 벌 관리 — works.doc(DY ONE 값)과 works.doc_gw(마지막 그룹웨어 값).
///   그룹웨어 값이 지난번과 달라진 칸만 DY ONE 값에 덮어쓴다(덮인 DY ONE 값은 works.field_change.overwritten).
///   그룹웨어에서 안 바뀐 칸은 DY ONE에서 고친 값을 그대로 둔다. DY ONE 변경은 그룹웨어로 보내지 않는다.
///   managed_in_dyone 문서는 사본만 갱신한다. 그룹웨어에서 사라진 문서는 gw_deleted_at으로 표시만 한다.
/// 변경 감지: GwChangeFeed로 마지막으로 본 변경 이력 번호 뒤에 이력이 생긴 문서만 다시 읽는다(앱별 sync.state 'works:{앱}').
/// 첨부 파일 본체는 그룹웨어 서버 디스크에 있어 정보(이름·크기·경로)만 옮긴다.
/// </summary>
public class GwWorksSyncJob(IOptions<GwWorksSyncOptions> options, IConfiguration config, ILogger<GwWorksSyncJob> logger) : IWorkerJob
{
    private readonly GwWorksSyncOptions _opt = options.Value;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;

    public string Name => "gw-works-sync";
    public bool RunOnStartup => _opt.RunOnStartup;

    public TimeSpan UntilNext(DateTimeOffset nowUtc)
    {
        if (_opt.IntervalMinutes > 0) return TimeSpan.FromMinutes(_opt.IntervalMinutes);
        var at = TimeOnly.Parse(_opt.DailyAt);
        var local = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var today = new DateTimeOffset(local.Year, local.Month, local.Day, at.Hour, at.Minute, 0, local.Offset);
        return (local < today ? today : today.AddDays(1)) - local;
    }

    // ── 그룹웨어에서 읽은 것 ────────────────────────────────────────────────
    private sealed record FieldDef(string Cid, string Label, string FieldType, string? ValueType, bool Required, int Seq,
        List<(long Code, string Label, bool IsDefault)> Options, Dictionary<string, object?> Props);
    private sealed record StatusRow(long Id, string Name, int Sort, string? Color, bool IsEnd, bool IsBegin);
    private sealed record TransitionRow(long Id, long From, long To, string Action, int Sort);
    private sealed record Elem(long? Long, string? Str, long? RefId, string? RefText);
    private sealed record GwValue(string? Str, long? Long, double? Dbl, DateTime? Date, string? Text, List<Elem>? Multi);
    private sealed record ActivityRow(long Id, long DocId, string? Html, long? ById, DateTime? At);
    private sealed record CommentRow(long Id, long ActivityId, string? Body, long? UserId, DateTime? At);
    private sealed record LogRow(long Id, long DocId, string Message, long? ActorId, DateTime? At);
    private sealed record FileRow(string Source, long DocId, long? ActivityId, long AttachId, string? Name, string? Ext, long? Size, string? Path, string? Host, DateTime? At);

    private sealed class Doc
    {
        public long Id;
        public long? StatusId;
        public DateTime? Created, Updated;
        public readonly Dictionary<string, GwValue> Values = [];
    }

    private sealed class Snapshot
    {
        public long AppletId;
        public string Name = "";
        public string? Layout;
        public string? ViewColumns;
        public List<FieldDef> Fields = [];
        public List<StatusRow> Statuses = [];
        public List<TransitionRow> Transitions = [];
        public List<Doc> Docs = [];
        public List<ActivityRow> Activities = [];
        public List<CommentRow> Comments = [];
        public List<LogRow> Logs = [];
        public List<FileRow> Files = [];
        public Dictionary<long, string> Users = [];
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var applets = _opt.Applets.Distinct().ToArray();
        if (applets.Length == 0) return;
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");
        // 번호는 조회 전에 먼저 읽어 둔다 — 조회 중에 생긴 변경은 다음 확인에서 잡힌다.
        var maxLog = await GwChangeFeed.MaxLogAsync(gwConn, ct);
        var fullDue = DateTimeOffset.UtcNow - _lastFull >= TimeSpan.FromHours(Math.Max(1, _opt.FullSyncHours));

        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");
        Exception? firstError = null;
        var allFull = true;
        foreach (var (target, conn) in targets)
        {
            if (!await HasSchemaAsync(conn, ct))
            {
                logger.LogWarning("[{Target}] works 스키마가 없어 건너뜀 (server/db/works_schema.sql 적용 필요)", target);
                continue;
            }
            foreach (var applet in applets)
            {
                try
                {
                    var entity = $"works:{applet}";
                    var last = ParseWatermark(await ReadWatermarkAsync(conn, entity, ct));
                    if (!fullDue && last is { } seen && seen >= maxLog)
                    {
                        logger.LogDebug("[{Target}] GW applet{Applet} 변경 없음", target, applet);
                        continue;
                    }
                    var full = fullDue || last is null;
                    long[]? docs = null;
                    if (!full)
                    {
                        docs = (await GwChangeFeed.ChangedDocsAsync(gwConn, last!.Value, [applet], ct))[applet];
                        if (docs.Length == 0)
                        {
                            await WriteWatermarkAsync(conn, entity, maxLog, ct);
                            continue;
                        }
                    }
                    allFull &= full;

                    var snap = await ReadGroupwareAsync(gwConn, applet, docs, ct);
                    logger.LogInformation("[{Target}] GW applet{Applet} {Mode}: 문서 {Docs}건 · 활동 {Act}건 · 댓글 {Cm}건 · 이력 {Log}건 · 첨부 {File}개",
                        target, applet, full ? "전체 조회" : "바뀐 문서 조회", snap.Docs.Count, snap.Activities.Count, snap.Comments.Count, snap.Logs.Count, snap.Files.Count);
                    if (snap.Fields.Count == 0 || (full && snap.Docs.Count == 0))
                    {
                        logger.LogWarning("[{Target}] GW applet{Applet} 조회 결과가 비어 있어 적재를 건너뛴다(삭제 표시 방지).", target, applet);
                        continue;
                    }
                    if (_opt.DryRun) continue;

                    var r = await WriteAsync(conn, snap, full, notify: last is not null, ct);
                    await WriteWatermarkAsync(conn, entity, maxLog, ct);
                    logger.LogInformation("[{Target}] GW applet{Applet} 반영: 새 문서 {New}건 · 그룹웨어에서 바뀐 칸 {Applied}개(DY ONE 값 덮음 {Over}개) · DY ONE 관리 건 건너뜀 {Skip}건 · 그룹웨어 삭제 표시 {Del}건",
                        target, applet, r.Created, r.Applied, r.Overwritten, r.Managed, r.Deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    firstError ??= ex;
                    logger.LogError(ex, "[{Target}] GW applet{Applet} 적재 실패", target, applet);
                    await TryRecordErrorAsync(conn, $"works:{applet}", ex.Message, ct);
                }
            }
        }
        if (firstError is not null) throw firstError;
        if (fullDue && allFull) _lastFull = DateTimeOffset.UtcNow;
    }

    // ── 그룹웨어 읽기 ───────────────────────────────────────────────────────
    /// <param name="docs">null이면 앱 전체, 아니면 이 문서들만(정의는 항상 전체)</param>
    private static async Task<Snapshot> ReadGroupwareAsync(string gwConn, long applet, long[]? docs, CancellationToken ct)
    {
        var snap = new Snapshot { AppletId = applet };
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("set transaction read only", conn, tx)) await ro.ExecuteNonQueryAsync(ct);
        string Scope(string column) => docs is null ? "" : $" and {column} = any(@docs)";
        NpgsqlCommand Cmd(string sql)
        {
            var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("a", applet);
            if (docs is not null) cmd.Parameters.AddWithValue("docs", docs);
            return cmd;
        }

        // 앱 이름 · 폼 배치 · 목록 열
        await using (var cmd = Cmd(@"select a.name, f.data,
              (select json_agg(c.field_cid order by c.seq) from go_applet_view_columns c where c.applet_view_id = a.view_id)::text
              from go_applets a left join go_applet_forms f on f.id = a.form_id where a.id = @a"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
            {
                snap.Name = r.GetString(0).Trim();
                snap.Layout = r.IsDBNull(1) ? null : SimplifyLayout(r.GetString(1));
                snap.ViewColumns = r.IsDBNull(2) ? null : r.GetString(2);
            }

        // 필드 정의
        await using (var cmd = Cmd(@"select f.cid, f.label, f.field_type, f.value_type, f.seq, f.properties_as_json
              from go_applet_form_fields ff join go_applet_fields f on f.id = ff.field_id
              where ff.applet_form_id = (select form_id from go_applets where id = @a) order by f.seq"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Fields.Add(ParseField(r.GetString(0), r.IsDBNull(1) ? r.GetString(0) : r.GetString(1).Trim(), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)));

        // 상태 · 전환
        await using (var cmd = Cmd(@"select s.id, s.name, s.seq, s.color, s.is_end, s.id = p.begin_status_id
              from go_applets a join go_applet_processes p on p.id = a.process_id
              join go_applet_process_statuses ps on ps.applet_process_id = p.id join go_applet_statuses s on s.id = ps.status_id
              where a.id = @a"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Statuses.Add(new StatusRow(r.GetInt64(0), r.GetString(1).Trim(), r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3), !r.IsDBNull(4) && r.GetBoolean(4), !r.IsDBNull(5) && r.GetBoolean(5)));
        await using (var cmd = Cmd(@"select t.id, t.before_status_id, t.next_status_id, t.action_name, t.seq from go_applet_status_transitions t
              where t.before_status_id = any(@s) and t.next_status_id = any(@s)"))
        {
            cmd.Parameters.AddWithValue("s", snap.Statuses.Select(s => s.Id).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Transitions.Add(new TransitionRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3).Trim(), r.IsDBNull(4) ? 0 : r.GetInt32(4)));
        }

        // 문서 값 (EAV). 복수 값은 원소마다 숫자·글자, 참조 문서는 원소 안의 id·text(go_applet_val_map_values)
        var docMap = new Dictionary<long, Doc>();
        await using (var cmd = Cmd(@"select d.id, d.status_id, d.created_at, d.updated_at, dv.values_key,
                 v.string_value, v.long_value, v.double_value, v.date_value, v.text_value,
                 case when v.category = 'MULTIVALUES' then (
                   select json_agg(json_build_object('l', m.long_value, 's', m.string_value,
                       'id', (select x.long_value from go_applet_val_map_values mp join go_applet_vals x on x.id = mp.val_id where mp.parent_id = m.id and mp.key = 'id'),
                       't', (select x.string_value from go_applet_val_map_values mp join go_applet_vals x on x.id = mp.val_id where mp.parent_id = m.id and mp.key = 'text'))
                     order by mv.seq)
                   from go_applet_val_multi_values mv join go_applet_vals m on m.id = mv.val_id where mv.parent_id = v.id)::text end
              from go_applet_docs d
              left join go_applet_doc_values dv on dv.applet_doc_id = d.id
              left join go_applet_vals v on v.id = dv.value_id
              where d.applet_id = @a" + Scope("d.id")))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                if (!docMap.TryGetValue(id, out var doc))
                    docMap[id] = doc = new Doc
                    {
                        Id = id,
                        StatusId = r.IsDBNull(1) ? null : r.GetInt64(1),
                        Created = r.IsDBNull(2) ? null : r.GetDateTime(2),
                        Updated = r.IsDBNull(3) ? null : r.GetDateTime(3),
                    };
                if (r.IsDBNull(4)) continue;
                List<Elem>? multi = null;
                if (!r.IsDBNull(10))
                {
                    using var j = JsonDocument.Parse(r.GetString(10));
                    multi = [.. j.RootElement.EnumerateArray().Select(e => new Elem(
                        e.GetProperty("l").ValueKind == JsonValueKind.Number ? e.GetProperty("l").GetInt64() : null,
                        e.GetProperty("s").ValueKind == JsonValueKind.String ? e.GetProperty("s").GetString() : null,
                        e.GetProperty("id").ValueKind == JsonValueKind.Number ? e.GetProperty("id").GetInt64() : null,
                        e.GetProperty("t").ValueKind == JsonValueKind.String ? e.GetProperty("t").GetString() : null))];
                }
                doc.Values[r.GetString(4)] = new GwValue(
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetDouble(7),
                    r.IsDBNull(8) ? null : r.GetDateTime(8), r.IsDBNull(9) ? null : r.GetString(9), multi);
            }
        snap.Docs = [.. docMap.Values];

        // 활동 글 · 댓글 · 변경 이력 · 첨부 정보
        await using (var cmd = Cmd(@"select a.id, a.applet_doc_id, c.contents, a.created_by_id, a.created_at from go_applet_activities a
              left join go_applet_act_contents c on c.activity_id = a.id
              where a.applet_doc_id in (select id from go_applet_docs where applet_id = @a)" + Scope("a.applet_doc_id")))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Activities.Add(new ActivityRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        await using (var cmd = Cmd(@"select c.id, x.activity_id, c.message, c.user_id, c.created_at
              from go_applet_activity_comments x join go_comments c on c.id = x.id
              where x.activity_id in (select a.id from go_applet_activities a join go_applet_docs d on d.id = a.applet_doc_id where d.applet_id = @a" + Scope("d.id") + ")"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Comments.Add(new CommentRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        await using (var cmd = Cmd(@"select l.id, dl.applet_doc_id, l.message, l.actor_id, l.created_at
              from go_applet_doc_logs dl join go_action_logs l on l.id = dl.log_id
              where dl.applet_doc_id in (select id from go_applet_docs where applet_id = @a)" + Scope("dl.applet_doc_id")))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Logs.Add(new LogRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));

        const string fileCols = @"distinct on (r.attach_id) r.attach_id, f.name, f.extension, f.length, f.path, f.host_id, f.created_at
              from go_attach_revisions r join go_attach_files f on f.id = r.attach_file_id";
        await using (var cmd = Cmd($@"select x.applet_activity_id, a.applet_doc_id, q.* from go_applet_activity_attaches x
              join go_applet_activities a on a.id = x.applet_activity_id
              join go_applet_docs d on d.id = a.applet_doc_id and d.applet_id = @a
              join lateral (select {fileCols} where r.attach_id = x.attach_id order by r.attach_id, r.revision desc) q on true
              where true" + Scope("d.id")))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                snap.Files.Add(ReadFile(r, "activity", r.GetInt64(1), r.GetInt64(0), 2));
        // 파일 필드(값 = 첨부 id 목록)
        var fileCids = snap.Fields.Where(f => f.ValueType == "FILES").Select(f => f.Cid).ToHashSet();
        var owner = snap.Docs.SelectMany(d => d.Values.Where(kv => fileCids.Contains(kv.Key) && kv.Value.Multi is not null)
            .SelectMany(kv => kv.Value.Multi!.Select(e => e.Long).OfType<long>().Select(a => (doc: d.Id, attach: a))))
            .GroupBy(x => x.attach).ToDictionary(g => g.Key, g => g.First().doc);
        if (owner.Count > 0)
        {
            // 파일 필드 값은 앱에 따라 첨부 id(판 이력을 거침)이거나 파일 id 자체다 → 첨부 id로 먼저 찾고, 없으면 파일 id로 찾는다.
            var found = new HashSet<long>();
            await using (var cmd = Cmd($"select {fileCols} where r.attach_id = any(@ids) order by r.attach_id, r.revision desc"))
            {
                cmd.Parameters.AddWithValue("ids", owner.Keys.ToArray());
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    snap.Files.Add(ReadFile(r, "doc", owner[r.GetInt64(0)], null, 0));
                    found.Add(r.GetInt64(0));
                }
            }
            var rest = owner.Keys.Where(k => !found.Contains(k)).ToArray();
            if (rest.Length > 0)
            {
                await using var cmd = Cmd("select f.id, f.name, f.extension, f.length, f.path, f.host_id, f.created_at from go_attach_files f where f.id = any(@ids)");
                cmd.Parameters.AddWithValue("ids", rest);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    snap.Files.Add(ReadFile(r, "doc", owner[r.GetInt64(0)], null, 0));
            }
        }

        // 사용자 이름: 사용자 필드 값 + 등록자·수정자 + 글쓴이·이력 작성자 (퇴사자도 이름은 남긴다)
        var userCids = snap.Fields.Where(f => f.ValueType is "USER" or "USERS").Select(f => f.Cid).Append("creator").Append("updater").ToHashSet();
        var userIds = snap.Docs.SelectMany(d => d.Values.Where(kv => userCids.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Multi?.Select(e => e.Long) ?? [kv.Value.Long]))
            .Concat(snap.Activities.Select(a => a.ById)).Concat(snap.Comments.Select(c => c.UserId)).Concat(snap.Logs.Select(l => l.ActorId))
            .OfType<long>().Distinct().ToArray();
        await using (var cmd = Cmd("select id, name from go_users where id = any(@ids)"))
        {
            cmd.Parameters.AddWithValue("ids", userIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(1)) snap.Users[r.GetInt64(0)] = r.GetString(1);
        }

        await tx.RollbackAsync(ct);
        return snap;
    }

    private static FileRow ReadFile(NpgsqlDataReader r, string source, long docId, long? activityId, int o) => new(
        source, docId, activityId, r.GetInt64(o),
        r.IsDBNull(o + 1) ? null : r.GetString(o + 1), r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
        r.IsDBNull(o + 3) ? null : r.GetInt64(o + 3), r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
        r.IsDBNull(o + 5) ? null : r.GetString(o + 5), r.IsDBNull(o + 6) ? null : r.GetDateTime(o + 6));

    private static FieldDef ParseField(string cid, string label, string fieldType, string? valueType, int seq, string? json)
    {
        var options = new List<(long, string, bool)>();
        var props = new Dictionary<string, object?>();
        var required = false;
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            required = root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;
            var defaults = new HashSet<long>();
            if (root.TryGetProperty("defaultValue", out var dv))
                foreach (var x in dv.ValueKind == JsonValueKind.Array ? dv.EnumerateArray().ToArray() : [dv])
                    if (AsLong(x) is { } v) defaults.Add(v);
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("value", out var value) && AsLong(value) is { } code)
                        options.Add((code, System.Text.RegularExpressions.Regex.Replace((item.TryGetProperty("displayText", out var t) ? t.GetString() ?? "" : "").Trim(), @"\s+", " "),
                            defaults.Contains(code)));
            // 참조 앱 정보만 남긴다(화면 연결용)
            foreach (var key in new[] { "integrationAppletId", "appletName", "targetAppletId", "targetComponentCid", "targetFieldLabel", "guide" })
                if (root.TryGetProperty(key, out var p) && p.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    props[key] = p.ValueKind == JsonValueKind.Number ? p.GetInt64() : p.GetString();
        }
        return new FieldDef(cid, label, fieldType, valueType, required, seq, options, props);
    }

    private static long? AsLong(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(e.GetString(), out var n) => n,
        _ => null,
    };

    /// <summary>그룹웨어 폼 JSON → 화면 배치용 { type, cid, label, count, children } (선택지 등은 빼고)</summary>
    private static string SimplifyLayout(string json)
    {
        using var doc = JsonDocument.Parse(json);
        static Dictionary<string, object?> Walk(JsonElement n)
        {
            var node = new Dictionary<string, object?>
            {
                ["type"] = n.TryGetProperty("type", out var t) ? t.GetString() : null,
                ["cid"] = n.TryGetProperty("cid", out var c) ? c.GetString() : null,
            };
            if (n.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String) node["label"] = l.GetString();
                if (p.TryGetProperty("count", out var cnt)) node["count"] = cnt.ToString();
            }
            if (n.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array && comps.GetArrayLength() > 0)
                node["children"] = comps.EnumerateArray().Select(Walk).ToList();
            return node;
        }
        return JsonSerializer.Serialize(Walk(doc.RootElement));
    }

    // ── 그룹웨어 값 → 비교용 값 · 표시 ───────────────────────────────────────
    private static (Dictionary<string, string?> Values, Dictionary<string, string?> Display) Normalize(Doc d, Snapshot snap)
    {
        var values = new Dictionary<string, string?>();
        var display = new Dictionary<string, string?>();
        foreach (var f in snap.Fields)
        {
            if (WorksValues.MetaCids.Contains(f.Cid) || WorksValues.LayoutTypes.Contains(f.FieldType)) continue;
            d.Values.TryGetValue(f.Cid, out var v);
            string? value = null, shown = null;
            if (v is not null)
                switch (f.ValueType)
                {
                    case "DATE":
                        value = WorksValues.Date(v.Date is { } dt ? DateOnly.FromDateTime(dt) : ParseYmd(v.Str));
                        value ??= WorksValues.Text(v.Str);
                        break;
                    case "DATETIME":
                        value = WorksValues.DateTimeText(v.Date);
                        break;
                    case "NUMBER":
                        value = WorksValues.Number(v.Dbl ?? v.Long);
                        break;
                    case "SELECT":
                        value = v.Long?.ToString();
                        shown = WorksValues.OptionDisplay("SELECT", value, f.Options.ToDictionary(o => o.Code, o => o.Label));
                        break;
                    case "SELECTS":
                        value = WorksValues.Codes(v.Multi?.Select(e => e.Long).OfType<long>() ?? (v.Long is { } one ? [one] : []));
                        shown = WorksValues.OptionDisplay("SELECTS", value, f.Options.ToDictionary(o => o.Code, o => o.Label));
                        break;
                    case "USER" or "USERS":
                        var ids = v.Multi?.Select(e => e.Long).OfType<long>().ToList() ?? (v.Long is { } u ? [u] : []);
                        value = WorksValues.Codes(ids);
                        shown = ids.Count == 0 ? null : string.Join(", ", ids.Select(i => snap.Users.TryGetValue(i, out var n) ? n : $"#{i}"));
                        break;
                    case "APPLETDOCS":
                        var refs = v.Multi?.Where(e => e.RefId is not null).ToList() ?? [];
                        value = WorksValues.Codes(refs.Select(e => e.RefId!.Value));
                        shown = refs.Count == 0 ? null : string.Join(", ", refs.Select(e => e.RefText ?? $"#{e.RefId}"));
                        break;
                    case "FILES":
                        var files = v.Multi?.Select(e => e.Long).OfType<long>().ToList() ?? [];
                        value = WorksValues.Codes(files);
                        shown = files.Count == 0 ? null : $"파일 {files.Count}개";
                        break;
                    default:
                        value = WorksValues.Text(v.Str ?? v.Text);
                        break;
                }
            values[f.Cid] = value;
            display[f.Cid] = shown ?? value;
        }
        return (values, display);
    }

    private static DateOnly? ParseYmd(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        return digits.Length == 8 && DateOnly.TryParseExact(digits, "yyyyMMdd", out var d) ? d : null;
    }

    // ── dyone 쓰기 ─────────────────────────────────────────────────────────
    private sealed record WriteResult(int Created, int Applied, int Overwritten, int Managed, int Deleted);

    /// <param name="notify">false면 알림을 남기지 않는다(처음 전체 적재)</param>
    private static async Task<WriteResult> WriteAsync(string writeConn, Snapshot snap, bool full, bool notify, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var applet = snap.AppletId;

        // 1) 앱 정의 (그룹웨어 그대로)
        await ExecAsync(conn, tx, @"insert into works.app (applet_id, name, layout, view_columns, synced_at) values (@a, @n, @l, @v, now())
            on conflict (applet_id) do update set name = excluded.name, layout = excluded.layout, view_columns = excluded.view_columns, synced_at = now()", ct,
            ("a", applet), ("n", snap.Name), ("l", Jsonb(snap.Layout)), ("v", Jsonb(snap.ViewColumns)));
        await ExecAsync(conn, tx, "delete from works.field where applet_id = @a and cid <> all(@cids)", ct,
            ("a", applet), ("cids", snap.Fields.Select(f => f.Cid).ToArray()));
        foreach (var f in snap.Fields)
            await ExecAsync(conn, tx, @"insert into works.field (applet_id, cid, label, field_type, value_type, required, seq, options, props, editable)
                values (@a, @c, @l, @ft, @vt, @r, @s, @o, @p, @e)
                on conflict (applet_id, cid) do update set label = excluded.label, field_type = excluded.field_type, value_type = excluded.value_type,
                  required = excluded.required, seq = excluded.seq, options = excluded.options, props = excluded.props, editable = excluded.editable", ct,
                ("a", applet), ("c", f.Cid), ("l", f.Label), ("ft", f.FieldType), ("vt", (object?)f.ValueType ?? DBNull.Value), ("r", f.Required), ("s", f.Seq),
                ("o", Jsonb(f.Options.Count == 0 ? null : JsonSerializer.Serialize(f.Options.Select(o => new { code = o.Code, label = o.Label, isDefault = o.IsDefault })))),
                ("p", Jsonb(f.Props.Count == 0 ? null : JsonSerializer.Serialize(f.Props))),
                ("e", WorksValues.IsEditable(f.FieldType, f.ValueType) && !WorksValues.MetaCids.Contains(f.Cid)));
        foreach (var s in snap.Statuses)
            await ExecAsync(conn, tx, @"insert into works.status (id, applet_id, name, sort, color, is_end, is_begin) values (@i, @a, @n, @s, @c, @e, @b)
                on conflict (id) do update set applet_id = excluded.applet_id, name = excluded.name, sort = excluded.sort, color = excluded.color,
                  is_end = excluded.is_end, is_begin = excluded.is_begin", ct,
                ("i", s.Id), ("a", applet), ("n", s.Name), ("s", s.Sort), ("c", (object?)s.Color ?? DBNull.Value), ("e", s.IsEnd), ("b", s.IsBegin));
        await ExecAsync(conn, tx, "delete from works.transition where applet_id = @a and id <> all(@ids)", ct,
            ("a", applet), ("ids", snap.Transitions.Select(t => t.Id).ToArray()));
        foreach (var t in snap.Transitions)
            await ExecAsync(conn, tx, @"insert into works.transition (id, applet_id, from_status, to_status, action_name, sort) values (@i, @a, @f, @t, @n, @s)
                on conflict (id) do update set from_status = excluded.from_status, to_status = excluded.to_status, action_name = excluded.action_name, sort = excluded.sort", ct,
                ("i", t.Id), ("a", applet), ("f", t.From), ("t", t.To), ("n", t.Action), ("s", t.Sort));

        // 2) 문서 — 두 벌 관리(칸별 반영)
        var docIds = new Dictionary<long, long>();
        int created = 0, applied = 0, overwritten = 0, managed = 0;
        var notices = new List<GwNotices.Notice>();
        var labels = snap.Fields.ToDictionary(f => f.Cid, f => f.Label);
        var statusNames = snap.Statuses.ToDictionary(x => x.Id, x => x.Name);
        var appName = ShortName(snap.Name);
        // 알림 제목 칸: 목록 열 중 첫 글자 칸(화면에서 문서를 여는 칸과 같은 규칙)
        var viewCols = snap.ViewColumns is null ? [] : JsonSerializer.Deserialize<string[]>(snap.ViewColumns) ?? [];
        var titleCid = viewCols.FirstOrDefault(c => snap.Fields.Any(f => f.Cid == c && f.FieldType is "text" or "textarea"));
        foreach (var d in snap.Docs)
        {
            var (values, display) = Normalize(d, snap);
            var r = await MergeDocAsync(conn, tx, applet, d, values, display, snap.Users, ct);
            docIds[d.Id] = r.DocId;
            if (r.Created) created++;
            if (r.Managed) managed++;
            applied += r.Applied;
            overwritten += r.Overwritten;

            var title = (titleCid is not null && display.TryGetValue(titleCid, out var t) ? t : null) ?? $"문서 {d.Id}";
            var link = $"/works/{applet}?doc={r.DocId}";
            if (r.Created)
                notices.Add(new GwNotices.Notice(r.DocId, "created", $"그룹웨어 새 문서 · {appName}", title, link));
            else if (r.Applied > 0)
            {
                string? Name(long? id) => id is { } v && statusNames.TryGetValue(v, out var n) ? n : null;
                var changedLabels = r.Keys.Where(k => k != "status").Select(k => labels.TryGetValue(k, out var l) ? l : k).ToList();
                var summary = GwNotices.Summary(Name(r.StatusFrom), r.Keys.Contains("status") ? Name(r.StatusTo) ?? "없음" : null, changedLabels, r.Overwritten);
                notices.Add(new GwNotices.Notice(r.DocId, "changed", $"그룹웨어 변경 · {appName}", $"{title} — {summary}", link));
            }
        }
        var deleted = 0;
        if (full)
            deleted = await ExecAsync(conn, tx, "update works.doc set gw_deleted_at = now() where applet_id = @a and gw_doc_id is not null and gw_deleted_at is null and gw_doc_id <> all(@ids)", ct,
                ("a", applet), ("ids", snap.Docs.Select(d => d.Id).ToArray()));
        if (deleted > 0)
            notices.Add(new GwNotices.Notice(0, "deleted", $"그룹웨어 삭제 · {appName}", $"그룹웨어에서 {deleted}건이 지워졌습니다. DY ONE에는 '그룹웨어 삭제'로 표시해 남겨 두었습니다.", $"/works/{applet}"));
        if (notify) await GwNotices.WriteAsync(conn, tx, $"works:{applet}", appName, $"/works/{applet}", notices, ct);
        var scope = docIds.Values.ToArray();

        // 3) 활동 글 · 댓글 (그룹웨어에서 지운 것은 DY ONE에서도 지운다. DY ONE에서 쓴 글은 gw id가 없어 건드리지 않는다)
        var activityIds = new Dictionary<long, long>();
        foreach (var a in snap.Activities)
        {
            if (!docIds.TryGetValue(a.DocId, out var docId)) continue;
            activityIds[a.Id] = await ScalarAsync(conn, tx, @"insert into works.activity (doc_id, gw_activity_id, body_html, body_text, created_by_name, created_at)
                values (@doc, @gw, @html, @text, @by, @at::timestamptz)
                on conflict (gw_activity_id) do update set doc_id = excluded.doc_id, body_html = excluded.body_html, body_text = excluded.body_text,
                  created_by_name = excluded.created_by_name, created_at = excluded.created_at returning id", ct,
                ("doc", docId), ("gw", a.Id), ("html", (object?)a.Html ?? DBNull.Value), ("text", (object?)GwText.HtmlToText(a.Html) ?? DBNull.Value),
                ("by", (object?)UserName(snap.Users, a.ById) ?? DBNull.Value), ("at", (object?)GwText.Kst(a.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, "delete from works.activity where gw_activity_id is not null and doc_id = any(@scope) and gw_activity_id <> all(@ids)", ct,
            ("scope", scope), ("ids", activityIds.Keys.ToArray()));
        foreach (var c in snap.Comments)
        {
            if (!activityIds.TryGetValue(c.ActivityId, out var activityId)) continue;
            await ExecAsync(conn, tx, @"insert into works.comment (activity_id, gw_comment_id, body, author_name, created_at)
                values (@act, @gw, @body, @by, @at::timestamptz)
                on conflict (gw_comment_id) do update set activity_id = excluded.activity_id, body = excluded.body, author_name = excluded.author_name, created_at = excluded.created_at", ct,
                ("act", activityId), ("gw", c.Id), ("body", (object?)GwText.HtmlToText(c.Body) ?? DBNull.Value),
                ("by", (object?)UserName(snap.Users, c.UserId) ?? DBNull.Value), ("at", (object?)GwText.Kst(c.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, @"delete from works.comment where gw_comment_id is not null and gw_comment_id <> all(@ids)
              and activity_id in (select id from works.activity where doc_id = any(@scope))", ct,
            ("scope", scope), ("ids", snap.Comments.Where(c => activityIds.ContainsKey(c.ActivityId)).Select(c => c.Id).ToArray()));

        // 4) 그룹웨어 변경 이력 (추가만 된다 — 이미 있는 것은 그대로)
        var logs = snap.Logs.Where(l => docIds.ContainsKey(l.DocId)).Select(l => (l, h: GwText.ParseLog(l.Message))).ToList();
        if (logs.Count > 0)
            await using (var cmd = new NpgsqlCommand(@"insert into works.history (doc_id, gw_log_id, kind, title, detail, status_from, status_to, actor_name, actor_gw_id, at)
                select * from unnest(@doc, @gw, @kind, @title, @detail, @from, @to, @actor, @actorId, @at) on conflict (gw_log_id) do nothing", conn, tx))
            {
                cmd.Parameters.AddWithValue("doc", logs.Select(x => docIds[x.l.DocId]).ToArray());
                cmd.Parameters.AddWithValue("gw", logs.Select(x => x.l.Id).ToArray());
                cmd.Parameters.AddWithValue("kind", logs.Select(x => x.h.Kind).ToArray());
                cmd.Parameters.AddWithValue("title", logs.Select(x => x.h.Title).ToArray());
                cmd.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = logs.Select(x => (object?)x.h.Detail ?? DBNull.Value).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = logs.Select(x => (object?)x.h.From ?? DBNull.Value).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = logs.Select(x => (object?)x.h.To ?? DBNull.Value).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = logs.Select(x => (object?)UserName(snap.Users, x.l.ActorId) ?? DBNull.Value).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("actorId", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = logs.Select(x => (object?)x.l.ActorId ?? DBNull.Value).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = logs.Select(x => (object?)GwText.Kst(x.l.At) ?? DBNull.Value).ToArray() });
                await cmd.ExecuteNonQueryAsync(ct);
            }

        // 5) 첨부 파일 정보
        foreach (var f in snap.Files)
        {
            if (!docIds.TryGetValue(f.DocId, out var docId)) continue;
            long? activityId = f.ActivityId is { } ga && activityIds.TryGetValue(ga, out var aid) ? aid : null;
            await ExecAsync(conn, tx, @"insert into works.file (doc_id, activity_id, source, gw_attach_id, name, ext, size_bytes, gw_path, gw_host, created_at)
                values (@doc, @act, @src, @gw, @name, @ext, @size, @path, @host, @at::timestamptz)
                on conflict (source, gw_attach_id) do update set doc_id = excluded.doc_id, activity_id = excluded.activity_id, name = excluded.name,
                  ext = excluded.ext, size_bytes = excluded.size_bytes, gw_path = excluded.gw_path, gw_host = excluded.gw_host, created_at = excluded.created_at", ct,
                ("doc", docId), ("act", (object?)activityId ?? DBNull.Value), ("src", f.Source), ("gw", f.AttachId),
                ("name", (object?)f.Name ?? DBNull.Value), ("ext", (object?)f.Ext ?? DBNull.Value), ("size", (object?)f.Size ?? DBNull.Value),
                ("path", (object?)f.Path ?? DBNull.Value), ("host", (object?)f.Host ?? DBNull.Value), ("at", (object?)GwText.Kst(f.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, @"delete from works.file m where m.gw_attach_id is not null and m.doc_id = any(@scope)
              and not exists (select 1 from unnest(@src, @ids) as s(src, id) where s.src = m.source and s.id = m.gw_attach_id)", ct,
            ("scope", scope), ("src", snap.Files.Select(f => f.Source).ToArray()), ("ids", snap.Files.Select(f => f.AttachId).ToArray()));

        await tx.CommitAsync(ct);
        return new WriteResult(created, applied, overwritten, managed, deleted);
    }

    private sealed record MergeResult(long DocId, bool Created, bool Managed, int Applied, int Overwritten,
        List<string> Keys, long? StatusFrom = null, long? StatusTo = null);

    private static async Task<MergeResult> MergeDocAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long applet, Doc d,
        Dictionary<string, string?> gw, Dictionary<string, string?> gwDisplay, Dictionary<long, string> users, CancellationToken ct)
    {
        var creator = d.Values.TryGetValue("creator", out var cv) ? cv.Long : null;
        var updater = d.Values.TryGetValue("updater", out var uv) ? uv.Long : null;
        var createdAt = GwText.Kst(d.Values.TryGetValue("create_date", out var cd) ? cd.Date ?? d.Created : d.Created);
        var updatedAt = GwText.Kst(d.Values.TryGetValue("update_date", out var ud) ? ud.Date ?? d.Updated : d.Updated);
        var gwJson = JsonSerializer.Serialize(gw);
        var gwDisplayJson = JsonSerializer.Serialize(gwDisplay);

        long? docId = null;
        var isManaged = false;
        Dictionary<string, string?>? cur = null, curDisplay = null;
        long? curStatus = null;
        await using (var find = new NpgsqlCommand(@"select id, managed_in_dyone, ""values""::text, display::text, status_id from works.doc where gw_doc_id = @g", conn, tx))
        {
            find.Parameters.AddWithValue("g", d.Id);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                docId = r.GetInt64(0);
                isManaged = r.GetBoolean(1);
                cur = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(2));
                curDisplay = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(3));
                curStatus = r.IsDBNull(4) ? null : r.GetInt64(4);
            }
        }

        // 처음 보는 문서: 그룹웨어 값으로 만든다
        if (docId is null)
        {
            var id = await ScalarAsync(conn, tx, @"insert into works.doc (applet_id, gw_doc_id, status_id, ""values"", display, created_by_name, created_at, updated_by_name, updated_at, synced_at)
                values (@a, @g, @s, @v, @d, @cBy, @cAt::timestamptz, @uBy, @uAt::timestamptz, now()) returning id", ct,
                ("a", applet), ("g", d.Id), ("s", (object?)d.StatusId ?? DBNull.Value), ("v", Jsonb(gwJson)), ("d", Jsonb(gwDisplayJson)),
                ("cBy", (object?)UserName(users, creator) ?? DBNull.Value), ("cAt", (object?)createdAt ?? DBNull.Value),
                ("uBy", (object?)UserName(users, updater) ?? DBNull.Value), ("uAt", (object?)updatedAt ?? DBNull.Value));
            await SaveSnapshotAsync(conn, tx, id, d, gwJson, gwDisplayJson, updatedAt, ct);
            return new MergeResult(id, true, false, 0, 0, []);
        }

        var did = docId.Value;
        Dictionary<string, string?>? before = null, beforeDisplay = null;
        long? beforeStatus = null;
        await using (var s = new NpgsqlCommand(@"select ""values""::text, display::text, status_id from works.doc_gw where doc_id = @d", conn, tx))
        {
            s.Parameters.AddWithValue("d", did);
            await using var r = await s.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                before = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(0));
                beforeDisplay = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(1));
                beforeStatus = r.IsDBNull(2) ? null : r.GetInt64(2);
            }
        }

        // 그룹웨어 사본이 지난번과 완전히 같으면 할 일이 없다(삭제 표시만 해제)
        var sameSnapshot = before is not null && beforeStatus == d.StatusId && SameValues(before, gw) && SameValues(beforeDisplay, gwDisplay);
        if (sameSnapshot)
        {
            await ExecAsync(conn, tx, "update works.doc set gw_deleted_at = null where id = @d and gw_deleted_at is not null", ct, ("d", did));
            return new MergeResult(did, false, isManaged, 0, 0, []);
        }

        int applied = 0, over = 0;
        var changedKeys = new List<string>();
        long? statusFrom = null, statusTo = null;
        if (before is not null && !isManaged && cur is not null && curDisplay is not null)
        {
            string? Get(IReadOnlyDictionary<string, string?>? dict, string key) => dict is not null && dict.TryGetValue(key, out var v) ? v : null;
            var at = updatedAt ?? DateTimeOffset.UtcNow;
            var by = UserName(users, updater);
            var keys = gw.Keys.Union(before.Keys).ToList();
            foreach (var key in keys)
            {
                var prevGw = Get(before, key);
                var nextGw = Get(gw, key);
                var mine = Get(cur, key);
                if (prevGw == nextGw)
                {
                    // 값은 그대로인데 표시만 바뀐 경우(참조 문서 제목·사용자 이름 등): DY ONE 값이 그룹웨어 값과 같을 때만 표시를 따라간다
                    if (mine == nextGw && Get(curDisplay, key) != Get(gwDisplay, key)) curDisplay[key] = Get(gwDisplay, key);
                    continue;
                }
                if (mine == nextGw) { curDisplay[key] = Get(gwDisplay, key); continue; }   // 이미 같은 값
                var dyEdited = mine != prevGw;
                cur[key] = nextGw;
                curDisplay[key] = Get(gwDisplay, key);
                await InsertChangeAsync(conn, tx, did, key, mine, nextGw, dyEdited ? mine : null, by, at, ct);
                applied++;
                if (dyEdited) over++;
                changedKeys.Add(key);
            }
            var status = curStatus;
            if (beforeStatus != d.StatusId && curStatus != d.StatusId)
            {
                var dyEdited = curStatus != beforeStatus;
                await InsertChangeAsync(conn, tx, did, "status", curStatus?.ToString(), d.StatusId?.ToString(), dyEdited ? curStatus?.ToString() : null, by, at, ct);
                status = d.StatusId;
                applied++;
                if (dyEdited) over++;
                changedKeys.Add("status");
                (statusFrom, statusTo) = (curStatus, d.StatusId);
            }
            await ExecAsync(conn, tx, @"update works.doc set ""values"" = @v, display = @d, status_id = @s,
                  updated_by_name = case when @changed then @by else updated_by_name end,
                  updated_at = case when @changed then @at::timestamptz else updated_at end where id = @id", ct,
                ("id", did), ("v", Jsonb(JsonSerializer.Serialize(cur))), ("d", Jsonb(JsonSerializer.Serialize(curDisplay))),
                ("s", (object?)status ?? DBNull.Value), ("changed", applied > 0), ("by", (object?)by ?? DBNull.Value), ("at", at));
        }

        // 비교 대상이 아닌 그룹웨어 정보(등록자·등록일)는 항상 최신으로, 삭제 표시는 해제
        await ExecAsync(conn, tx, "update works.doc set created_by_name = @cBy, created_at = @cAt::timestamptz, gw_deleted_at = null, synced_at = now() where id = @d", ct,
            ("d", did), ("cBy", (object?)UserName(users, creator) ?? DBNull.Value), ("cAt", (object?)createdAt ?? DBNull.Value));
        await SaveSnapshotAsync(conn, tx, did, d, gwJson, gwDisplayJson, updatedAt, ct);
        return new MergeResult(did, false, isManaged, applied, over, changedKeys, statusFrom, statusTo);
    }

    private static Task InsertChangeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long docId, string field, string? before, string? after,
        string? overwritten, string? by, DateTimeOffset at, CancellationToken ct) =>
        ExecAsync(conn, tx, @"insert into works.field_change (doc_id, field, before_value, after_value, overwritten, source, by_name, at)
            values (@d, @f, @b, @a, @o, 'groupware', @by, @at)", ct,
            ("d", docId), ("f", field), ("b", (object?)before ?? DBNull.Value), ("a", (object?)after ?? DBNull.Value),
            ("o", (object?)overwritten ?? DBNull.Value), ("by", (object?)by ?? DBNull.Value), ("at", at));

    private static Task SaveSnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long docId, Doc d, string values, string display,
        DateTimeOffset? updatedAt, CancellationToken ct) =>
        ExecAsync(conn, tx, @"insert into works.doc_gw (doc_id, gw_doc_id, status_id, ""values"", display, gw_updated_at, synced_at)
            values (@doc, @g, @s, @v, @d, @u::timestamptz, now())
            on conflict (doc_id) do update set gw_doc_id = excluded.gw_doc_id, status_id = excluded.status_id, ""values"" = excluded.""values"",
              display = excluded.display, gw_updated_at = excluded.gw_updated_at, synced_at = now()", ct,
            ("doc", docId), ("g", d.Id), ("s", (object?)d.StatusId ?? DBNull.Value), ("v", Jsonb(values)), ("d", Jsonb(display)),
            ("u", (object?)updatedAt ?? DBNull.Value));

    // ── 도우미 ─────────────────────────────────────────────────────────────
    /// <summary>그룹웨어 앱 이름에서 앞 번호를 뗀다 (예: '04. ES_ 이슈사항 보고' → 'ES_ 이슈사항 보고')</summary>
    private static string ShortName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\d+\.\s*", "").Trim();

    /// <summary>칸 값 비교(jsonb는 키 순서를 바꾸므로 글자로 비교하지 않는다). 없는 키와 null은 같다.</summary>
    private static bool SameValues(IReadOnlyDictionary<string, string?>? a, IReadOnlyDictionary<string, string?> b)
    {
        if (a is null) return false;
        foreach (var key in a.Keys.Union(b.Keys))
            if ((a.TryGetValue(key, out var x) ? x : null) != (b.TryGetValue(key, out var y) ? y : null)) return false;
        return true;
    }

    private static string? UserName(Dictionary<long, string> users, long? id) => id is { } v && users.TryGetValue(v, out var n) ? n : null;

    private static NpgsqlParameter Jsonb(string? json) => new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = (object?)json ?? DBNull.Value };

    private static long? ParseWatermark(string? text) =>
        text is not null && text.StartsWith("log:") && long.TryParse(text[4..], out var n) ? n : null;

    private static async Task<bool> HasSchemaAsync(string conn, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select to_regclass('works.doc') is not null", c);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string?> ReadWatermarkAsync(string conn, string entity, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select watermark from sync.state where source = 'groupware' and entity = @e", c);
        cmd.Parameters.AddWithValue("e", entity);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task WriteWatermarkAsync(string conn, string entity, long log, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', @e, @w, now())
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", c);
        cmd.Parameters.AddWithValue("e", entity);
        cmd.Parameters.AddWithValue("w", $"log:{log}");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TryRecordErrorAsync(string writeConn, string entity, string message, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into sync.error (source, entity, source_pk, message, created_at) values ('groupware', @e, null, @m, now())", conn);
            cmd.Parameters.AddWithValue("e", entity);
            cmd.Parameters.AddWithValue("m", message.Length > 2000 ? message[..2000] : message);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "sync.error 기록 실패");
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) Add(cmd, name, value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string name, object value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) Add(cmd, name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Add(NpgsqlCommand cmd, string name, object value)
    {
        if (value is NpgsqlParameter p) { p.ParameterName = name; cmd.Parameters.Add(p); }
        else cmd.Parameters.AddWithValue(name, value);
    }
}
