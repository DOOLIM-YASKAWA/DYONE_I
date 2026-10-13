using DyOne.Api.Platform;
using DyOne.Data;
using System.Text.Json;
using DyOne.Data.Entities;
using DyOne.Data.Modular;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Modular;

/// <summary>
/// 모듈라빌드 프로젝트 정보 — 그룹웨어 WORKS applet 376을 DY ONE으로 옮긴 화면의 API.
///
/// 두 벌 관리: modular.build = DY ONE 값(화면·수정 대상), modular.build_gw = 마지막으로 읽은 그룹웨어 값.
///   Worker(GwModularSyncJob)는 그룹웨어에서 바뀐 칸만 DY ONE 값에 덮어쓰고, DY ONE에서 고친 칸은 그룹웨어가 안 바뀌면 그대로 둔다.
///   DY ONE에서의 수정·상태 전환은 그룹웨어로 보내지 않는다. 'DY ONE 관리 전환'한 건은 그룹웨어 변경을 더 이상 받지 않는다.
/// 쓰기: Modular:EditEnabled=true 이고 ConnectionStrings:DyOneWrite 가 있을 때만 켜진다(그룹웨어에서 온 건 포함 모든 건).
///   칸별 변경은 modular.field_change(source = dyone)에 남는다. 상태 전환은 modular.transition(그룹웨어 워크플로 그대로)만 허용한다.
/// </summary>
public static class ModularModule
{
    /// <summary>복수 선택 목록 (list_key). category·region은 단일 선택이라 build 열에 둔다.</summary>
    private static readonly string[] MultiLists = ["install_location", "robot_model", "cable_robot_box", "cable_box_rc", "comm", "applicator"];

    public static readonly Dictionary<string, string> RoleLabels = new()
    {
        ["pm"] = "PM", ["pl"] = "PL", ["control"] = "제어", ["design"] = "설계", ["teach"] = "티쳐",
    };

    public record StatusDto(long Id, string Name, int Sort, string? Color, bool IsEnd, bool IsBegin);
    public record TransitionDto(long Id, long From, long To, string Action, int Sort);
    public record OptionDto(int Code, string Label, bool IsDefault);

    public record BuildListItem(
        long Id, bool FromGroupware, int? Year, StatusDto? Status, string ProjectCode, string ProjectName,
        IReadOnlyList<string> InstallLocations, string? ShipMonth, DateOnly? InstallStart, DateOnly? CommissioningStart,
        string? Category, DateOnly? ShipDue, string? Region, string? RobotCount,
        IReadOnlyList<string> RobotModels, IReadOnlyList<string> Applicators, IReadOnlyList<string> Comms,
        string? Plant, string? Customer, string? Process, string? ProcessDetail, string? ExternalInspection, DateTimeOffset? UpdatedAt,
        bool ManagedInDyone, bool GwDeleted);

    public record MemberDto(string Role, string RoleLabel, string Name, long? EmployeeId);
    public record CommentDto(long Id, string? Body, string? By, DateTimeOffset? At);
    public record FileDto(long Id, string Source, string? Name, string? Ext, long? Size, bool Stored, DateTimeOffset? At);
    public record ActivityDto(long Id, string? Body, string? By, DateTimeOffset? At, IReadOnlyList<CommentDto> Comments, IReadOnlyList<FileDto> Files);
    /// <summary>
    /// 변경 이력 한 줄. Source = groupware · dyone.
    /// Kind = status · edit · created · activity · comment · etc(그룹웨어 변경 이력) · overwrite(그룹웨어 값이 DY ONE 수정을 덮음) · manage(DY ONE 관리 전환)
    /// </summary>
    public record HistoryDto(string Source, string Kind, string? Title, string? Detail, string? From, string? To, string? By, DateTimeOffset? At);
    /// <summary>DY ONE 값이 그룹웨어 값과 다른 칸</summary>
    public record DifferenceDto(string Key, string Label, string DyOne, string Groupware);

