using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DyOne.Data.Entities;
using DyOne.Data.Modular;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어(tims) WORKS applet 376 "모듈라빌드 프로젝트 정보"를 dyone DB modular.* 로 미러한다.
///
/// 원천(GW): 조회 전용. 선택지·상태·전환 정의, 문서(EAV 피벗)·복수 선택값, 활동 글·댓글, 변경 이력(go_action_logs),
///           첨부 파일 정보(go_attach_files: 이름·크기·서버 경로)를 읽는다. 파일 본체는 그룹웨어 서버 디스크에 있어 복사하지 않는다.
/// 변경 감지: GwChangeFeed(그룹웨어 전체 변경 이력 피드)로 마지막으로 본 번호 뒤에 이력이 생긴 376 문서만 다시 읽는다.
///           그룹웨어는 외부로 알림(웹훅)을 보내지 않고, 알림 테이블(go_noti_messages)에는 376 상태 변경이 남지 않아
///           모든 변경이 남는 변경 이력을 신호로 쓴다. 프로세스 시작 직후와 FullSyncHours마다 전체를 비교한다(삭제 확인 포함).
/// 대상(dyone): 두 벌 관리. modular.build_gw = 마지막으로 읽은 그룹웨어 값, modular.build = DY ONE 값.
///             그룹웨어 값이 지난번과 달라진 칸만 DY ONE 값에 덮어쓰고(modular.field_change에 기록, 덮인 DY ONE 값은 overwritten),
///             그룹웨어에서 안 바뀐 칸은 DY ONE에서 고친 값을 그대로 둔다. DY ONE 변경은 그룹웨어로 보내지 않는다.
///             managed_in_dyone 인 건(DY ONE 관리 전환)은 사본만 갱신하고 반영하지 않는다.
///             활동 글·댓글·첨부 정보·변경 이력은 그룹웨어 것을 그대로 옮긴다(DY ONE에서 쓴 것은 gw id가 없어 건드리지 않는다).
///             그룹웨어에서 사라진 문서는 지우지 않고 gw_deleted_at으로 표시한다.
/// modular 스키마가 없는 쓰기 대상(예: 아직 적용 전인 운영)은 건너뛴다.
/// 결과: sync.state(groupware/modular) 기록, 실패 시 sync.error 기록.
/// </summary>
public partial class GwModularSyncJob(IOptions<GwModularSyncOptions> options, IConfiguration config, ILogger<GwModularSyncJob> logger)
    : IWorkerJob
{
    private const long AppletId = 376;
    private readonly GwModularSyncOptions _opt = options.Value;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    private DateTimeOffset _lastFull = DateTimeOffset.MinValue;

    public string Name => "gw-modular-sync";
    public bool RunOnStartup => _opt.RunOnStartup;

    public TimeSpan UntilNext(DateTimeOffset nowUtc)
    {
        if (_opt.IntervalMinutes > 0) return TimeSpan.FromMinutes(_opt.IntervalMinutes);
        var at = TimeOnly.Parse(_opt.DailyAt);
        var local = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var today = new DateTimeOffset(local.Year, local.Month, local.Day, at.Hour, at.Minute, 0, local.Offset);
        var next = local < today ? today : today.AddDays(1);
        return next - local;
    }

    // ── 필드 키 (그룹웨어 applet 376 폼 정의) ─────────────────────────────
    private static readonly Dictionary<string, string> OptionLists = new()
    {
        ["_i9ymbmlpm"] = "install_location",
        ["_jzgc031hz"] = "robot_model",
        ["_w1q77c8b0"] = "cable_robot_box",
        ["_x1la1l7d6"] = "cable_box_rc",
        ["_qo86vuddy"] = "comm",
        ["_aqtckgnv0"] = "applicator",
        ["_d8w4r5aig"] = "category",
        ["_6ssce5d3v"] = "region",
    };

    private static readonly Dictionary<string, string> MemberRoles = new()
    {
        ["_tpe5syh92"] = "pm",
        ["_nlzsevy0y"] = "pl",
        ["_a36t43tlm"] = "control",
        ["_o1dpsa7kb"] = "design",
        ["_qovciv1c4"] = "teach",
    };

    /// <summary>현재 폼에 있는 필드 키. 그 밖의 키는 폼에서 지워진 옛 필드라 legacy에 보관한다.</summary>
    private static readonly HashSet<string> KnownKeys =
    [
        "_7cz5srcqa", "_zzv3du17w", "_j5ioejkql", "_e9l4te74o", "_18qsghkdw", "_i9ymbmlpm", "_stqqsx447", "_3qfe7pbl3",
        "_g91atexaz", "_1opepamz0", "_r3etmju2o", "_disj88h9s", "_jzgc031hz", "_w1q77c8b0", "_x1la1l7d6", "_qo86vuddy",
        "_aqtckgnv0", "_ij4auyj7h", "_uprsdqxub", "_o9e6bm4on", "_tpe5syh92", "_tbhkmxoig", "_ls94wtdm7", "_nlzsevy0y",
        "_a36t43tlm", "_d8w4r5aig", "_6ssce5d3v", "_o1dpsa7kb", "_qovciv1c4", "_zi5k4oa1v",
        "create_date", "creator", "update_date", "updater",
    ];

    private sealed record OptionRow(string ListKey, int Code, string Label, int Sort, bool IsDefault);
    private sealed record StatusRow(long Id, string Name, int Sort, string? Color, bool IsEnd, bool IsBegin);
    private sealed record TransitionRow(long Id, long From, long To, string Action, int Sort);
    private sealed record ActivityRow(long Id, long DocId, string? Html, int Comments, int Attaches, long? ById, DateTime? At);
    private sealed record LogRow(long Id, long DocId, string Message, long? ActorId, DateTime? At);
    private sealed record CommentRow(long Id, long ActivityId, string? Body, long? UserId, DateTime? At);
    private sealed record FileRow(string Source, long DocId, long? ActivityId, long AttachId, string? Name, string? Ext, long? Size, string? Path, string? Host, DateTime? At);

    /// <summary>그룹웨어 값 하나. 복수 선택은 Multi에 선택지 코드 목록.</summary>
    private sealed record GwValue(string? Str, long? Long, double? Dbl, DateTime? Date, string? Text, long[]? Multi);

    private sealed class Doc
    {
        public long Id;
        public long? StatusId;
        public DateTime? Created, Updated;
        public readonly Dictionary<string, GwValue> Values = [];
    }

    private sealed class Snapshot
    {
        public List<OptionRow> Options = [];
        public List<StatusRow> Statuses = [];
        public List<TransitionRow> Transitions = [];
        public List<Doc> Docs = [];
        public List<ActivityRow> Activities = [];
        public List<LogRow> Logs = [];
        public List<CommentRow> Comments = [];
        public List<FileRow> Files = [];
        public Dictionary<long, string> Users = [];
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");

        // 변경 감지: 그룹웨어 전체의 마지막 변경 이력 번호를 쓰기 대상마다 마지막으로 반영한 번호(sync.state)와 비교한다.
        // 같으면 건너뛰고, 다르면 그 사이에 이력이 생긴 376 문서만 읽는다. FullSyncHours마다(프로세스 시작 직후 포함) 전체 비교.
        // 번호는 조회 전에 먼저 읽어 둔다 — 조회 중에 생긴 변경은 다음 확인에서 잡힌다.
        var marker = new Marker(await GwChangeFeed.MaxLogAsync(gwConn, ct));
        var fullDue = DateTimeOffset.UtcNow - _lastFull >= TimeSpan.FromHours(Math.Max(1, _opt.FullSyncHours));

        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");
        Exception? firstError = null;
        var allFull = true;
        foreach (var (name, conn) in targets)
        {
            try
            {
                if (!await HasSchemaAsync(conn, ct))
                {
                    logger.LogWarning("[{Target}] modular 스키마가 없어 건너뜀 (server/db/modular_schema.sql 적용 필요)", name);
                    continue;
                }

                // 이 대상이 마지막으로 반영한 지점. 문서 수가 같으면 그 뒤에 변경 이력이 생긴 문서만 다시 읽는다.
                var last = Marker.Parse(await ReadWatermarkAsync(conn, ct));
                if (!fullDue && last is not null && last.Log >= marker.Log)
                {
                    logger.LogDebug("[{Target}] GW 변경 없음 ({Marker})", name, marker.Text);
                    continue;
                }
                var full = fullDue || last is null;
                long[]? docs = null;
                if (!full)
                {
                    docs = (await GwChangeFeed.ChangedDocsAsync(gwConn, last!.Log, [AppletId], ct))[AppletId];
                    if (docs.Length == 0)
                    {
                        await WriteWatermarkAsync(conn, marker, ct);
                        continue;
                    }
                }
                allFull &= full;

                var snap = await ReadGroupwareAsync(gwConn, docs, ct);
                logger.LogInformation("[{Target}] GW applet376 {Mode}: 문서 {Docs}건 · 활동 {Act}건 · 댓글 {Cm}건 · 이력 {Log}건 · 첨부 {File}개",
                    name, full ? "전체 조회" : "바뀐 문서 조회", snap.Docs.Count, snap.Activities.Count, snap.Comments.Count, snap.Logs.Count, snap.Files.Count);
                if (snap.Statuses.Count == 0 || (full && snap.Docs.Count == 0))
                {
                    logger.LogWarning("[{Target}] GW 조회 결과가 비어 있어 적재를 건너뛴다(삭제 표시 방지).", name);
                    continue;
                }
                if (_opt.DryRun)
                {
                    logger.LogInformation("DryRun: 적재 생략. 샘플 {Sample}",
                        string.Join(" / ", snap.Docs.Take(3).Select(d => $"{Str(d, "_zzv3du17w")} {Str(d, "_j5ioejkql")}")));
                    continue;
                }

                var result = await WriteAsync(conn, snap, full, marker, notify: last is not null, ct);
                logger.LogInformation("[{Target}] 모듈라 반영: 새 문서 {New}건 · 그룹웨어에서 바뀐 칸 {Applied}개(DY ONE 값 덮음 {Over}개) · DY ONE 관리 건 건너뜀 {Skip}건 · 그룹웨어 삭제 표시 {Del}건",
                    name, result.Created, result.Applied, result.Overwritten, result.Managed, result.Deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
                logger.LogError(ex, "[{Target}] 모듈라 적재 실패", name);
                await TryRecordErrorAsync(conn, ex.Message, ct);
            }
        }
        if (firstError is not null) throw firstError;
        if (fullDue && allFull) _lastFull = DateTimeOffset.UtcNow;
    }

    /// <summary>변경 감지 지점: 그룹웨어 전체 변경 이력 번호(go_applet_doc_logs.log_id). sync.state.watermark에 "log:M"으로 남긴다.</summary>
    private sealed record Marker(long Log)
    {
        public string Text => $"log:{Log}";

        /// <summary>"log:M" (예전 형식 "docs:N|log:M"도 읽는다)</summary>
        public static Marker? Parse(string? text)
        {
            var m = text is null ? null : MarkerText().Match(text);
            return m is { Success: true } ? new Marker(long.Parse(m.Groups[1].Value)) : null;
        }
    }

    private static async Task<string?> ReadWatermarkAsync(string conn, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select watermark from sync.state where source = 'groupware' and entity = 'modular'", c);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task WriteWatermarkAsync(string conn, Marker marker, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', 'modular', @w, now())
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", c);
        cmd.Parameters.AddWithValue("w", marker.Text);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── 그룹웨어 읽기 ─────────────────────────────────────────────────────
    /// <param name="docs">null이면 376 전체, 아니면 이 문서들만 읽는다(정의는 항상 전체).</param>
    private static async Task<Snapshot> ReadGroupwareAsync(string gwConn, long[]? docs, CancellationToken ct)
    {
        string Scope(string column) => docs is null ? "" : $" and {column} = any(@docs)";
        void Bind(NpgsqlCommand cmd) { if (docs is not null) cmd.Parameters.AddWithValue("docs", docs); }
        var snap = new Snapshot();
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using (var ro = new NpgsqlCommand("set transaction read only", conn, tx)) await ro.ExecuteNonQueryAsync(ct);

        // 선택지: 폼 필드 정의(properties_as_json.items) 그대로
        await using (var cmd = new NpgsqlCommand(
            @"select f.cid, f.properties_as_json from go_applet_form_fields ff join go_applet_fields f on f.id = ff.field_id
              where ff.applet_form_id = (select form_id from go_applets where id = @a) and f.cid = any(@cids)", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            cmd.Parameters.AddWithValue("cids", OptionLists.Keys.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Options.AddRange(ParseOptions(OptionLists[r.GetString(0)], r.IsDBNull(1) ? null : r.GetString(1)));
        }

        // 상태·전환
        await using (var cmd = new NpgsqlCommand(
            @"select s.id, s.name, s.seq, s.color, s.is_end, s.id = p.begin_status_id
              from go_applets a join go_applet_processes p on p.id = a.process_id
              join go_applet_process_statuses ps on ps.applet_process_id = p.id
              join go_applet_statuses s on s.id = ps.status_id
              where a.id = @a", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Statuses.Add(new StatusRow(r.GetInt64(0), r.GetString(1).Trim(), r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3), !r.IsDBNull(4) && r.GetBoolean(4), !r.IsDBNull(5) && r.GetBoolean(5)));
        }
        var statusIds = snap.Statuses.Select(s => s.Id).ToArray();
        await using (var cmd = new NpgsqlCommand(
            @"select id, before_status_id, next_status_id, action_name, seq from go_applet_status_transitions
              where before_status_id = any(@s) and next_status_id = any(@s)", conn, tx))
        {
            cmd.Parameters.AddWithValue("s", statusIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Transitions.Add(new TransitionRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3).Trim(), r.IsDBNull(4) ? 0 : r.GetInt32(4)));
        }

        // 문서 값 (EAV). 복수 선택은 go_applet_val_multi_values 에 선택지 코드가 들어 있다.
        var docMap = new Dictionary<long, Doc>();
        await using (var cmd = new NpgsqlCommand(
            @"select d.id, d.status_id, d.created_at, d.updated_at, dv.values_key,
                     v.string_value, v.long_value, v.double_value, v.date_value, v.text_value,
                     case when v.category = 'MULTIVALUES' then array(
                       select m.long_value from go_applet_val_multi_values mv join go_applet_vals m on m.id = mv.val_id
                       where mv.parent_id = v.id and m.long_value is not null order by mv.seq) end as multi
              from go_applet_docs d
              left join go_applet_doc_values dv on dv.applet_doc_id = d.id
              left join go_applet_vals v on v.id = dv.value_id
              where d.applet_id = @a" + Scope("d.id"), conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                if (!docMap.TryGetValue(id, out var doc))
                {
                    doc = new Doc
                    {
                        Id = id,
                        StatusId = r.IsDBNull(1) ? null : r.GetInt64(1),
                        Created = r.IsDBNull(2) ? null : r.GetDateTime(2),
                        Updated = r.IsDBNull(3) ? null : r.GetDateTime(3),
                    };
                    docMap[id] = doc;
                }
                if (r.IsDBNull(4)) continue;
                doc.Values[r.GetString(4)] = new GwValue(
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetDouble(7),
                    r.IsDBNull(8) ? null : r.GetDateTime(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetFieldValue<long[]>(10));
            }
        }
        snap.Docs = [.. docMap.Values];

        // 활동 글
        await using (var cmd = new NpgsqlCommand(
            @"select a.id, a.applet_doc_id, c.contents, coalesce(a.comment_count, 0),
                     (select count(*) from go_applet_activity_attaches x where x.applet_activity_id = a.id),
                     a.created_by_id, a.created_at
              from go_applet_activities a
              left join go_applet_act_contents c on c.activity_id = a.id
              where a.applet_doc_id in (select id from go_applet_docs where applet_id = @a)" + Scope("a.applet_doc_id"), conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Activities.Add(new ActivityRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                    (int)r.GetInt64(3), (int)r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetDateTime(6)));
        }

        // 활동 글 댓글
        await using (var cmd = new NpgsqlCommand(
            @"select c.id, x.activity_id, c.message, c.user_id, c.created_at
              from go_applet_activity_comments x join go_comments c on c.id = x.id
              where x.activity_id in (select a.id from go_applet_activities a join go_applet_docs d on d.id = a.applet_doc_id where d.applet_id = @a" + Scope("d.id") + ")", conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Comments.Add(new CommentRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        }

        // 변경 이력 (그룹웨어 문서의 '변경 이력' 탭)
        await using (var cmd = new NpgsqlCommand(
            @"select l.id, dl.applet_doc_id, l.message, l.actor_id, l.created_at
              from go_applet_doc_logs dl join go_action_logs l on l.id = dl.log_id
              where dl.applet_doc_id in (select id from go_applet_docs where applet_id = @a)" + Scope("dl.applet_doc_id"), conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Logs.Add(new LogRow(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetDateTime(4)));
        }

        // 첨부 파일 정보 (마지막 판). 활동 글 첨부 + 문서의 '참고자료' 필드(값 = 첨부 id 목록)
        const string fileCols = @"distinct on (r.attach_id) r.attach_id, f.name, f.extension, f.length, f.path, f.host_id, f.created_at
              from go_attach_revisions r join go_attach_files f on f.id = r.attach_file_id";
        await using (var cmd = new NpgsqlCommand(
            $@"select x.applet_activity_id, a.applet_doc_id, q.* from go_applet_activity_attaches x
               join go_applet_activities a on a.id = x.applet_activity_id
               join go_applet_docs d on d.id = a.applet_doc_id and d.applet_id = @a
               join lateral (select {fileCols} where r.attach_id = x.attach_id order by r.attach_id, r.revision desc) q on true
               where true" + Scope("d.id"), conn, tx))
        {
            cmd.Parameters.AddWithValue("a", AppletId);
            Bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Files.Add(ReadFile(r, "activity", r.GetInt64(1), r.GetInt64(0), 2));
        }
        var docFiles = snap.Docs.SelectMany(d => d.Values.TryGetValue("_e9l4te74o", out var v) && v.Multi is { } ids
            ? ids.Select(id => (doc: d.Id, attach: id)) : []).ToList();
        if (docFiles.Count > 0)
        {
            var owner = docFiles.GroupBy(x => x.attach).ToDictionary(g => g.Key, g => g.First().doc);
            await using var cmd = new NpgsqlCommand($"select {fileCols} where r.attach_id = any(@ids) order by r.attach_id, r.revision desc", conn, tx);
            cmd.Parameters.AddWithValue("ids", owner.Keys.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                snap.Files.Add(ReadFile(r, "doc", owner[r.GetInt64(0)], null, 0));
        }

        // 등록자·수정자·글쓴이·이력 작성자 이름 (퇴사자도 이름은 남긴다)
        var userIds = snap.Docs.SelectMany(d => new[] { Long(d, "creator"), Long(d, "updater") })
            .Concat(snap.Activities.Select(a => a.ById)).Concat(snap.Comments.Select(c => c.UserId)).Concat(snap.Logs.Select(l => l.ActorId))
            .OfType<long>().Distinct().ToArray();
        await using (var cmd = new NpgsqlCommand("select id, name from go_users where id = any(@ids)", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", userIds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(1)) snap.Users[r.GetInt64(0)] = r.GetString(1);
        }

        await tx.RollbackAsync(ct);
        return snap;
    }

    /// <summary>fileCols 순서(attach_id, name, extension, length, path, host_id, created_at)를 offset부터 읽는다.</summary>
    private static FileRow ReadFile(NpgsqlDataReader r, string source, long docId, long? activityId, int o) => new(
        source, docId, activityId, r.GetInt64(o),
        r.IsDBNull(o + 1) ? null : r.GetString(o + 1), r.IsDBNull(o + 2) ? null : r.GetString(o + 2),
        r.IsDBNull(o + 3) ? null : r.GetInt64(o + 3), r.IsDBNull(o + 4) ? null : r.GetString(o + 4),
        r.IsDBNull(o + 5) ? null : r.GetString(o + 5), r.IsDBNull(o + 6) ? null : r.GetDateTime(o + 6));

    private static IEnumerable<OptionRow> ParseOptions(string listKey, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) yield break;
        var defaults = new HashSet<int>();
        if (doc.RootElement.TryGetProperty("defaultValue", out var dv))
        {
            if (dv.ValueKind == JsonValueKind.Array)
                foreach (var x in dv.EnumerateArray()) { if (AsInt(x) is { } v) defaults.Add(v); }
            else if (AsInt(dv) is { } v) defaults.Add(v);
        }
        var sort = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("value", out var value) || AsInt(value) is not { } code) continue;
            var label = item.TryGetProperty("displayText", out var t) ? (t.GetString() ?? "").Trim() : "";
            label = MultiSpace().Replace(label, " ");
            var selected = item.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.True;
            yield return new OptionRow(listKey, code, label, sort++, defaults.Contains(code) || (defaults.Count == 0 && selected));
        }
    }

    private static int? AsInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) => n,
        JsonValueKind.String when int.TryParse(e.GetString(), out var n) => n,
        _ => null,
    };

    // ── dyone 쓰기 ─────────────────────────────────────────────────────────
    private static async Task<bool> HasSchemaAsync(string conn, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select to_regclass('modular.build') is not null", c);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private sealed record WriteResult(int Created, int Applied, int Overwritten, int Managed, int Deleted);

    /// <param name="full">true면 376 전체를 읽은 것 → 그룹웨어에서 사라진 문서를 표시한다.</param>
    /// <param name="notify">false면 알림을 남기지 않는다(처음 전체 적재)</param>
    private async Task<WriteResult> WriteAsync(string writeConn, Snapshot snap, bool full, Marker marker, bool notify, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1) 정의: 선택지·상태·전환 (그룹웨어 그대로)
        foreach (var o in snap.Options)
            await ExecAsync(conn, tx, @"insert into modular.option (list_key, code, label, sort, is_default, is_active)
                values (@k, @c, @l, @s, @d, true)
                on conflict (list_key, code) do update set label = excluded.label, sort = excluded.sort, is_default = excluded.is_default, is_active = true", ct,
                ("k", o.ListKey), ("c", o.Code), ("l", o.Label), ("s", o.Sort), ("d", o.IsDefault));
        await DeactivateMissingOptionsAsync(conn, tx, snap.Options, ct);

        foreach (var s in snap.Statuses)
            await ExecAsync(conn, tx, @"insert into modular.status (id, name, sort, color, is_end, is_begin) values (@i, @n, @s, @c, @e, @b)
                on conflict (id) do update set name = excluded.name, sort = excluded.sort, color = excluded.color, is_end = excluded.is_end, is_begin = excluded.is_begin", ct,
                ("i", s.Id), ("n", s.Name), ("s", s.Sort), ("c", (object?)s.Color ?? DBNull.Value), ("e", s.IsEnd), ("b", s.IsBegin));
        await ExecAsync(conn, tx, "delete from modular.transition where id <> all(@ids)", ct, ("ids", snap.Transitions.Select(t => t.Id).ToArray()));
        foreach (var t in snap.Transitions)
            await ExecAsync(conn, tx, @"insert into modular.transition (id, from_status, to_status, action_name, sort) values (@i, @f, @t, @a, @s)
                on conflict (id) do update set from_status = excluded.from_status, to_status = excluded.to_status, action_name = excluded.action_name, sort = excluded.sort", ct,
                ("i", t.Id), ("f", t.From), ("t", t.To), ("a", t.Action), ("s", t.Sort));

        // 2) 문서 — 두 벌 관리(칸별 반영)
        var buildIds = new Dictionary<long, long>();
        int created = 0, applied = 0, overwritten = 0, managed = 0;
        var notices = new List<GwNotices.Notice>();
        var statusNames = snap.Statuses.ToDictionary(x => x.Id.ToString(), x => x.Name);
        foreach (var d in snap.Docs)
        {
            var r = await MergeDocAsync(conn, tx, d, snap.Users, ct);
            buildIds[d.Id] = r.BuildId;
            if (r.Created) created++;
            if (r.Managed) managed++;
            applied += r.Applied;
            overwritten += r.Overwritten;

            var title = Str(d, "_j5ioejkql") ?? Str(d, "_zzv3du17w") ?? $"문서 {d.Id}";
            var link = $"/modular?doc={r.BuildId}";
            if (r.Created)
                notices.Add(new GwNotices.Notice(r.BuildId, "created", "그룹웨어 새 문서 · 모듈라", title, link));
            else if (r.Applied > 0)
            {
                string? Name(string? id) => id is not null && statusNames.TryGetValue(id, out var n) ? n : id;
                var labels = r.Keys.Where(k => k != "status_id").Select(k => ModularFieldSet.ByKey.TryGetValue(k, out var f) ? f.Label : k).ToList();
                var summary = GwNotices.Summary(Name(r.StatusFrom), r.Keys.Contains("status_id") ? Name(r.StatusTo) : null, labels, r.Overwritten);
                notices.Add(new GwNotices.Notice(r.BuildId, "changed", "그룹웨어 변경 · 모듈라", $"{title} — {summary}", link));
            }
        }

        // 그룹웨어에서 사라진 문서는 지우지 않고 표시만 한다(전체 조회일 때만 판단)
        var deleted = 0;
        if (full)
        {
            await using var del = new NpgsqlCommand(
                "update modular.build set gw_deleted_at = now() where gw_doc_id is not null and gw_deleted_at is null and gw_doc_id <> all(@ids)", conn, tx);
            del.Parameters.AddWithValue("ids", snap.Docs.Select(d => d.Id).ToArray());
            deleted = await del.ExecuteNonQueryAsync(ct);
        }
        if (deleted > 0)
            notices.Add(new GwNotices.Notice(0, "deleted", "그룹웨어 삭제 · 모듈라", $"그룹웨어에서 {deleted}건이 지워졌습니다. DY ONE에는 '그룹웨어 삭제'로 표시해 남겨 두었습니다.", "/modular"));
        if (notify) await GwNotices.WriteAsync(conn, tx, "modular", "모듈라", "/modular", notices, ct);
        var scope = buildIds.Values.ToArray();

        // 3) 활동 글 (그룹웨어에서 지운 글은 DY ONE에서도 지운다 — 댓글·첨부 정보도 함께 지워진다)
        var activityIds = new Dictionary<long, long>();
        foreach (var a in snap.Activities)
        {
            if (!buildIds.TryGetValue(a.DocId, out var buildId)) continue;
            activityIds[a.Id] = await ScalarAsync(conn, tx, @"insert into modular.activity (build_id, gw_activity_id, body_html, body_text, comment_count, attach_count, created_by_name, created_at)
                values (@build, @gwid, @html, @text, @comments, @attaches, @author, @written::timestamptz)
                on conflict (gw_activity_id) do update set build_id = excluded.build_id, body_html = excluded.body_html, body_text = excluded.body_text,
                  comment_count = excluded.comment_count, attach_count = excluded.attach_count, created_by_name = excluded.created_by_name, created_at = excluded.created_at
                returning id", ct,
                ("build", buildId), ("gwid", a.Id), ("html", (object?)a.Html ?? DBNull.Value), ("text", (object?)HtmlToText(a.Html) ?? DBNull.Value),
                ("comments", a.Comments), ("attaches", a.Attaches), ("author", (object?)UserName(snap.Users, a.ById) ?? DBNull.Value),
                ("written", (object?)Kst(a.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, "delete from modular.activity where gw_activity_id is not null and build_id = any(@scope) and gw_activity_id <> all(@ids)", ct,
            ("scope", scope), ("ids", activityIds.Keys.ToArray()));

        // 4) 댓글
        foreach (var c in snap.Comments)
        {
            if (!activityIds.TryGetValue(c.ActivityId, out var activityId)) continue;
            await ExecAsync(conn, tx, @"insert into modular.comment (activity_id, gw_comment_id, body, author_name, created_at)
                values (@activity, @gwid, @body, @author, @written::timestamptz)
                on conflict (gw_comment_id) do update set activity_id = excluded.activity_id, body = excluded.body,
                  author_name = excluded.author_name, created_at = excluded.created_at", ct,
                ("activity", activityId), ("gwid", c.Id), ("body", (object?)HtmlToText(c.Body) ?? DBNull.Value),
                ("author", (object?)UserName(snap.Users, c.UserId) ?? DBNull.Value), ("written", (object?)Kst(c.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, @"delete from modular.comment where gw_comment_id is not null and gw_comment_id <> all(@ids)
              and activity_id in (select id from modular.activity where build_id = any(@scope))", ct,
            ("scope", scope), ("ids", snap.Comments.Where(c => activityIds.ContainsKey(c.ActivityId)).Select(c => c.Id).ToArray()));

        // 5) 변경 이력 (추가만 된다)
        foreach (var l in snap.Logs)
        {
            if (!buildIds.TryGetValue(l.DocId, out var buildId)) continue;
            var h = ParseLog(l.Message);
            await ExecAsync(conn, tx, @"insert into modular.history (build_id, gw_log_id, kind, title, detail, status_from, status_to, actor_name, actor_gw_id, at)
                values (@build, @gwid, @kind, @title, @detail, @from, @to, @actor, @actorId, @written::timestamptz)
                on conflict (gw_log_id) do update set build_id = excluded.build_id, kind = excluded.kind, title = excluded.title, detail = excluded.detail,
                  status_from = excluded.status_from, status_to = excluded.status_to, actor_name = excluded.actor_name, at = excluded.at", ct,
                ("build", buildId), ("gwid", l.Id), ("kind", h.Kind), ("title", h.Title), ("detail", (object?)h.Detail ?? DBNull.Value),
                ("from", (object?)h.From ?? DBNull.Value), ("to", (object?)h.To ?? DBNull.Value),
                ("actor", (object?)UserName(snap.Users, l.ActorId) ?? DBNull.Value), ("actorId", (object?)l.ActorId ?? DBNull.Value),
                ("written", (object?)Kst(l.At) ?? DBNull.Value));
        }

        // 6) 첨부 파일 정보 (본체는 그룹웨어 서버에 있다. stored_path는 복사 작업이 채운다)
        foreach (var f in snap.Files)
        {
            if (!buildIds.TryGetValue(f.DocId, out var buildId)) continue;
            long? activityId = f.ActivityId is { } ga && activityIds.TryGetValue(ga, out var aid) ? aid : null;
            await ExecAsync(conn, tx, @"insert into modular.file (build_id, activity_id, source, gw_attach_id, name, ext, size_bytes, gw_path, gw_host, created_at)
                values (@build, @activity, @source, @gwid, @name, @ext, @size, @path, @host, @written::timestamptz)
                on conflict (source, gw_attach_id) do update set build_id = excluded.build_id, activity_id = excluded.activity_id, name = excluded.name,
                  ext = excluded.ext, size_bytes = excluded.size_bytes, gw_path = excluded.gw_path, gw_host = excluded.gw_host, created_at = excluded.created_at", ct,
                ("build", buildId), ("activity", (object?)activityId ?? DBNull.Value), ("source", f.Source), ("gwid", f.AttachId),
                ("name", (object?)f.Name ?? DBNull.Value), ("ext", (object?)f.Ext ?? DBNull.Value), ("size", (object?)f.Size ?? DBNull.Value),
                ("path", (object?)f.Path ?? DBNull.Value), ("host", (object?)f.Host ?? DBNull.Value), ("written", (object?)Kst(f.At) ?? DBNull.Value));
        }
        await ExecAsync(conn, tx, @"delete from modular.file m where m.gw_attach_id is not null and m.build_id = any(@scope)
              and not exists (select 1 from unnest(@src, @ids) as s(src, id) where s.src = m.source and s.id = m.gw_attach_id)", ct,
            ("scope", scope), ("src", snap.Files.Select(f => f.Source).ToArray()), ("ids", snap.Files.Select(f => f.AttachId).ToArray()));

        await ExecAsync(conn, tx, @"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', 'modular', @w, now())
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", ct,
            ("w", marker.Text));

        await tx.CommitAsync(ct);
        return new WriteResult(created, applied, overwritten, managed, deleted);
    }

    // ── 두 벌 관리: 문서 하나 병합 ─────────────────────────────────────────
    private sealed record MergeResult(long BuildId, bool Created, bool Managed, int Applied, int Overwritten,
        List<string> Keys, string? StatusFrom = null, string? StatusTo = null);

    /// <summary>그룹웨어 문서 → 비교용 값(ModularFieldSet 키)</summary>
    private static Dictionary<string, string?> GwValues(Doc d)
    {
        string? DateKey(string cid) { var raw = DateRaw(d, cid); return ModularFieldSet.Date(ParseDate(raw)) ?? ModularFieldSet.Text(raw); }
        var v = new Dictionary<string, string?>
        {
            ["year"] = ModularFieldSet.Int(ParseInt(Str(d, "_7cz5srcqa"))),
            ["project_code"] = ModularFieldSet.Text(Str(d, "_zzv3du17w")),
            ["project_name"] = ModularFieldSet.Text(Str(d, "_j5ioejkql")),
            ["status_id"] = ModularFieldSet.Int(d.StatusId),
            ["install_start"] = DateKey("_stqqsx447"),
            ["commissioning_start"] = DateKey("_3qfe7pbl3"),
            ["external_inspection"] = Str(d, "_g91atexaz"),
            ["ship_due"] = ModularFieldSet.Date(ParseDate(DateRaw(d, "_1opepamz0"))),
            ["ship_month"] = Str(d, "_r3etmju2o"),
            ["origin_tool_receipt"] = Str(d, "_18qsghkdw"),
            ["note"] = Str(d, "_disj88h9s"),
            ["customer"] = Str(d, "_ij4auyj7h"),
            ["plant"] = Str(d, "_tbhkmxoig"),
            ["process"] = Str(d, "_uprsdqxub"),
            ["process_detail"] = Str(d, "_ls94wtdm7"),
            ["robot_count"] = Str(d, "_o9e6bm4on"),
            ["category_code"] = ModularFieldSet.Int(Long(d, "_d8w4r5aig")),
            ["region_code"] = ModularFieldSet.Int(Long(d, "_6ssce5d3v")),
            ["main_issue"] = ModularFieldSet.Text(d.Values.TryGetValue("_zi5k4oa1v", out var issue) ? issue.Text ?? issue.Str : null),
        };
        foreach (var (cid, listKey) in OptionLists.Where(x => x.Value is not ("category" or "region")))
            v["opt:" + listKey] = d.Values.TryGetValue(cid, out var o) ? ModularFieldSet.Codes((o.Multi ?? (o.Long is { } one ? [one] : [])).Select(c => (int)c)) : null;
        foreach (var (cid, role) in MemberRoles)
            v["member:" + role] = ModularFieldSet.Text(Str(d, cid));
        return v;
    }

    private static async Task<MergeResult> MergeDocAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Doc d, Dictionary<long, string> users, CancellationToken ct)
    {
        var gw = GwValues(d);
        var gwJson = JsonSerializer.Serialize(gw);
        long? buildId = null;
        var isManaged = false;
        await using (var find = new NpgsqlCommand("select id, managed_in_dyone from modular.build where gw_doc_id = @g", conn, tx))
        {
            find.Parameters.AddWithValue("g", d.Id);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { buildId = r.GetInt64(0); isManaged = r.GetBoolean(1); }
        }

        // 처음 보는 문서: 그룹웨어 값으로 만든다
        if (buildId is null)
        {
            var id = await UpsertBuildAsync(conn, tx, d, users, ct);
            foreach (var f in ModularFieldSet.Fields.Where(f => f.Kind is ModularFieldSet.Kind.Option or ModularFieldSet.Kind.Member))
                await ApplyAsync(conn, tx, id, f.Key, ModularFieldSet.Get(gw, f.Key), ct);
            await SaveSnapshotAsync(conn, tx, id, d, gwJson, ct);
            return new MergeResult(id, true, false, 0, 0, []);
        }

        var bid = buildId.Value;
        Dictionary<string, string?>? before = null;
        await using (var snapCmd = new NpgsqlCommand("select \"values\"::text from modular.build_gw where build_id = @b", conn, tx))
        {
            snapCmd.Parameters.AddWithValue("b", bid);
            if (await snapCmd.ExecuteScalarAsync(ct) is string json)
                before = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        }

        int applied = 0, over = 0;
        var keys = new List<string>();
        string? statusFrom = null, statusTo = null;
        // 사본이 없으면(두 벌 관리 이전에 들어온 건) 지금 그룹웨어 값을 사본으로 삼고 반영할 것은 없다.
        if (before is not null && !isManaged)
        {
            var changed = ModularFieldSet.Changed(before, gw);
            if (changed.Count > 0)
            {
                var current = await LoadDyOneAsync(conn, tx, bid, ct);
                var gwUpdatedBy = UserName(users, Long(d, "updater"));
                foreach (var key in changed)
                {
                    var cur = ModularFieldSet.Get(current, key);
                    var next = ModularFieldSet.Get(gw, key);
                    if (cur == next) continue;     // 이미 같은 값(양쪽에서 같게 고침)
                    // DY ONE 값이 지난번 그룹웨어 값과 달랐다 = DY ONE에서 고친 칸 → 덮이는 값을 남긴다
                    var dyEdited = cur != ModularFieldSet.Get(before, key);
                    await ApplyAsync(conn, tx, bid, key, next, ct);
                    await ExecAsync(conn, tx, @"insert into modular.field_change (build_id, field, before_value, after_value, overwritten, source, by_name, at)
                        values (@b, @f, @before, @after, @over, 'groupware', @by, @at::timestamptz)", ct,
                        ("b", bid), ("f", key), ("before", (object?)cur ?? DBNull.Value), ("after", (object?)next ?? DBNull.Value),
                        ("over", dyEdited ? (object?)cur ?? DBNull.Value : DBNull.Value), ("by", (object?)gwUpdatedBy ?? DBNull.Value),
                        ("at", (object?)Kst(DateOf(d, "update_date") ?? d.Updated) ?? DateTimeOffset.UtcNow));
                    applied++;
                    if (dyEdited) over++;
                    keys.Add(key);
                    if (key == "status_id") { statusFrom = cur; statusTo = next; }
                }
                if (applied > 0)
                    await ExecAsync(conn, tx, "update modular.build set updated_by_name = @by, updated_at = @at::timestamptz where id = @b", ct,
                        ("b", bid), ("by", (object?)gwUpdatedBy ?? DBNull.Value), ("at", (object?)Kst(DateOf(d, "update_date") ?? d.Updated) ?? DBNull.Value));
            }
        }

        // 비교 대상이 아닌 그룹웨어 정보는 항상 최신으로(등록자·첨부 수·옛 필드), 삭제 표시는 해제
        var legacy = d.Values.Where(kv => !KnownKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => (object?)(
            kv.Value.Multi is { } m ? m : kv.Value.Str ?? (object?)kv.Value.Long ?? kv.Value.Dbl ?? (object?)kv.Value.Date ?? kv.Value.Text));
        await using (var meta = new NpgsqlCommand(@"update modular.build set created_by_name = @cBy, created_by_gw_id = @cById, created_at = @cAt::timestamptz,
              attachment_count = @files, legacy = @legacy, gw_deleted_at = null, synced_at = now() where id = @b", conn, tx))
        {
            var creator = Long(d, "creator");
            meta.Parameters.AddWithValue("b", bid);
            meta.Parameters.AddWithValue("cBy", (object?)UserName(users, creator) ?? DBNull.Value);
            meta.Parameters.AddWithValue("cById", (object?)creator ?? DBNull.Value);
            meta.Parameters.AddWithValue("cAt", (object?)Kst(DateOf(d, "create_date") ?? d.Created) ?? DBNull.Value);
            meta.Parameters.AddWithValue("files", d.Values.TryGetValue("_e9l4te74o", out var files) ? (files.Multi?.Length ?? (files.Long is null ? 0 : 1)) : 0);
            meta.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Jsonb) { Value = legacy.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(legacy) });
            await meta.ExecuteNonQueryAsync(ct);
        }
        await SaveSnapshotAsync(conn, tx, bid, d, gwJson, ct);
        return new MergeResult(bid, false, isManaged, applied, over, keys, statusFrom, statusTo);
    }

    private static async Task SaveSnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long buildId, Doc d, string json, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"insert into modular.build_gw (build_id, gw_doc_id, ""values"", gw_updated_at, synced_at)
            values (@b, @g, @v, @u::timestamptz, now())
            on conflict (build_id) do update set gw_doc_id = excluded.gw_doc_id, ""values"" = excluded.""values"", gw_updated_at = excluded.gw_updated_at, synced_at = now()", conn, tx);
        cmd.Parameters.AddWithValue("b", buildId);
        cmd.Parameters.AddWithValue("g", d.Id);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("u", (object?)Kst(DateOf(d, "update_date") ?? d.Updated) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>지금의 DY ONE 값(비교용)</summary>
    private static async Task<Dictionary<string, string?>> LoadDyOneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long buildId, CancellationToken ct)
    {
        var b = new ModularBuild { Id = buildId };
        await using (var cmd = new NpgsqlCommand(@"select year, project_code, project_name, status_id, install_start, install_start_raw, commissioning_start,
              commissioning_start_raw, external_inspection, ship_due, ship_month, origin_tool_receipt, note, customer, plant, process, process_detail,
              robot_count, robot_count_raw, category_code, region_code, main_issue from modular.build where id = @b", conn, tx))
        {
            cmd.Parameters.AddWithValue("b", buildId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateOnly? D(int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
            b.Year = r.IsDBNull(0) ? null : r.GetInt32(0);
            b.ProjectCode = S(1) ?? ""; b.ProjectName = S(2) ?? "";
            b.StatusId = r.IsDBNull(3) ? null : r.GetInt64(3);
            b.InstallStart = D(4); b.InstallStartRaw = S(5); b.CommissioningStart = D(6); b.CommissioningStartRaw = S(7);
            b.ExternalInspection = S(8); b.ShipDue = D(9); b.ShipMonth = S(10); b.OriginToolReceipt = S(11); b.Note = S(12);
            b.Customer = S(13); b.Plant = S(14); b.Process = S(15); b.ProcessDetail = S(16);
            b.RobotCount = r.IsDBNull(17) ? null : r.GetInt32(17); b.RobotCountRaw = S(18);
            b.CategoryCode = r.IsDBNull(19) ? null : r.GetInt32(19); b.RegionCode = r.IsDBNull(20) ? null : r.GetInt32(20);
            b.MainIssue = S(21);
        }
        var options = new List<ModularBuildOption>();
        await using (var cmd = new NpgsqlCommand("select list_key, code from modular.build_option where build_id = @b", conn, tx))
        {
            cmd.Parameters.AddWithValue("b", buildId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) options.Add(new ModularBuildOption { BuildId = buildId, ListKey = r.GetString(0), Code = r.GetInt32(1) });
        }
        var members = new List<ModularBuildMember>();
        await using (var cmd = new NpgsqlCommand("select role, name_raw from modular.build_member where build_id = @b", conn, tx))
        {
            cmd.Parameters.AddWithValue("b", buildId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) members.Add(new ModularBuildMember { BuildId = buildId, Role = r.GetString(0), NameRaw = r.GetString(1) });
        }
        return ModularFieldSet.FromBuild(b, options, members);
    }

    /// <summary>칸 하나를 DY ONE 값에 쓴다(값 표기는 ModularFieldSet).</summary>
    private static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long buildId, string key, string? value, CancellationToken ct)
    {
        if (key.StartsWith("opt:"))
        {
            var list = key[4..];
            await ExecAsync(conn, tx, "delete from modular.build_option where build_id = @b and list_key = @l", ct, ("b", buildId), ("l", list));
            foreach (var code in ModularFieldSet.ParseCodes(value))
                await ExecAsync(conn, tx, "insert into modular.build_option (build_id, list_key, code) values (@b, @l, @c) on conflict do nothing", ct,
                    ("b", buildId), ("l", list), ("c", code));
            return;
        }
        if (key.StartsWith("member:"))
        {
            var role = key[7..];
            await ExecAsync(conn, tx, "delete from modular.build_member where build_id = @b and role = @r", ct, ("b", buildId), ("r", role));
            if (value is not null)
                await ExecAsync(conn, tx, @"insert into modular.build_member (build_id, role, name_raw, employee_id)
                    values (@b, @r, @n, (select min(e.id) from org.employee e where e.name = @n having count(*) = 1))", ct,
                    ("b", buildId), ("r", role), ("n", value));
            return;
        }

        object N(object? v) => v ?? DBNull.Value;
        var (sql, args) = key switch
        {
            "year" => ("year = @v", new[] { ("v", N(ModularFieldSet.ParseLong(value) is { } y ? (int)y : null)) }),
            "status_id" => ("status_id = @v", [("v", N(ModularFieldSet.ParseLong(value)))]),
            "category_code" or "region_code" => ($"{key} = @v", [("v", N(ModularFieldSet.ParseLong(value) is { } c ? (int)c : null))]),
            "project_code" or "project_name" => ($"{key} = @v", [("v", (object)(value ?? ""))]),
            "install_start" or "commissioning_start" =>
                ($"{key} = @d, {key}_raw = @raw", [("d", N(ModularFieldSet.ParseDate(value))), ("raw", N(ModularFieldSet.ParseDate(value) is null ? value : value?.Replace("-", "")))]),
            "ship_due" => ("ship_due = @v", [("v", N(ModularFieldSet.ParseDate(value)))]),
            "robot_count" => ("robot_count = @n, robot_count_raw = @v", [("n", N(ModularFieldSet.LeadingInt(value))), ("v", N(value))]),
            _ when ModularFieldSet.ByKey.ContainsKey(key) => ($"{key} = @v", new[] { ("v", N(value)) }),
            _ => throw new ArgumentException($"알 수 없는 칸: {key}"),
        };
        await using var cmd = new NpgsqlCommand($"update modular.build set {sql} where id = @b", conn, tx);
        cmd.Parameters.AddWithValue("b", buildId);
        foreach (var (name, v) in args)
        {
            if (v is DateOnly) cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Date) { Value = v });
            else if (v is DBNull && (name == "d" || (key is "ship_due" && name == "v"))) cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Date) { Value = v });
            else cmd.Parameters.AddWithValue(name, v);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeactivateMissingOptionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<OptionRow> options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            @"update modular.option o set is_active = false
              where not exists (select 1 from unnest(@k, @c) as s(k, c) where s.k = o.list_key and s.c = o.code)", conn, tx);
        cmd.Parameters.AddWithValue("k", options.Select(o => o.ListKey).ToArray());
        cmd.Parameters.AddWithValue("c", options.Select(o => o.Code).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> UpsertBuildAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Doc d, Dictionary<long, string> users, CancellationToken ct)
    {
        var installRaw = DateRaw(d, "_stqqsx447");
        var commRaw = DateRaw(d, "_3qfe7pbl3");
        var robotRaw = Str(d, "_o9e6bm4on");
        var legacy = d.Values.Where(kv => !KnownKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => (object?)(
            kv.Value.Multi is { } m ? m : kv.Value.Str ?? (object?)kv.Value.Long ?? kv.Value.Dbl ?? (object?)kv.Value.Date ?? kv.Value.Text));
        var creator = Long(d, "creator");
        var updater = Long(d, "updater");

        const string sql = @"
            insert into modular.build (gw_doc_id, year, project_code, project_name, status_id,
              install_start, install_start_raw, commissioning_start, commissioning_start_raw, external_inspection,
              ship_due, ship_month, origin_tool_receipt, note, customer, plant, process, process_detail,
              robot_count, robot_count_raw, category_code, region_code, main_issue, attachment_count, legacy,
              created_by_name, created_by_gw_id, created_at, updated_by_name, updated_by_gw_id, updated_at, synced_at, is_deleted)
            values (@gw, @year, @code, @name, @status,
              @inst, @instRaw, @comm, @commRaw, @ext,
              @ship, @shipMonth, @tool, @note, @customer, @plant, @process, @detail,
              @robots, @robotsRaw, @category, @region, @issue, @files, @legacy,
              @cBy, @cById, @cAt, @uBy, @uById, @uAt, now(), false)
            on conflict (gw_doc_id) do update set
              year = excluded.year, project_code = excluded.project_code, project_name = excluded.project_name, status_id = excluded.status_id,
              install_start = excluded.install_start, install_start_raw = excluded.install_start_raw,
              commissioning_start = excluded.commissioning_start, commissioning_start_raw = excluded.commissioning_start_raw,
              external_inspection = excluded.external_inspection, ship_due = excluded.ship_due, ship_month = excluded.ship_month,
              origin_tool_receipt = excluded.origin_tool_receipt, note = excluded.note, customer = excluded.customer, plant = excluded.plant,
              process = excluded.process, process_detail = excluded.process_detail, robot_count = excluded.robot_count,
              robot_count_raw = excluded.robot_count_raw, category_code = excluded.category_code, region_code = excluded.region_code,
              main_issue = excluded.main_issue, attachment_count = excluded.attachment_count, legacy = excluded.legacy,
              created_by_name = excluded.created_by_name, created_by_gw_id = excluded.created_by_gw_id, created_at = excluded.created_at,
              updated_by_name = excluded.updated_by_name, updated_by_gw_id = excluded.updated_by_gw_id, updated_at = excluded.updated_at,
              synced_at = now(), is_deleted = false
            returning id";

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var p = cmd.Parameters;
        p.AddWithValue("gw", d.Id);
        p.AddWithValue("year", (object?)ParseInt(Str(d, "_7cz5srcqa")) ?? DBNull.Value);
        p.AddWithValue("code", (Str(d, "_zzv3du17w") ?? "").Trim());
        p.AddWithValue("name", (Str(d, "_j5ioejkql") ?? "").Trim());
        p.AddWithValue("status", (object?)d.StatusId ?? DBNull.Value);
        p.Add(new NpgsqlParameter("inst", NpgsqlDbType.Date) { Value = (object?)ParseDate(installRaw) ?? DBNull.Value });
        p.AddWithValue("instRaw", (object?)installRaw ?? DBNull.Value);
        p.Add(new NpgsqlParameter("comm", NpgsqlDbType.Date) { Value = (object?)ParseDate(commRaw) ?? DBNull.Value });
        p.AddWithValue("commRaw", (object?)commRaw ?? DBNull.Value);
        p.AddWithValue("ext", (object?)Str(d, "_g91atexaz") ?? DBNull.Value);
        p.Add(new NpgsqlParameter("ship", NpgsqlDbType.Date) { Value = (object?)ParseDate(DateRaw(d, "_1opepamz0")) ?? DBNull.Value });
        p.AddWithValue("shipMonth", (object?)Str(d, "_r3etmju2o") ?? DBNull.Value);
        p.AddWithValue("tool", (object?)Str(d, "_18qsghkdw") ?? DBNull.Value);
        p.AddWithValue("note", (object?)Str(d, "_disj88h9s") ?? DBNull.Value);
        p.AddWithValue("customer", (object?)Str(d, "_ij4auyj7h") ?? DBNull.Value);
        p.AddWithValue("plant", (object?)Str(d, "_tbhkmxoig") ?? DBNull.Value);
        p.AddWithValue("process", (object?)Str(d, "_uprsdqxub") ?? DBNull.Value);
        p.AddWithValue("detail", (object?)Str(d, "_ls94wtdm7") ?? DBNull.Value);
        p.AddWithValue("robots", (object?)ParseInt(robotRaw) ?? DBNull.Value);
        p.AddWithValue("robotsRaw", (object?)robotRaw ?? DBNull.Value);
        p.AddWithValue("category", (object?)(int?)Long(d, "_d8w4r5aig") ?? DBNull.Value);
        p.AddWithValue("region", (object?)(int?)Long(d, "_6ssce5d3v") ?? DBNull.Value);
        p.AddWithValue("issue", (object?)(d.Values.TryGetValue("_zi5k4oa1v", out var issue) ? issue.Text ?? issue.Str : null) ?? DBNull.Value);
        p.AddWithValue("files", d.Values.TryGetValue("_e9l4te74o", out var files) ? (files.Multi?.Length ?? (files.Long is null ? 0 : 1)) : 0);
        p.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Jsonb) { Value = legacy.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(legacy) });
        p.AddWithValue("cBy", (object?)UserName(users, creator) ?? DBNull.Value);
        p.AddWithValue("cById", (object?)creator ?? DBNull.Value);
        p.AddWithValue("cAt", (object?)Kst(DateOf(d, "create_date") ?? d.Created) ?? DBNull.Value);
        p.AddWithValue("uBy", (object?)UserName(users, updater) ?? DBNull.Value);
        p.AddWithValue("uById", (object?)updater ?? DBNull.Value);
        p.AddWithValue("uAt", (object?)Kst(DateOf(d, "update_date") ?? d.Updated) ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ── 변경 이력 메시지 해석 ───────────────────────────────────────────────
    // 그룹웨어는 '[머리] 내용' 형태로 남기고, 머리에 ${resource('키')} 번역 키를 쓴다.
    // 예) [${resource('task.status')} ${resource('task.action.change')}] 4. 시운전 -> 5. 외부검수
    //     [출하 월 ${resource('task.action.edit')}] 미지정 -> 26년 9월   (여러 필드를 한 번에 바꾸면 줄마다 하나씩)
    private static readonly Dictionary<string, string> Resources = new()
    {
        ["task.status"] = "상태",
        ["task.action.change"] = "변경",
        ["task.action.edit"] = "변경",
        ["applet.log.added.doc"] = "등록",
        ["applet.log.added.doc.bymappingcopy"] = "등록(다른 앱에서 복사)",
        ["applet.log.added.activity"] = "활동기록 등록",
        ["applet.log.edited.activity"] = "활동기록 수정",
        ["applet.log.deleted.activity"] = "활동기록 삭제",
        ["task.log.added.comment"] = "댓글 등록",
        ["task.log.edited.comment"] = "댓글 수정",
        ["task.log.removed.comment"] = "댓글 삭제",
    };

    private sealed record ParsedLog(string Kind, string Title, string? Detail, string? From, string? To);

    private static string Translate(string text) =>
        ResourceKey().Replace(text, m => Resources.TryGetValue(m.Groups[1].Value, out var ko) ? ko : m.Groups[1].Value);

    private static ParsedLog ParseLog(string message)
    {
        var raw = message ?? "";
        var m = LogHead().Match(raw);
        var headRaw = m.Success ? m.Groups[1].Value : "";
        var head = MultiSpace().Replace(Translate(headRaw), " ").Trim();
        var body = (m.Success ? raw[m.Length..] : raw).Trim();

        if (headRaw.Contains("task.status"))
        {
            var parts = body.Split("->", 2);
            string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : MultiSpace().Replace(v, " ").Trim();
            return new ParsedLog("status", "상태 변경", Clean(body), Clean(parts[0]), parts.Length > 1 ? Clean(parts[1]) : null);
        }

        var kind = headRaw.Contains("added.doc") ? "created"
            : headRaw.Contains("activity") ? "activity"
            : headRaw.Contains("comment") ? "comment"
            : headRaw.Contains("task.action.edit") ? "edit" : "etc";
        // 여러 필드를 한 번에 바꾼 경우: 줄마다 '[필드 변경] 전 -> 후' → '필드: 전 → 후'
        var lines = Translate(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => FieldLine().Replace(l.Trim(), "$1: ")).Select(l => MultiSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        var detail = kind == "edit" ? string.Join('\n', lines)
            : body.Length == 0 ? null : HtmlToText(body);
        if (detail is { Length: > 1000 }) detail = detail[..1000] + "…";
        var title = kind == "edit" ? (lines.Count > 1 ? "정보 변경" : head) : head;
        return new ParsedLog(kind, title.Length == 0 ? "변경" : title, detail, null, null);
    }

    // ── 값 변환 도우미 ─────────────────────────────────────────────────────
    private static string? Str(Doc d, string key) =>
        d.Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.Str ?? v.Text) ? (v.Str ?? v.Text)!.Trim() : null;

    private static long? Long(Doc d, string key) => d.Values.TryGetValue(key, out var v) ? v.Long : null;

    private static DateTime? DateOf(Doc d, string key) => d.Values.TryGetValue(key, out var v) ? v.Date : null;

    /// <summary>날짜 필드 원문. 그룹웨어는 date 필드를 'YYYYMMDD' 문자열로, datetime 필드를 date_value로 저장한다.</summary>
    private static string? DateRaw(Doc d, string key)
    {
        if (!d.Values.TryGetValue(key, out var v)) return null;
        if (v.Date is { } dt) return dt.ToString("yyyyMMdd");
        return string.IsNullOrWhiteSpace(v.Str) ? null : v.Str.Trim();
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string([.. raw.Where(char.IsAsciiDigit)]);
        return digits.Length == 8 && DateOnly.TryParseExact(digits, "yyyyMMdd", out var d) ? d : null;
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = LeadingInt().Match(raw.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static string? UserName(Dictionary<long, string> users, long? id) => id is { } v && users.TryGetValue(v, out var n) ? n : null;

    /// <summary>그룹웨어 timestamp(without time zone)는 한국 시각이다.</summary>
    private static DateTimeOffset? Kst(DateTime? local) =>
        local is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Unspecified), TimeSpan.FromHours(9)).ToUniversalTime() : null;

    /// <summary>활동 글 HTML → 화면용 글자. 줄바꿈은 살리고 태그·스타일은 버린다.</summary>
    private static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = BlockTags().Replace(html, "\n");
        s = ImgTag().Replace(s, "[이미지]");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = string.Join('\n', s.Split('\n').Select(l => MultiSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0));
        return s.Length == 0 ? null : s;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string name, object value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string name, object value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TryRecordErrorAsync(string writeConn, string message, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into sync.error (source, entity, source_pk, message, created_at) values ('groupware', 'modular', null, @m, now())", conn);
            cmd.Parameters.AddWithValue("m", message.Length > 2000 ? message[..2000] : message);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "sync.error 기록 실패");
        }
    }

    [GeneratedRegex(@"\s+")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"^(\d{1,4})")] private static partial Regex LeadingInt();
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d|/tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BlockTags();
    [GeneratedRegex(@"<\s*img\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ImgTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"\$\{resource\('([^']+)'\)\}")] private static partial Regex ResourceKey();
    [GeneratedRegex(@"^\[([^\]]*)\]")] private static partial Regex LogHead();
    [GeneratedRegex(@"^\[(.+?)\s*변경\]\s*")] private static partial Regex FieldLine();
    [GeneratedRegex(@"log:(\d+)$")] private static partial Regex MarkerText();
}
