using System.Text.Json;
using DyOne.Api.Platform;
using DyOne.Data;
using DyOne.Data.Entities;
using DyOne.Data.Works;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Works;

/// <summary>
/// 그룹웨어 WORKS 앱(앱 번호 기준) 화면 API — works.* (첫 대상 281 ES 이슈사항 보고).
///
/// 두 벌 관리: works.doc = DY ONE 값, works.doc_gw = 마지막 그룹웨어 값. Worker(GwWorksSyncJob)는 그룹웨어에서 바뀐 칸만 DY ONE 값에 덮어쓴다.
/// 쓰기: Works:EditEnabled=true 이고 ConnectionStrings:DyOneWrite 가 있을 때만 켜진다(그룹웨어에서 온 문서 포함).
///   고칠 수 있는 칸은 works.field.editable(글자·날짜·숫자·선택) 뿐이다. 사용자·참조 문서·파일 칸은 그룹웨어 값 그대로.
///   DY ONE 변경은 그룹웨어로 보내지 않는다. 칸별 변경은 works.field_change(source = dyone)에 남는다.
/// </summary>
public static class WorksModule
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public record StatusDto(long Id, string Name, int Sort, string? Color, bool IsEnd, bool IsBegin);
    public record TransitionDto(long Id, long From, long To, string Action, int Sort);
    public record OptionDto(long Code, string Label, bool IsDefault);
    public record FieldDto(string Cid, string Label, string FieldType, string? ValueType, bool Required, bool Editable,
        IReadOnlyList<OptionDto>? Options, JsonElement? Props);
    public record DocListItem(long Id, bool FromGroupware, StatusDto? Status, Dictionary<string, string?> Display,
        bool ManagedInDyone, bool GwDeleted, DateTimeOffset? CreatedAt, string? CreatedBy);
    public record CommentDto(long Id, string? Body, string? By, DateTimeOffset? At);
    public record FileDto(long Id, string Source, string? Name, string? Ext, long? Size, bool Stored, DateTimeOffset? At);
    public record ActivityDto(long Id, string? Body, string? By, DateTimeOffset? At, IReadOnlyList<CommentDto> Comments, IReadOnlyList<FileDto> Files);
    public record HistoryDto(string Source, string Kind, string? Title, string? Detail, string? From, string? To, string? By, DateTimeOffset? At);
    public record DifferenceDto(string Key, string Label, string DyOne, string Groupware);

    /// <summary>입력·수정: 필드 cid → 값(글자). 선택은 선택지 코드, 복수 선택은 '1,2', 날짜 yyyy-MM-dd, 일시 yyyy-MM-dd HH:mm</summary>
    public record DocInput(Dictionary<string, string?> Values);
    public record TransitionInput(long TransitionId, string? Note);

    public static void MapWorks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/works").WithTags("Works");

        g.MapGet("/apps/{applet:long}/meta", async (long applet, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var a = await db.WorksApps.FirstOrDefaultAsync(x => x.AppletId == applet, ct);
            if (a is null) return Results.NotFound();
            var fields = await db.WorksFields.Where(f => f.AppletId == applet).OrderBy(f => f.Seq).ToListAsync(ct);
            var statuses = await db.WorksStatuses.Where(s => s.AppletId == applet).OrderBy(s => s.Sort).ToListAsync(ct);
            var transitions = await db.WorksTransitions.Where(t => t.AppletId == applet).OrderBy(t => t.Sort).ToListAsync(ct);
            var entity = $"works:{applet}";
            var synced = await db.SyncStates.Where(s => s.Source == "groupware" && s.Entity == entity).Select(s => (DateTimeOffset?)s.UpdatedAt).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                appletId = a.AppletId,
                a.Name,
                layout = Parse(a.Layout),
                viewColumns = Parse(a.ViewColumns),
                fields = fields.Select(ToDto),
                statuses = statuses.Select(ToDto),
                transitions = transitions.Select(t => new TransitionDto(t.Id, t.FromStatus, t.ToStatus, t.ActionName, t.Sort)),
                editEnabled = WriteConnection(config) is not null,
                syncedAt = synced,
            });
        });

        // 목록: statusId=0이면 상태 없는 문서. q는 표시 글자 전체에서 찾는다. projectCode는 프로젝트(285) 참조 칸에서 찾는다.
        g.MapGet("/apps/{applet:long}/docs", async (long applet, long? statusId, string? q, string? projectCode, int? page, int? pageSize,
            DyOneDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 300);
            var current = Math.Max(page ?? 1, 1);
            var where = "d.applet_id = @a and not d.is_deleted";
            var args = new List<(string, object)> { ("a", applet) };
            if (!string.IsNullOrWhiteSpace(q)) { where += " and d.display::text ilike @q"; args.Add(("q", $"%{q.Trim()}%")); }
            if (!string.IsNullOrWhiteSpace(projectCode))
            {
                where += @" and exists (select 1 from works.field f where f.applet_id = d.applet_id and f.props->>'integrationAppletId' = '285'
                            and d.display->>f.cid ilike @code)";
                args.Add(("code", $"%{projectCode.Trim()}%"));
            }
            var statusWhere = statusId is null ? "" : statusId == 0 ? " and d.status_id is null" : " and d.status_id = @s";
            if (statusId is > 0) args.Add(("s", statusId.Value));

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            var counts = new List<object>();
            await using (var cmd = Command(conn, $"select d.status_id, count(*) from works.doc d where {where} group by 1", args))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) counts.Add(new { statusId = r.IsDBNull(0) ? 0 : r.GetInt64(0), count = r.GetInt64(1) });
            var rows = new List<WorksDoc>();
            long total;
            await using (var cmd = Command(conn, $"select count(*) from works.doc d where {where}{statusWhere}", args))
                total = (long)(await cmd.ExecuteScalarAsync(ct))!;
            await using (var cmd = Command(conn, $@"select d.id, d.gw_doc_id, d.status_id, d.display::text, d.managed_in_dyone, d.gw_deleted_at, d.created_at, d.created_by_name
                from works.doc d where {where}{statusWhere} order by d.created_at desc nulls last, d.id desc limit {size} offset {(current - 1) * size}", args))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    rows.Add(new WorksDoc
                    {
                        Id = r.GetInt64(0), GwDocId = r.IsDBNull(1) ? null : r.GetInt64(1), StatusId = r.IsDBNull(2) ? null : r.GetInt64(2),
                        Display = r.GetString(3), ManagedInDyone = r.GetBoolean(4), GwDeletedAt = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                        CreatedAt = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), CreatedByName = r.IsDBNull(7) ? null : r.GetString(7),
                    });
            var statuses = await db.WorksStatuses.Where(s => s.AppletId == applet).ToDictionaryAsync(s => s.Id, ct);
            var viewCols = (await db.WorksApps.Where(a => a.AppletId == applet).Select(a => a.ViewColumns).FirstOrDefaultAsync(ct)) is { } vc
                ? JsonSerializer.Deserialize<string[]>(vc) ?? [] : [];
            return Results.Ok(new
            {
                total, page = current, pageSize = size, statusCounts = counts,
                items = rows.Select(d => new DocListItem(d.Id, d.GwDocId is not null,
                    d.StatusId is { } s && statuses.TryGetValue(s, out var st) ? ToDto(st) : null,
                    Pick(Dict(d.Display), viewCols), d.ManagedInDyone, d.GwDeletedAt is not null, d.CreatedAt, d.CreatedByName)),
            });
        });

        g.MapGet("/docs/{id:long}", async (long id, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var d = await db.WorksDocs.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            return d is null ? Results.NotFound() : Results.Ok(await DetailAsync(db, d, WriteConnection(config) is not null, ct));
        });

        g.MapPost("/apps/{applet:long}/docs", async (long applet, DocInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } connStr) return EditDisabled();
            var fields = await db.WorksFields.Where(f => f.AppletId == applet).ToListAsync(ct);
            if (fields.Count == 0) return Results.NotFound();
            var (values, display, errors) = Normalize(fields, new Dictionary<string, string?>(), new Dictionary<string, string?>(), input.Values);
            foreach (var f in fields.Where(f => f.Required && f.Editable && ValueOf(values, f.Cid) is null))
                errors[f.Cid] = [$"{f.Label}을(를) 입력하세요."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var begin = await db.WorksStatuses.Where(s => s.AppletId == applet && s.IsBegin).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(connStr);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            long newId;
            await using (var cmd = Command(c, @"insert into works.doc (applet_id, gw_doc_id, status_id, ""values"", display, created_by_name, created_at, updated_by_name, updated_at)
                values (@a, null, @s, @v::jsonb, @d::jsonb, @by, now(), @by, now()) returning id",
                [("a", applet), ("s", (object?)begin ?? DBNull.Value), ("v", JsonSerializer.Serialize(values)), ("d", JsonSerializer.Serialize(display)), ("by", user.Name)], tx))
                newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            await ExecAsync(c, tx, "insert into works.status_history (doc_id, from_status, to_status, action_name, by_name, at) values (@d, null, @t, '등록', @by, now())", ct,
                ("d", newId), ("t", (object?)begin ?? DBNull.Value), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id = newId });
        });

        g.MapPut("/docs/{id:long}", async (long id, DocInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } connStr) return EditDisabled();
            var d = await db.WorksDocs.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            if (d is null) return Results.NotFound();
            var fields = await db.WorksFields.Where(f => f.AppletId == d.AppletId).ToListAsync(ct);
            var before = Dict(d.Values);
            var (values, display, errors) = Normalize(fields, before, Dict(d.Display), input.Values);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var changed = values.Keys.Union(before.Keys).Where(k => ValueOf(before, k) != ValueOf(values, k)).ToList();
            if (changed.Count == 0) return Results.Ok(new { id, changed = 0 });

            var user = http.User.Current();
            await using var c = new NpgsqlConnection(connStr);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await ExecAsync(c, tx, @"update works.doc set ""values"" = @v::jsonb, display = @d::jsonb, updated_by_name = @by, updated_at = now() where id = @id", ct,
                ("id", id), ("v", JsonSerializer.Serialize(values)), ("d", JsonSerializer.Serialize(display)), ("by", user.Name));
            foreach (var key in changed)
                await ExecAsync(c, tx, @"insert into works.field_change (doc_id, field, before_value, after_value, source, by_name, at)
                    values (@d, @f, @b, @a, 'dyone', @by, now())", ct,
                    ("d", id), ("f", key), ("b", (object?)ValueOf(before, key) ?? DBNull.Value), ("a", (object?)ValueOf(values, key) ?? DBNull.Value), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, changed = changed.Count });
        });

        g.MapPost("/docs/{id:long}/transition", async (long id, TransitionInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } connStr) return EditDisabled();
            var d = await db.WorksDocs.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            if (d is null) return Results.NotFound();
            var t = await db.WorksTransitions.FirstOrDefaultAsync(x => x.Id == input.TransitionId && x.AppletId == d.AppletId, ct);
            if (t is null || t.FromStatus != d.StatusId)
                return Results.Problem("현재 상태에서 할 수 없는 전환입니다.", statusCode: StatusCodes.Status409Conflict);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(connStr);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            // 다른 사람이 먼저 상태를 바꿨으면 반영하지 않는다(현재 상태가 전환 출발 상태일 때만)
            if (await ExecAsync(c, tx, "update works.doc set status_id = @to, updated_by_name = @by, updated_at = now() where id = @id and status_id = @from", ct,
                    ("to", t.ToStatus), ("from", t.FromStatus), ("by", user.Name), ("id", id)) == 0)
                return Results.Problem("다른 사용자가 먼저 상태를 바꿨습니다. 새로 고친 뒤 다시 시도하세요.", statusCode: StatusCodes.Status409Conflict);
            await ExecAsync(c, tx, "insert into works.status_history (doc_id, from_status, to_status, action_name, by_name, at, note) values (@d, @f, @t, @a, @by, now(), @n)", ct,
                ("d", id), ("f", t.FromStatus), ("t", t.ToStatus), ("a", t.ActionName), ("by", user.Name), ("n", (object?)input.Note?.Trim() ?? DBNull.Value));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, statusId = t.ToStatus });
        });

        // DY ONE 관리 전환: 이 문서는 그룹웨어 변경을 더 이상 반영하지 않는다(되돌리지 않는다).
        g.MapPost("/docs/{id:long}/manage", async (long id, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } connStr) return EditDisabled();
            var d = await db.WorksDocs.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            if (d is null) return Results.NotFound();
            if (d.GwDocId is null || d.ManagedInDyone)
                return Results.Problem("이미 DY ONE에서 관리하는 문서입니다.", statusCode: StatusCodes.Status409Conflict);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(connStr);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await ExecAsync(c, tx, "update works.doc set managed_in_dyone = true, managed_in_dyone_at = now(), managed_in_dyone_by = @by where id = @id", ct,
                ("id", id), ("by", user.Name));
            await ExecAsync(c, tx, @"insert into works.field_change (doc_id, field, before_value, after_value, source, by_name, at)
                values (@d, 'managed_in_dyone', 'false', 'true', 'dyone', @by, now())", ct, ("d", id), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, managedInDyone = true });
        });
    }

    // ── 상세 ───────────────────────────────────────────────────────────────
    private static async Task<object> DetailAsync(DyOneDbContext db, WorksDoc d, bool editEnabled, CancellationToken ct)
    {
        var fields = await db.WorksFields.Where(f => f.AppletId == d.AppletId).OrderBy(f => f.Seq).ToListAsync(ct);
        var statuses = await db.WorksStatuses.Where(s => s.AppletId == d.AppletId).ToDictionaryAsync(s => s.Id, ct);
        var transitions = await db.WorksTransitions.Where(t => t.AppletId == d.AppletId && t.FromStatus == d.StatusId).OrderBy(t => t.Sort).ToListAsync(ct);
        var activities = await db.WorksActivities.Where(a => a.DocId == d.Id).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToListAsync(ct);
        var activityIds = activities.Select(a => a.Id).ToList();
        var comments = await db.WorksComments.Where(c => activityIds.Contains(c.ActivityId)).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var files = await db.WorksFiles.Where(f => f.DocId == d.Id).OrderBy(f => f.CreatedAt).ToListAsync(ct);
        var gwHistory = await db.WorksHistory.Where(h => h.DocId == d.Id).ToListAsync(ct);
        var dyStatus = await db.WorksStatusHistory.Where(h => h.DocId == d.Id).ToListAsync(ct);
        var changes = await db.WorksFieldChanges.Where(f => f.DocId == d.Id).ToListAsync(ct);
        var snapshot = await db.WorksDocGw.FirstOrDefaultAsync(x => x.DocId == d.Id, ct);

        var byCid = fields.ToDictionary(f => f.Cid);
        var values = Dict(d.Values);
        var display = Dict(d.Display);
        string Label(string key) => key == "status" ? "상태" : byCid.TryGetValue(key, out var f) ? f.Label : key;
        string StatusName(string? v) => long.TryParse(v, out var sid) && statuses.TryGetValue(sid, out var st) ? st.Name : v ?? "(없음)";
        string Show(string key, string? value)
        {
            if (value is null) return "(없음)";
            if (key == "status") return StatusName(value);
            return byCid.TryGetValue(key, out var f) && f.ValueType is "SELECT" or "SELECTS"
                ? WorksValues.OptionDisplay(f.ValueType, value, OptionLabels(f)) ?? value : value;
        }

        // DY ONE 값이 지금 그룹웨어 값과 다른 칸
        var differences = new List<DifferenceDto>();
        if (snapshot is not null)
        {
            var gw = Dict(snapshot.Values);
            var gwDisplay = Dict(snapshot.Display);
            foreach (var key in values.Keys.Union(gw.Keys).Where(k => ValueOf(values, k) != ValueOf(gw, k)))
                differences.Add(new DifferenceDto(key, Label(key), ValueOf(display, key) ?? Show(key, ValueOf(values, key)), ValueOf(gwDisplay, key) ?? "(없음)"));
            if (d.StatusId != snapshot.StatusId)
                differences.Add(new DifferenceDto("status", "상태", StatusName(d.StatusId?.ToString()), StatusName(snapshot.StatusId?.ToString())));
        }

        var changeHistory = changes.Select(f => f.Field == "managed_in_dyone"
                ? new HistoryDto("dyone", "manage", "DY ONE 관리 전환", "이후 그룹웨어 변경은 반영하지 않습니다.", null, null, f.ByName, f.At)
                : f.Source == "dyone"
                    ? new HistoryDto("dyone", "edit", "정보 변경", $"{Label(f.Field)}: {Show(f.Field, f.BeforeValue)} → {Show(f.Field, f.AfterValue)}", null, null, f.ByName, f.At)
                    : f.Overwritten is not null
                        ? new HistoryDto("groupware", "overwrite", "그룹웨어 값으로 덮어씀",
                            $"{Label(f.Field)}: DY ONE {Show(f.Field, f.Overwritten)} → 그룹웨어 {Show(f.Field, f.AfterValue)}", null, null, f.ByName, f.At)
                        : null)
            .OfType<HistoryDto>();

        return new
        {
            id = d.Id,
            appletId = d.AppletId,
            status = d.StatusId is { } s && statuses.TryGetValue(s, out var cur) ? ToDto(cur) : null,
            values,
            display,
            fromGroupware = d.GwDocId is not null,
            managedInDyone = d.ManagedInDyone,
            d.ManagedInDyoneBy,
            gwDeletedAt = d.GwDeletedAt,
            d.CreatedByName, d.CreatedAt, d.UpdatedByName, d.UpdatedAt,
            differences,
            transitions = transitions.Select(t => new TransitionDto(t.Id, t.FromStatus, t.ToStatus, t.ActionName, t.Sort)),
            activities = activities.Select(a => new ActivityDto(a.Id, a.BodyText, a.CreatedByName, a.CreatedAt,
                [.. comments.Where(c => c.ActivityId == a.Id).Select(c => new CommentDto(c.Id, c.Body, c.AuthorName, c.CreatedAt))],
                [.. files.Where(f => f.ActivityId == a.Id).Select(ToFile)])),
            files = files.Select(ToFile),
            history = gwHistory.Select(h => new HistoryDto("groupware", h.Kind, h.Title, h.Detail, h.StatusFrom, h.StatusTo, h.ActorName, h.At))
                .Concat(dyStatus.Select(h => new HistoryDto("dyone", "status", h.ActionName ?? "상태 변경", h.Note,
                    h.FromStatus is { } fs && statuses.TryGetValue(fs, out var fn) ? fn.Name : null,
                    statuses.TryGetValue(h.ToStatus, out var tn) ? tn.Name : h.ToStatus.ToString(), h.ByName, h.At)))
                .Concat(changeHistory)
                .OrderByDescending(h => h.At),
            editable = editEnabled,
            gwUrl = d.GwDocId is not null ? $"https://gw.doolim-yaskawa.com/app/works/applet/{d.AppletId}/home" : null,
        };
    }

    // ── 값 처리 ────────────────────────────────────────────────────────────
    /// <summary>
    /// 입력을 지금 값 위에 얹는다. 고칠 수 있는 칸만 받고, 그 밖의 칸(사용자·참조 문서·파일 등)은 지금 값을 그대로 둔다.
    /// </summary>
    private static (Dictionary<string, string?> Values, Dictionary<string, string?> Display, Dictionary<string, string[]> Errors) Normalize(
        List<WorksField> fields, Dictionary<string, string?> current, Dictionary<string, string?> currentDisplay, Dictionary<string, string?> input)
    {
        var values = new Dictionary<string, string?>(current);
        var display = new Dictionary<string, string?>(currentDisplay);
        var errors = new Dictionary<string, string[]>();
        var byCid = fields.ToDictionary(f => f.Cid);
        foreach (var (cid, raw) in input)
        {
            if (!byCid.TryGetValue(cid, out var f)) { errors[cid] = ["알 수 없는 칸입니다."]; continue; }
            if (!f.Editable) { errors[cid] = [$"{f.Label}은(는) 그룹웨어 값 그대로라 DY ONE에서 고칠 수 없습니다."]; continue; }
            var labels = OptionLabels(f);
            var value = WorksValues.FromInput(f.ValueType, raw, labels.Keys.ToList(), out var error);
            if (error is not null) { errors[cid] = [$"{f.Label}: {error}"]; continue; }
            values[cid] = value;
            display[cid] = f.ValueType is "SELECT" or "SELECTS" ? WorksValues.OptionDisplay(f.ValueType, value, labels) : value;
        }
        return (values, display, errors);
    }

    private static Dictionary<long, string> OptionLabels(WorksField f) =>
        string.IsNullOrEmpty(f.Options) ? []
            : (JsonSerializer.Deserialize<List<OptionDto>>(f.Options, Json) ?? []).GroupBy(o => o.Code).ToDictionary(g => g.Key, g => g.First().Label);

    private static FieldDto ToDto(WorksField f) => new(f.Cid, f.Label, f.FieldType, f.ValueType, f.Required, f.Editable,
        string.IsNullOrEmpty(f.Options) ? null : JsonSerializer.Deserialize<List<OptionDto>>(f.Options, Json), Parse(f.Props));

    private static StatusDto ToDto(WorksStatus s) => new(s.Id, s.Name, s.Sort, s.Color, s.IsEnd, s.IsBegin);
    private static FileDto ToFile(WorksFile f) => new(f.Id, f.Source, f.Name, f.Ext, f.SizeBytes, f.StoredPath is not null, f.CreatedAt);

    private static Dictionary<string, string?> Dict(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];

    private static string? ValueOf(IReadOnlyDictionary<string, string?> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    private static Dictionary<string, string?> Pick(Dictionary<string, string?> d, IEnumerable<string> keys) =>
        keys.Distinct().Where(k => k != "status").ToDictionary(k => k, k => ValueOf(d, k));

    private static JsonElement? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static string? WriteConnection(IConfiguration config) =>
        config.GetValue<bool>("Works:EditEnabled") && config.GetConnectionString("DyOneWrite") is { Length: > 0 } c ? c : null;

    private static IResult EditDisabled() =>
        Results.Problem("입력 기능이 꺼져 있습니다. 지금은 그룹웨어가 원본입니다.", statusCode: StatusCodes.Status403Forbidden);

    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, IEnumerable<(string, object)> args, NpgsqlTransaction? tx = null)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = Command(c, sql, args, tx);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