    /// <summary>입력·수정 요청. Options = list_key → 선택지 코드 목록.</summary>
    public record BuildInput(
        int? Year, string ProjectCode, string ProjectName,
        DateOnly? InstallStart, DateOnly? CommissioningStart, string? ExternalInspection, DateOnly? ShipDue, string? ShipMonth,
        string? OriginToolReceipt, string? Note, string? Customer, string? Plant, string? Process, string? ProcessDetail,
        string? RobotCount, int? CategoryCode, int? RegionCode, string? MainIssue,
        Dictionary<string, int[]>? Options, Dictionary<string, string>? Members);

    public record TransitionInput(long TransitionId, string? Note);

    public static void MapModular(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/modular").WithTags("Modular");

        g.MapGet("/meta", async (DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var statuses = await db.ModularStatuses.OrderBy(s => s.Sort).ToListAsync(ct);
            var transitions = await db.ModularTransitions.OrderBy(t => t.Sort).ToListAsync(ct);
            var options = await db.ModularOptions.Where(o => o.IsActive).OrderBy(o => o.ListKey).ThenBy(o => o.Sort).ToListAsync(ct);
            var sync = await db.SyncStates.Where(s => s.Source == "groupware" && s.Entity == "modular").Select(s => (DateTimeOffset?)s.UpdatedAt).FirstOrDefaultAsync(ct);
            return new
            {
                statuses = statuses.Select(ToDto),
                transitions = transitions.Select(t => new TransitionDto(t.Id, t.FromStatus, t.ToStatus, t.ActionName, t.Sort)),
                options = options.GroupBy(o => o.ListKey).ToDictionary(x => x.Key, x => x.Select(o => new OptionDto(o.Code, o.Label, o.IsDefault))),
                roles = RoleLabels,
                editEnabled = EditEnabled(config),
                syncedAt = sync,
            };
        });

        g.MapGet("/builds", async (int? year, long? statusId, int? category, string? q, string? projectCode, int? page, int? pageSize,
            DyOneDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 300);
            var current = Math.Max(page ?? 1, 1);
            var query = Filter(db.ModularBuilds.Where(b => !b.IsDeleted), year, statusId, category, q, projectCode);
            var total = await query.CountAsync(ct);
            // 그룹웨어 기본 보기처럼 최근 등록 순(연도 → 등록일)
            var rows = await query.OrderByDescending(b => b.Year).ThenByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
                .Skip((current - 1) * size).Take(size).ToListAsync(ct);
            var years = await db.ModularBuilds.Where(b => !b.IsDeleted && b.Year != null).Select(b => b.Year!.Value).Distinct().OrderByDescending(y => y).ToListAsync(ct);
            var counts = await Filter(db.ModularBuilds.Where(b => !b.IsDeleted), year, null, category, q, projectCode)
                .GroupBy(b => b.StatusId).Select(x => new { statusId = x.Key, count = x.Count() }).ToListAsync(ct);
            return new { total, page = current, pageSize = size, years, statusCounts = counts, items = await ListItemsAsync(db, rows, ct) };
        });

        g.MapGet("/gantt", async (int? year, long? statusId, int? category, string? q, DyOneDbContext db, CancellationToken ct) =>
        {
            var rows = await Filter(db.ModularBuilds.Where(b => !b.IsDeleted && b.InstallStart != null), year, statusId, category, q, null)
                .OrderBy(b => b.InstallStart).ToListAsync(ct);
            var statuses = await db.ModularStatuses.ToDictionaryAsync(s => s.Id, ct);
            // 그룹웨어 간트 보기: 제목 = 프로젝트명, 시작 = 모듈라 설치 시작, 종료 = 출하 예정일
            return rows.Select(b => new
            {
                b.Id, b.ProjectCode, b.ProjectName, start = b.InstallStart, end = b.ShipDue ?? b.CommissioningStart ?? b.InstallStart,
                endIsEstimate = b.ShipDue is null, b.CommissioningStart,
                status = b.StatusId is { } s && statuses.TryGetValue(s, out var st) ? ToDto(st) : null,
            });
        });

        g.MapGet("/builds/{id:long}", async (long id, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var b = await db.ModularBuilds.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            return b is null ? Results.NotFound() : Results.Ok(await DetailAsync(db, b, EditEnabled(config), ct));
        });

        // 프로젝트 화면용: 이 프로젝트 코드의 모듈라 건
        g.MapGet("/by-project/{code}", async (string code, DyOneDbContext db, CancellationToken ct) =>
        {
            var rows = await db.ModularBuilds.Where(b => !b.IsDeleted && b.ProjectCode == code.Trim())
                .OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
            return await ListItemsAsync(db, rows, ct);
        });

        // ── 입력·수정 (DY ONE 값만 바꾼다. 그룹웨어로는 보내지 않는다) ─────────
        g.MapPost("/builds", async (BuildInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } conn) return EditDisabled();
            if (Validate(input) is { } bad) return bad;
            var begin = await db.ModularStatuses.Where(s => s.IsBegin).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            long id;
            await using (var cmd = new NpgsqlCommand(
                $@"insert into modular.build (gw_doc_id, status_id, created_by_name, created_at, updated_by_name, updated_at, {Columns})
                   values (null, @status, @by, now(), @by, now(), {Params}) returning id", c, tx))
            {
                AddFields(cmd, input);
                cmd.Parameters.AddWithValue("status", (object?)begin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("by", user.Name);
                id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await WriteChildrenAsync(c, tx, id, input, ct);
            await ExecAsync(c, tx, "insert into modular.status_history (build_id, from_status, to_status, action_name, by_name, at) values (@b, null, @t, '등록', @by, now())", ct,
                ("b", id), ("t", (object?)begin ?? DBNull.Value), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id });
        });

        g.MapPut("/builds/{id:long}", async (long id, BuildInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } conn) return EditDisabled();
            var current = await db.ModularBuilds.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
            if (current is null) return Results.NotFound();
            if (Validate(input) is { } bad) return bad;
            var before = ModularFieldSet.FromBuild(current,
                await db.ModularBuildOptions.Where(o => o.BuildId == id).ToListAsync(ct),
                await db.ModularBuildMembers.Where(m => m.BuildId == id).ToListAsync(ct));
            var after = InputValues(input, current);
            var changed = ModularFieldSet.Changed(before, after);
            if (changed.Count == 0) return Results.Ok(new { id, changed = 0 });

            var user = http.User.Current();
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand(
                $"update modular.build set ({Columns}, updated_by_name, updated_at) = ({Params}, @by, now()) where id = @id", c, tx))
            {
                AddFields(cmd, input);
                cmd.Parameters.AddWithValue("by", user.Name);
                cmd.Parameters.AddWithValue("id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await WriteChildrenAsync(c, tx, id, input, ct);
            foreach (var key in changed)
                await ExecAsync(c, tx, @"insert into modular.field_change (build_id, field, before_value, after_value, source, by_name, at)
                    values (@b, @f, @before, @after, 'dyone', @by, now())", ct,
                    ("b", id), ("f", key), ("before", (object?)ModularFieldSet.Get(before, key) ?? DBNull.Value),
                    ("after", (object?)ModularFieldSet.Get(after, key) ?? DBNull.Value), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, changed = changed.Count });
        });

        g.MapPost("/builds/{id:long}/transition", async (long id, TransitionInput input, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } conn) return EditDisabled();
            var build = await db.ModularBuilds.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted, ct);
            if (build is null) return Results.NotFound();
            var t = await db.ModularTransitions.FirstOrDefaultAsync(x => x.Id == input.TransitionId, ct);
            if (t is null || t.FromStatus != build.StatusId)
                return Results.Problem("현재 상태에서 할 수 없는 전환입니다.", statusCode: StatusCodes.Status409Conflict);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            // 동시에 다른 사람이 상태를 바꿨으면 반영하지 않는다(현재 상태가 전환 출발 상태일 때만).
            await using (var cmd = new NpgsqlCommand(
                "update modular.build set status_id = @to, updated_by_name = @by, updated_at = now() where id = @id and status_id = @from", c, tx))
            {
                cmd.Parameters.AddWithValue("to", t.ToStatus);
                cmd.Parameters.AddWithValue("from", t.FromStatus);
                cmd.Parameters.AddWithValue("by", user.Name);
                cmd.Parameters.AddWithValue("id", id);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                    return Results.Problem("다른 사용자가 먼저 상태를 바꿨습니다. 새로 고친 뒤 다시 시도하세요.", statusCode: StatusCodes.Status409Conflict);
            }
            await ExecAsync(c, tx, "insert into modular.status_history (build_id, from_status, to_status, action_name, by_name, at, note) values (@b, @f, @t, @a, @by, now(), @n)", ct,
                ("b", id), ("f", t.FromStatus), ("t", t.ToStatus), ("a", t.ActionName), ("by", user.Name), ("n", (object?)input.Note?.Trim() ?? DBNull.Value));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, statusId = t.ToStatus });
        });

        // DY ONE 관리 전환: 이 건은 그룹웨어 변경을 더 이상 반영하지 않는다(되돌리지 않는다).
        g.MapPost("/builds/{id:long}/manage", async (long id, HttpContext http, DyOneDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (WriteConnection(config) is not { } conn) return EditDisabled();
            var build = await db.ModularBuilds.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted, ct);
            if (build is null) return Results.NotFound();
            if (build.GwDocId is null || build.ManagedInDyone)
                return Results.Problem("이미 DY ONE에서 관리하는 건입니다.", statusCode: StatusCodes.Status409Conflict);
            var user = http.User.Current();
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await ExecAsync(c, tx, "update modular.build set managed_in_dyone = true, managed_in_dyone_at = now(), managed_in_dyone_by = @by where id = @b", ct,
                ("b", id), ("by", user.Name));
            await ExecAsync(c, tx, @"insert into modular.field_change (build_id, field, before_value, after_value, source, by_name, at)
                values (@b, 'managed_in_dyone', 'false', 'true', 'dyone', @by, now())", ct, ("b", id), ("by", user.Name));
            await tx.CommitAsync(ct);
            return Results.Ok(new { id, managedInDyone = true });
        });
    }

    // ── 조회 도우미 ─────────────────────────────────────────────────────────
    private static IQueryable<ModularBuild> Filter(IQueryable<ModularBuild> q, int? year, long? statusId, int? category, string? search, string? projectCode)
    {
        if (year is not null) q = q.Where(b => b.Year == year);
        if (statusId is not null) q = q.Where(b => b.StatusId == statusId);
        if (category is not null) q = q.Where(b => b.CategoryCode == category);
        if (!string.IsNullOrWhiteSpace(projectCode)) q = q.Where(b => b.ProjectCode == projectCode.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(b => EF.Functions.ILike(b.ProjectCode, s) || EF.Functions.ILike(b.ProjectName, s)
                || EF.Functions.ILike(b.Customer ?? "", s) || EF.Functions.ILike(b.Plant ?? "", s) || EF.Functions.ILike(b.Process ?? "", s));
        }
        return q;
    }

    private static StatusDto ToDto(ModularStatus s) => new(s.Id, s.Name, s.Sort, s.Color, s.IsEnd, s.IsBegin);

    private static FileDto ToFile(ModularFile f) => new(f.Id, f.Source, f.Name, f.Ext, f.SizeBytes, f.StoredPath is not null, f.CreatedAt);

    private static async Task<List<BuildListItem>> ListItemsAsync(DyOneDbContext db, List<ModularBuild> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var picked = await db.ModularBuildOptions.Where(o => ids.Contains(o.BuildId)).ToListAsync(ct);
        var labels = await OptionLabelsAsync(db, ct);
        var statuses = await db.ModularStatuses.ToDictionaryAsync(s => s.Id, ct);
        List<string> Pick(long id, string list) => [.. picked.Where(p => p.BuildId == id && p.ListKey == list)
            .Select(p => labels.TryGetValue((list, p.Code), out var l) ? l : p.Code.ToString()).Order()];
        string? Single(string list, int? code) => code is { } c ? (labels.TryGetValue((list, c), out var l) ? l : c.ToString()) : null;

        return rows.Select(b => new BuildListItem(
            b.Id, b.GwDocId is not null, b.Year,
            b.StatusId is { } s && statuses.TryGetValue(s, out var st) ? ToDto(st) : null,
            b.ProjectCode, b.ProjectName, Pick(b.Id, "install_location"), b.ShipMonth, b.InstallStart, b.CommissioningStart,
            Single("category", b.CategoryCode), b.ShipDue, Single("region", b.RegionCode),
            b.RobotCount?.ToString() ?? b.RobotCountRaw,
            Pick(b.Id, "robot_model"), Pick(b.Id, "applicator"), Pick(b.Id, "comm"),
            b.Plant, b.Customer, b.Process, b.ProcessDetail, b.ExternalInspection, b.UpdatedAt,
            b.ManagedInDyone, b.GwDeletedAt is not null)).ToList();
    }

    private static async Task<Dictionary<(string, int), string>> OptionLabelsAsync(DyOneDbContext db, CancellationToken ct) =>
        (await db.ModularOptions.ToListAsync(ct)).ToDictionary(o => (o.ListKey, o.Code), o => o.Label);

    private static async Task<object> DetailAsync(DyOneDbContext db, ModularBuild b, bool editEnabled, CancellationToken ct)
    {
        var item = (await ListItemsAsync(db, [b], ct))[0];
        var picked = await db.ModularBuildOptions.Where(o => o.BuildId == b.Id).ToListAsync(ct);
        var members = await db.ModularBuildMembers.Where(m => m.BuildId == b.Id).ToListAsync(ct);
        var activities = await db.ModularActivities.Where(a => a.BuildId == b.Id).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToListAsync(ct);
        var activityIds = activities.Select(a => a.Id).ToList();
        var comments = await db.ModularComments.Where(c => activityIds.Contains(c.ActivityId)).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var files = await db.ModularFiles.Where(f => f.BuildId == b.Id).OrderBy(f => f.CreatedAt).ToListAsync(ct);
        var statuses = await db.ModularStatuses.ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var gwHistory = await db.ModularHistory.Where(h => h.BuildId == b.Id).ToListAsync(ct);
        var dyHistory = await db.ModularStatusHistory.Where(h => h.BuildId == b.Id).ToListAsync(ct);
        var transitions = await db.ModularTransitions.Where(t => t.FromStatus == b.StatusId).OrderBy(t => t.Sort).ToListAsync(ct);
        var fieldChanges = await db.ModularFieldChanges.Where(f => f.BuildId == b.Id).ToListAsync(ct);
        var snapshot = await db.ModularBuildGw.Where(x => x.BuildId == b.Id).Select(x => x.Values).FirstOrDefaultAsync(ct);
        var labels = await OptionLabelsAsync(db, ct);
        string Show(string key, string? value) => Display(key, value, labels, statuses);

        // DY ONE 값이 지금 그룹웨어 값과 다른 칸 (DY ONE에서 고쳤고 그룹웨어는 아직 안 바뀐 칸)
        var differences = new List<DifferenceDto>();
        if (snapshot is not null && JsonSerializer.Deserialize<Dictionary<string, string?>>(snapshot) is { } gwValues)
        {
            var dyValues = ModularFieldSet.FromBuild(b, picked, members);
            differences = [.. ModularFieldSet.Changed(gwValues, dyValues).Select(k => new DifferenceDto(
                k, ModularFieldSet.ByKey[k].Label, Show(k, ModularFieldSet.Get(dyValues, k)), Show(k, ModularFieldSet.Get(gwValues, k))))];
        }

        // 칸별 기록 → 이력: DY ONE 수정, 그룹웨어 값이 DY ONE 수정을 덮은 경우, DY ONE 관리 전환
        // (덮지 않은 그룹웨어 반영은 그룹웨어 변경 이력에 이미 있으므로 따로 보이지 않는다)
        var changeHistory = fieldChanges.Select(f => f.Field == "managed_in_dyone"
                ? new HistoryDto("dyone", "manage", "DY ONE 관리 전환", "이후 그룹웨어 변경은 반영하지 않습니다.", null, null, f.ByName, f.At)
                : f.Source == "dyone"
                    ? new HistoryDto("dyone", "edit", "정보 변경",
                        $"{Label(f.Field)}: {Show(f.Field, f.BeforeValue)} → {Show(f.Field, f.AfterValue)}", null, null, f.ByName, f.At)
                    : f.Overwritten is not null
                        ? new HistoryDto("groupware", "overwrite", "그룹웨어 값으로 덮어씀",
                            $"{Label(f.Field)}: DY ONE {Show(f.Field, f.Overwritten)} → 그룹웨어 {Show(f.Field, f.AfterValue)}", null, null, f.ByName, f.At)
                        : null)
            .OfType<HistoryDto>();
        var project = await db.Projects.Where(p => p.Code == b.ProjectCode).Select(p => new { p.Id, p.Code, p.Name, p.PartnerName }).FirstOrDefaultAsync(ct);

        return new
        {
            item,
            fields = new
            {
                b.Year, b.ProjectCode, b.ProjectName, b.InstallStart, b.InstallStartRaw, b.CommissioningStart, b.CommissioningStartRaw,
                b.ExternalInspection, b.ShipDue, b.ShipMonth, b.OriginToolReceipt, b.Note, b.Customer, b.Plant, b.Process, b.ProcessDetail,
                b.RobotCount, b.RobotCountRaw, b.CategoryCode, b.RegionCode, b.MainIssue, b.AttachmentCount,
                b.CreatedByName, b.CreatedAt, b.UpdatedByName, b.UpdatedAt, b.SyncedAt, b.GwDocId,
            },
            options = MultiLists.ToDictionary(l => l, l => picked.Where(p => p.ListKey == l).Select(p => p.Code).Order().ToArray()),
            members = RoleLabels.Keys.Select(r => members.FirstOrDefault(m => m.Role == r) is { } m
                ? new MemberDto(r, RoleLabels[r], m.NameRaw, m.EmployeeId) : new MemberDto(r, RoleLabels[r], "", null)),
            activities = activities.Select(a => new ActivityDto(a.Id, a.BodyText, a.CreatedByName, a.CreatedAt,
                [.. comments.Where(c => c.ActivityId == a.Id).Select(c => new CommentDto(c.Id, c.Body, c.AuthorName, c.CreatedAt))],
                [.. files.Where(f => f.ActivityId == a.Id).Select(ToFile)])),
            // 참고자료(문서 필드) 첨부 + 활동 글 첨부 전체
            files = files.Select(ToFile),
            history = gwHistory
                .Select(h => new HistoryDto("groupware", h.Kind, h.Title, h.Detail, h.StatusFrom, h.StatusTo, h.ActorName, h.At))
                .Concat(dyHistory.Select(h => new HistoryDto("dyone", "status", h.ActionName ?? "상태 변경", h.Note,
                    h.FromStatus is { } f && statuses.TryGetValue(f, out var fn) ? fn : null,
                    statuses.TryGetValue(h.ToStatus, out var tn) ? tn : h.ToStatus.ToString(), h.ByName, h.At)))
                .Concat(changeHistory)
                .OrderByDescending(h => h.At),
            transitions = transitions.Select(t => new TransitionDto(t.Id, t.FromStatus, t.ToStatus, t.ActionName, t.Sort)),
            project,
            differences,
            fromGroupware = b.GwDocId is not null,
            managedInDyone = b.ManagedInDyone,
            b.ManagedInDyoneAt,
            b.ManagedInDyoneBy,
            gwDeletedAt = b.GwDeletedAt,
            // 그룹웨어에서 온 건도 DY ONE 값은 고칠 수 있다(그룹웨어로는 보내지 않는다).
            editable = editEnabled,
            gwUrl = b.GwDocId is not null ? "https://gw.doolim-yaskawa.com/app/works/applet/376/home" : null,
        };
    }

    // ── 쓰기 도우미 ─────────────────────────────────────────────────────────
    private const string Columns = "year, project_code, project_name, install_start, install_start_raw, commissioning_start, commissioning_start_raw, " +
        "external_inspection, ship_due, ship_month, origin_tool_receipt, note, customer, plant, process, process_detail, robot_count, robot_count_raw, " +
        "category_code, region_code, main_issue";
    private const string Params = "@year, @code, @name, @inst, @instRaw, @comm, @commRaw, @ext, @ship, @shipMonth, @tool, @note, @customer, @plant, @process, @detail, " +
        "@robots, @robotsRaw, @category, @region, @issue";

    private static bool EditEnabled(IConfiguration config) => WriteConnection(config) is not null;

    private static string? WriteConnection(IConfiguration config) =>
        config.GetValue<bool>("Modular:EditEnabled") && config.GetConnectionString("DyOneWrite") is { Length: > 0 } c ? c : null;

    private static IResult EditDisabled() =>
        Results.Problem("모듈라 입력 기능이 꺼져 있습니다. 지금은 그룹웨어가 원본입니다.", statusCode: StatusCodes.Status403Forbidden);

    private static string Label(string key) => ModularFieldSet.ByKey.TryGetValue(key, out var f) ? f.Label : key;

    /// <summary>비교용 값 → 화면 표시(상태 이름, 선택지 이름)</summary>
    private static string Display(string key, string? value, Dictionary<(string, int), string> labels, Dictionary<long, string> statuses)
    {
        if (value is null) return "(없음)";
        string Opt(string list, int code) => labels.TryGetValue((list, code), out var l) ? l : code.ToString();
        return key switch
        {
            "status_id" => ModularFieldSet.ParseLong(value) is { } sid && statuses.TryGetValue(sid, out var n) ? n : value,
            "category_code" => Opt("category", int.Parse(value)),
            "region_code" => Opt("region", int.Parse(value)),
            _ when key.StartsWith("opt:") => string.Join(", ", ModularFieldSet.ParseCodes(value).Select(c => Opt(key[4..], c))),
            _ => value,
        };
    }

    /// <summary>수정 요청 → 비교용 값. 상태는 수정 화면에서 바꾸지 않으므로 지금 값을 쓴다.</summary>
    private static Dictionary<string, string?> InputValues(BuildInput i, ModularBuild current)
    {
        var b = new ModularBuild
        {
            Id = current.Id, Year = i.Year, ProjectCode = i.ProjectCode, ProjectName = i.ProjectName, StatusId = current.StatusId,
            InstallStart = i.InstallStart, CommissioningStart = i.CommissioningStart, ExternalInspection = i.ExternalInspection,
            ShipDue = i.ShipDue, ShipMonth = i.ShipMonth, OriginToolReceipt = i.OriginToolReceipt, Note = i.Note, Customer = i.Customer,
            Plant = i.Plant, Process = i.Process, ProcessDetail = i.ProcessDetail, RobotCountRaw = i.RobotCount,
            CategoryCode = i.CategoryCode, RegionCode = i.RegionCode, MainIssue = i.MainIssue,
        };
        var options = (i.Options ?? []).SelectMany(kv => kv.Value.Select(c => new ModularBuildOption { BuildId = current.Id, ListKey = kv.Key, Code = c }));
        var members = (i.Members ?? []).Select(kv => new ModularBuildMember { BuildId = current.Id, Role = kv.Key, NameRaw = kv.Value ?? "" });
        return ModularFieldSet.FromBuild(b, options, members);
    }

    private static IResult? Validate(BuildInput i)
    {
        var errors = new Dictionary<string, string[]>();
        if (i.Year is null) errors["year"] = ["연도를 입력하세요."];
        if (string.IsNullOrWhiteSpace(i.ProjectCode)) errors["projectCode"] = ["프로젝트 코드를 입력하세요."];
        if (string.IsNullOrWhiteSpace(i.ProjectName)) errors["projectName"] = ["프로젝트명을 입력하세요."];
        foreach (var key in i.Options?.Keys ?? Enumerable.Empty<string>())
            if (!MultiLists.Contains(key)) errors["options"] = [$"알 수 없는 선택 목록: {key}"];
        foreach (var key in i.Members?.Keys ?? Enumerable.Empty<string>())
            if (!RoleLabels.ContainsKey(key)) errors["members"] = [$"알 수 없는 담당 역할: {key}"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static void AddFields(NpgsqlCommand cmd, BuildInput i)
    {
        object N(object? v) => v ?? DBNull.Value;
        object T(string? v) => string.IsNullOrWhiteSpace(v) ? DBNull.Value : v.Trim();
        var p = cmd.Parameters;
        p.AddWithValue("year", N(i.Year));
        p.AddWithValue("code", i.ProjectCode.Trim());
        p.AddWithValue("name", i.ProjectName.Trim());
        p.Add(new NpgsqlParameter("inst", NpgsqlTypes.NpgsqlDbType.Date) { Value = N(i.InstallStart) });
        p.AddWithValue("instRaw", N(i.InstallStart?.ToString("yyyyMMdd")));
        p.Add(new NpgsqlParameter("comm", NpgsqlTypes.NpgsqlDbType.Date) { Value = N(i.CommissioningStart) });
        p.AddWithValue("commRaw", N(i.CommissioningStart?.ToString("yyyyMMdd")));
        p.AddWithValue("ext", T(i.ExternalInspection));
        p.Add(new NpgsqlParameter("ship", NpgsqlTypes.NpgsqlDbType.Date) { Value = N(i.ShipDue) });
        p.AddWithValue("shipMonth", T(i.ShipMonth));
        p.AddWithValue("tool", T(i.OriginToolReceipt));
        p.AddWithValue("note", T(i.Note));
        p.AddWithValue("customer", T(i.Customer));
        p.AddWithValue("plant", T(i.Plant));
        p.AddWithValue("process", T(i.Process));
        p.AddWithValue("detail", T(i.ProcessDetail));
        p.AddWithValue("robots", N(ModularFieldSet.LeadingInt(i.RobotCount)));
        p.AddWithValue("robotsRaw", T(i.RobotCount));
        p.AddWithValue("category", N(i.CategoryCode));
        p.AddWithValue("region", N(i.RegionCode));
        p.AddWithValue("issue", T(i.MainIssue));
    }

    private static async Task WriteChildrenAsync(NpgsqlConnection c, NpgsqlTransaction tx, long id, BuildInput input, CancellationToken ct)
    {
        await ExecAsync(c, tx, "delete from modular.build_option where build_id = @b", ct, ("b", id));
        foreach (var (list, codes) in input.Options ?? [])
            foreach (var code in codes.Distinct())
                await ExecAsync(c, tx, "insert into modular.build_option (build_id, list_key, code) values (@b, @l, @c)", ct, ("b", id), ("l", list), ("c", code));

        await ExecAsync(c, tx, "delete from modular.build_member where build_id = @b", ct, ("b", id));
        foreach (var (role, name) in input.Members ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            await ExecAsync(c, tx, @"insert into modular.build_member (build_id, role, name_raw, employee_id)
                values (@b, @r, @n, (select min(e.id) from org.employee e where e.name = @n and e.is_active having count(*) = 1))", ct,
                ("b", id), ("r", role), ("n", name.Trim()));
        }
    }

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
