using System.Security.Claims;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

/// <param name="Note">변경 사유(선택). 이력에 남는다.</param>
public record StageUpdate(long StageId, string? Note = null);

/// <summary>단계 변경 이력 한 줄. FromStage가 null이면 DY ONE에서 처음 지정한 것(그 전엔 그룹웨어 상태 기준).
/// FromStatus/ToStatus가 있으면 단계별 진행 상황 변경(ToStageId 단계가 FromStatus → ToStatus), 없으면 예전 방식의 대표 단계 이동.</summary>
public record StageHistoryDto(long Id, DateTimeOffset ChangedAt, string? ChangedByName, string? ChangedByDept, string? ChangedByPosition,
    long? FromStageId, string? FromStage, int? FromOrder, long? ToStageId, string? ToStage, int? ToOrder, string? Note,
    string? FromStatus = null, string? ToStatus = null, string Source = "dyone", string? GwFrom = null, string? GwTo = null);

/// <summary>단계별 진행 상황 한 칸. Status: wait(대기) · run(진행) · done(완료). UpdatedAt이 없으면 저장된 적 없음.
/// FromGroupware: 영업·수주처럼 그룹웨어 상태로 계산되는 단계(DY ONE에서 바꾸지 않음).</summary>
public record ProgressItem(long StageId, string Code, string Name, string Status, DateTimeOffset? UpdatedAt, string? UpdatedByName, bool FromGroupware = false);
public record ProgressChange(long StageId, string Status);
/// <param name="Note">변경 사유(선택). 바뀐 단계마다 이력에 같이 남는다.</param>
public record ProgressUpdate(List<ProgressChange> Changes, string? Note = null);

public record ProjectDto(
    long Id, string Code, string Name, string? PartnerName, long? StatusId, string? Status, string? Type,
    DateOnly? ErpRegDate, DateOnly? StartDate, DateOnly? EndDate, bool IsActive, DateTimeOffset UpdatedAt, string? Description,
    DateTime? SourceUpdatedAt = null, string? EffectiveStage = null, string? EffectiveSource = null, string? EffectiveStatus = null,
    DateTimeOffset? LastChangedAt = null, string? LastChangedSource = null, string? PmName = null,
    string? LastChangedBy = null, int RecentActivity7d = 0);
// LastChangedAt·Source·By = 최근 활동(ProjectActivity: 단계·파일·인원·그룹웨어·모듈라·ES·SmartLab·SRS 중 가장 최근). Source = 활동 종류 문구

public record AssignmentDto(string Level, string? Customer, string? Factory, string? Booth, string? Zone);

public record LabTestDto(
    long Id, string Name, string? Type, long? ProjectId, string? ProjectCode, string? ProjectName,
    string? Operator, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Description);

public record PagedResult<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);

/// <summary>
/// 프로젝트 화면. 기존 dyone DB를 읽는다.
/// - 진행 상태: project.project (그룹웨어 applet285 미러) + code.code(project_status · project_type)
/// - 공정 배당: project.project_assignment → plant.zone → booth → factory → customer
/// - 랩 테스트: smartlab.lab_test
/// 모듈라(376)·업무일지(296)·ES 이슈(281)는 아직 dyone DB에 없다.
/// </summary>
public static class ProjectsModule
{
    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Projects");

        g.MapGet("/codes/{group}", async (string group, DyOneDbContext db, CancellationToken ct) =>
            await db.Codes.Where(c => c.GroupKey == group && c.IsActive).OrderBy(c => c.SortOrder)
                .Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(ct)).PartnerAllowed();

        g.MapGet("/projects", async (string? search, long? statusId, string? customer, int? page, int? pageSize, ClaimsPrincipal user, ProjectScope scope, DyOneDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var current = Math.Max(page ?? 1, 1);
            var q = db.Projects.Where(p => p.IsActive);
            // 협력사: 배정된 프로젝트만(진행 중 모집단 조건 없이 배정된 것 전부)
            var visible = await scope.VisibleIdsAsync(user, ct);
            if (visible is not null) q = q.Where(p => visible.Contains(p.Id));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = $"%{search.Trim()}%";
                q = q.Where(p => EF.Functions.ILike(p.Code, s) || EF.Functions.ILike(p.Name, s) || EF.Functions.ILike(p.PartnerName ?? "", s));
            }
            if (statusId is not null) q = statusId == 0 ? q.Where(p => p.StatusId == null) : q.Where(p => p.StatusId == statusId);
            else if (visible is null)
            {
                // [진행 중 모집단] 상태 미선택 시: 그룹웨어 영업중(0)/수주(1) '또는' dyone 진행 단계(project_stage)가 있는 것.
                // → 영업부가 ERP에 넣는 영업·수주 + 대응 인원이 dyone에서 진행시킨 건. 그룹웨어 마감/중단·미지정(단계 없음)은 제외.
                // 사용자가 상태 드롭다운에서 마감/중단/미지정을 직접 고르면 그 값은 그대로 보여준다.
                var salesStatusIds = await db.Codes.Where(c => c.GroupKey == "project_status" && (c.SortOrder == 0 || c.SortOrder == 1)).Select(c => c.Id).ToListAsync(ct);
                q = q.Where(p => (p.StatusId != null && salesStatusIds.Contains(p.StatusId.Value)) || db.ProjectStages.Any(ps => ps.ProjectId == p.Id));
            }
            if (!string.IsNullOrWhiteSpace(customer)) q = FilterByCustomer(q, customer.Trim());

            // 정렬: 최근 변경(ProjectActivity — 단계·파일·인원·그룹웨어·모듈라·ES·SmartLab·SRS 중 가장 최근 작업) 최신순.
            // 조건에 맞는 id만 읽고, 프로젝트별 최근 활동(한 번의 조회)과 붙여 메모리에서 정렬·페이지를 자른다(수천 건 수준).
            var keys = await q.Select(p => new { p.Id, p.Code }).ToListAsync(ct);
            var activity = await ActivityAsync(db, null, ct);
            var pageIds = keys
                .Select(k => new { k.Id, k.Code, At = activity.TryGetValue(k.Id, out var a) ? a.At : (DateTimeOffset?)null })
                .OrderBy(k => k.At == null).ThenByDescending(k => k.At).ThenByDescending(k => k.Code)
                .Skip((current - 1) * size).Take(size).Select(k => k.Id).ToList();
            var byId = await db.Projects.Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var rows = pageIds.Select(id => byId[id]).ToList();
            return new PagedResult<ProjectDto>(keys.Count, current, size, await ToDtosAsync(db, rows, ct));
        }).PartnerAllowed();

        // 프로젝트 선택 상자용 검색(최대 20건)
        g.MapGet("/projects/lookup", async (string? search, ClaimsPrincipal user, ProjectScope scope, DyOneDbContext db, CancellationToken ct) =>
        {
            var s = $"%{search?.Trim()}%";
            var visible = await scope.VisibleIdsAsync(user, ct);
            return await db.Projects.Where(p => p.IsActive && (EF.Functions.ILike(p.Code, s) || EF.Functions.ILike(p.Name, s)))
                .Where(p => visible == null || visible.Contains(p.Id))
                .OrderBy(p => p.ErpRegDate == null).ThenByDescending(p => p.ErpRegDate).Take(20)
                .Select(p => new { p.Id, p.Code, p.Name }).ToListAsync(ct);
        }).PartnerAllowed();

        g.MapGet("/projects/{id:long}", async (long id, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            var dto = (await ToDtosAsync(db, [p], ct))[0];
            var apps = await db.ProjectApps.Where(a => a.ProjectId == id).Select(a => a.AppKey).ToListAsync(ct);
            var labCount = await db.LabTests.CountAsync(l => l.ProjectId == id, ct);

            // dyone 자체 진행 단계(사용자 편집). 없으면 null → 화면은 그룹웨어 상태(영업중/수주)를 기본으로.
            var dyoneStage = await (from ps in db.ProjectStages.Where(x => x.ProjectId == id)
                                    join c in db.Codes on ps.StageId equals c.Id
                                    join e in db.Employees on ps.UpdatedBy equals e.Id into ej
                                    from e in ej.DefaultIfEmpty()
                                    join d in db.Departments on e.DepartmentId equals d.Id into dj
                                    from d in dj.DefaultIfEmpty()
                                    select new { stageId = ps.StageId, stageCode = c.Code, stageName = c.Name, ps.UpdatedAt, updatedByName = e != null ? e.Name : null, updatedByDept = d != null ? d.Name : null })
                                    .FirstOrDefaultAsync(ct);

            var (progress, currentStageId) = await ProgressAsync(db, id, dto.Status, ct);
            return Results.Ok(new { project = dto, assignments = await AssignmentsAsync(db, id, ct), apps, labTestCount = labCount, dyoneStage, progress, currentStageId });
        }).PartnerAllowed();

        // dyone 자체 진행 단계 변경(사용자 편집). 그룹웨어는 안 건드림. 지금은 로그인한 전원 가능.
        // 바꿀 때마다 project.project_stage_history에 누가·언제·전→후·메모를 같은 트랜잭션으로 남긴다. 같은 단계로 바꾸면 아무것도 하지 않는다.
        g.MapPut("/projects/{id:long}/stage", async (long id, StageUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!await db.Projects.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!await db.Codes.AnyAsync(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive && !GwStageCodes.Contains(c.Code), ct))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다(영업·수주는 그룹웨어 상태를 따릅니다)." });
            long? empId = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;
            var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
            if (note is { Length: > 500 }) return Results.BadRequest(new { message = "메모는 500자까지 쓸 수 있습니다." });

            // 읽기 전용 연결(ConnectionStrings:DyOne)에서 read-only 옵션만 제거해 쓰기 연결을 만든다(사용자 편집 데이터 전용).
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            long? from;
            await using (var cur = new NpgsqlCommand("select stage_id from project.project_stage where project_id = @pid for update", conn, tx))
            {
                cur.Parameters.AddWithValue("pid", id);
                from = await cur.ExecuteScalarAsync(ct) as long?;
            }
            if (from == body.StageId) return Results.Ok(new { ok = true, changed = false });

            await using (var cmd = new NpgsqlCommand(
                @"insert into project.project_stage (project_id, stage_id, updated_by, updated_at) values (@pid, @sid, @uid, now())
                  on conflict (project_id) do update set stage_id = excluded.stage_id, updated_by = excluded.updated_by, updated_at = now()", conn, tx))
            {
                cmd.Parameters.AddWithValue("pid", id);
                cmd.Parameters.AddWithValue("sid", body.StageId);
                cmd.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var hist = new NpgsqlCommand(
                @"insert into project.project_stage_history (project_id, from_stage_id, to_stage_id, changed_by, changed_at, note)
                  values (@pid, @from, @to, @uid, now(), @note)", conn, tx))
            {
                hist.Parameters.AddWithValue("pid", id);
                hist.Parameters.AddWithValue("from", (object?)from ?? DBNull.Value);
                hist.Parameters.AddWithValue("to", body.StageId);
                hist.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                hist.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
                await hist.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { ok = true, changed = true });
        }).RequireAuthorization();

        // 단계별 진행 상황 변경(대기·진행·완료, 여러 단계 동시 진행 가능). 바뀐 단계마다 이력을 남기고, 대표 단계(project_stage)를 맞춘다.
        // 영업·수주는 그룹웨어 상태로 계산되므로 여기서 바꾸지 않는다. 저장된 적 없는 단계는 대기로 본다.
        g.MapPut("/projects/{id:long}/progress", async (long id, ProgressUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (body.Changes is not { Count: > 0 }) return Results.BadRequest(new { message = "바꿀 단계가 없습니다." });
            if (body.Changes.Any(c => c.Status is not ("wait" or "run" or "done")))
                return Results.BadRequest(new { message = "상태는 대기·진행·완료만 쓸 수 있습니다." });
            var stages = (await StagesAsync(db, ct)).Where(s => !GwStageCodes.Contains(s.Code)).ToList(); // DY ONE에서 바꾸는 단계(설계·구매부터)
            if (body.Changes.Any(c => stages.All(s => s.Id != c.StageId)))
                return Results.BadRequest(new { message = "바꿀 수 없는 단계입니다(영업·수주는 그룹웨어 상태를 따릅니다)." });
            var note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
            if (note is { Length: > 500 }) return Results.BadRequest(new { message = "메모는 500자까지 쓸 수 있습니다." });
            long? empId = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;

            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var current = new Dictionary<long, string>();
            await using (var cur = new NpgsqlCommand("select stage_id, status from project.project_stage_status where project_id = @pid for update", conn, tx))
            {
                cur.Parameters.AddWithValue("pid", id);
                await using var r = await cur.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) current[r.GetInt64(0)] = r.GetString(1);
            }
            var changed = 0;
            foreach (var c in body.Changes.DistinctBy(c => c.StageId))
            {
                var from = current.GetValueOrDefault(c.StageId, "wait");
                if (from == c.Status) continue;
                await using (var up = new NpgsqlCommand(
                    @"insert into project.project_stage_status (project_id, stage_id, status, updated_by, updated_at) values (@pid, @sid, @st, @uid, now())
                      on conflict (project_id, stage_id) do update set status = excluded.status, updated_by = excluded.updated_by, updated_at = now()", conn, tx))
                {
                    up.Parameters.AddWithValue("pid", id);
                    up.Parameters.AddWithValue("sid", c.StageId);
                    up.Parameters.AddWithValue("st", c.Status);
                    up.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                    await up.ExecuteNonQueryAsync(ct);
                }
                await using (var hist = new NpgsqlCommand(
                    @"insert into project.project_stage_history (project_id, from_stage_id, to_stage_id, changed_by, changed_at, note, from_status, to_status)
                      values (@pid, null, @sid, @uid, now(), @note, @from, @to)", conn, tx))
                {
                    hist.Parameters.AddWithValue("pid", id);
                    hist.Parameters.AddWithValue("sid", c.StageId);
                    hist.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                    hist.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
                    hist.Parameters.AddWithValue("from", from);
                    hist.Parameters.AddWithValue("to", c.Status);
                    await hist.ExecuteNonQueryAsync(ct);
                }
                current[c.StageId] = c.Status;
                changed++;
            }
            if (changed == 0) return Results.Ok(new { ok = true, changed });

            // 대표 단계(목록·홈 모집단용 project_stage): DY ONE 단계 중 진행 중 가장 뒤, 없으면 완료 중 가장 뒤, 둘 다 없으면 지정 해제(그룹웨어 상태 기준)
            var order = stages.Select((st, i) => (st.Id, i)).ToDictionary(x => x.Id, x => x.i);
            long? Last(string status) => current.Where(kv => kv.Value == status).OrderByDescending(kv => order.GetValueOrDefault(kv.Key))
                .Select(kv => (long?)kv.Key).FirstOrDefault();
            var main = Last("run") ?? Last("done");
            await using (var ms = main is not null
                ? new NpgsqlCommand(@"insert into project.project_stage (project_id, stage_id, updated_by, updated_at) values (@pid, @sid, @uid, now())
                      on conflict (project_id) do update set stage_id = excluded.stage_id, updated_by = excluded.updated_by, updated_at = now()", conn, tx)
                : new NpgsqlCommand("delete from project.project_stage where project_id = @pid", conn, tx))
            {
                ms.Parameters.AddWithValue("pid", id);
                if (main is { } m)
                {
                    ms.Parameters.AddWithValue("sid", m);
                    ms.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                }
                await ms.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { ok = true, changed });
        }).RequireAuthorization();

        // 단계 변경 이력(최신순). 변경자 이름·부서·직위와 전→후 단계(S 번호 = 단계 정렬 순서)를 함께 준다.
        g.MapGet("/projects/{id:long}/stage-history", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                select h.id, h.changed_at, e.name, d.name, p.name,
                       h.from_stage_id, f.name, f.sort_order, h.to_stage_id, t.name, t.sort_order, h.note, h.from_status, h.to_status,
                       h.source, h.gw_from, h.gw_to
                from project.project_stage_history h
                left join code.code t on t.id = h.to_stage_id
                left join code.code f on f.id = h.from_stage_id
                left join org.employee e on e.id = h.changed_by
                left join org.department d on d.id = e.department_id
                left join org.position p on p.id = e.position_id
                where h.project_id = @pid
                order by h.changed_at desc, h.id desc
                limit 200", conn);
            cmd.Parameters.AddWithValue("pid", id);
            var list = new List<StageHistoryDto>();
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                while (await r.ReadAsync(ct))
                    list.Add(new StageHistoryDto(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), S(2), S(3), S(4),
                        r.IsDBNull(5) ? null : r.GetInt64(5), S(6), r.IsDBNull(7) ? null : r.GetInt32(7),
                        r.IsDBNull(8) ? null : r.GetInt64(8), S(9), r.IsDBNull(10) ? null : r.GetInt32(10), S(11), S(12), S(13),
                        S(14) ?? "dyone", S(15), S(16)));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // 이력 테이블 적용 전(server/db/project_stage_history.sql): 빈 목록
            }
            return Results.Ok(list);
        }).PartnerAllowed();

        g.MapGet("/lab-tests", async (long? projectId, string? search, int? page, int? pageSize, DyOneDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var current = Math.Max(page ?? 1, 1);
            var q = db.LabTests.AsQueryable();
            if (projectId is not null) q = q.Where(l => l.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(l => EF.Functions.ILike(l.Name, $"%{search.Trim()}%"));
            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(l => l.StartedAt == null).ThenByDescending(l => l.StartedAt).ThenByDescending(l => l.Id)
                .Skip((current - 1) * size).Take(size).ToListAsync(ct);
            return new PagedResult<LabTestDto>(total, current, size, await LabDtosAsync(db, rows, ct));
        });
    }

    private sealed record StageDef(long Id, string Code, string Name);

    /// <summary>진행 단계 목록(S1~S9, 정렬 순서)</summary>
    private static async Task<List<StageDef>> StagesAsync(DyOneDbContext db, CancellationToken ct) =>
        await db.Codes.Where(c => c.GroupKey == "project_stage" && c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new StageDef(c.Id, c.Code, c.Name)).ToListAsync(ct);

    /// <summary>그룹웨어 상태로 계산하는 단계(영업 · 수주). 이 뒤 단계(설계·구매부터)는 DY ONE에서 대기·진행·완료로 직접 관리한다.</summary>
    private static readonly string[] GwStageCodes = ["opportunity", "order"];

    private sealed record StageView(StageDef Stage, string Status, bool FromGw);

    /// <summary>
    /// 단계별 진행 상황과 대표(현재) 단계 계산 — 목록·홈·상세가 모두 이 규칙 하나로 표시한다.
    /// 영업·수주(그룹웨어): 영업중 = 영업 진행 / 수주 = 영업 완료 · 수주 진행(설계 이후가 시작되면 수주 완료) / 마감 = 둘 다 완료 / 중단 = 영업 완료 · 수주 대기 / 없음 = 대기.
    /// 설계·구매 이후(DY ONE): 저장된 값, 없으면 대기.
    /// 대표 단계: DY ONE 단계 중 진행 중 가장 뒤 → 완료 중 가장 뒤 → 그룹웨어 단계 중 진행 중인 것 → 없음(-1).
    /// </summary>
    private static (List<StageView> Steps, int Current) Compute(List<StageDef> stages, string? gw, IReadOnlyDictionary<long, string> saved)
    {
        var started = stages.Any(st => !GwStageCodes.Contains(st.Code) && saved.GetValueOrDefault(st.Id) is "run" or "done");
        string Gw(string code) => (gw, code) switch
        {
            ("영업중", "opportunity") => "run",
            ("수주", "opportunity") => "done",
            ("수주", _) => started ? "done" : "run",
            ("마감", _) => "done",
            ("중단", "opportunity") => "done",
            _ => "wait",
        };
        var steps = stages.Select(st => GwStageCodes.Contains(st.Code)
            ? new StageView(st, Gw(st.Code), true)
            : new StageView(st, saved.GetValueOrDefault(st.Id, "wait"), false)).ToList();
        var cur = steps.FindLastIndex(x => !x.FromGw && x.Status == "run");
        if (cur < 0) cur = steps.FindLastIndex(x => !x.FromGw && x.Status == "done");
        if (cur < 0) cur = steps.FindLastIndex(x => x.FromGw && x.Status == "run");
        return (steps, cur);
    }

    /// <summary>프로젝트 상세의 단계별 진행 상황 + 대표 단계 id</summary>
    private static async Task<(List<ProgressItem> Items, long? CurrentStageId)> ProgressAsync(DyOneDbContext db, long projectId, string? gwStatus, CancellationToken ct)
    {
        var stages = await StagesAsync(db, ct);
        var saved = await (from st in db.ProjectStageStatuses.Where(x => x.ProjectId == projectId)
                           join e in db.Employees on st.UpdatedBy equals e.Id into ej
                           from e in ej.DefaultIfEmpty()
                           select new { st.StageId, st.Status, st.UpdatedAt, By = e != null ? e.Name : null }).ToListAsync(ct);
        var map = saved.ToDictionary(x => x.StageId);
        var (steps, cur) = Compute(stages, gwStatus, saved.ToDictionary(x => x.StageId, x => x.Status));
        var items = steps.Select(v => map.TryGetValue(v.Stage.Id, out var m) && !v.FromGw
            ? new ProgressItem(v.Stage.Id, v.Stage.Code, v.Stage.Name, v.Status, m.UpdatedAt, m.By, false)
            : new ProgressItem(v.Stage.Id, v.Stage.Code, v.Stage.Name, v.Status, null, null, v.FromGw)).ToList();
        return (items, cur >= 0 ? steps[cur].Stage.Id : null);
    }

    /// <summary>발주처명 기준 고객사 그룹 필터 — 홈 customerCounts(HomeModule.CustomerGroup)와 동일 분류.</summary>
    private static IQueryable<Data.Entities.Project> FilterByCustomer(IQueryable<Data.Entities.Project> q, string customer) => customer switch
    {
        "현대" => q.Where(p => EF.Functions.ILike(p.PartnerName ?? "", "%현대%")),
        "기아" => q.Where(p => EF.Functions.ILike(p.PartnerName ?? "", "%기아%")),
        "GM" => q.Where(p => EF.Functions.ILike(p.PartnerName ?? "", "%지엠%") || EF.Functions.ILike(p.PartnerName ?? "", "%GM%")),
        "르노" => q.Where(p => EF.Functions.ILike(p.PartnerName ?? "", "%르노%")),
        "쌍용" => q.Where(p => EF.Functions.ILike(p.PartnerName ?? "", "%쌍용%") || EF.Functions.ILike(p.PartnerName ?? "", "%KG%")),
        "기타" => q.Where(p => p.PartnerName != null && p.PartnerName != ""
            && !EF.Functions.ILike(p.PartnerName, "%현대%") && !EF.Functions.ILike(p.PartnerName, "%기아%")
            && !EF.Functions.ILike(p.PartnerName, "%지엠%") && !EF.Functions.ILike(p.PartnerName, "%GM%")
            && !EF.Functions.ILike(p.PartnerName, "%르노%") && !EF.Functions.ILike(p.PartnerName, "%쌍용%")
            && !EF.Functions.ILike(p.PartnerName, "%KG%")),
        _ => q,
    };

    public static async Task<List<ProjectDto>> ToDtosAsync(DyOneDbContext db, List<Data.Entities.Project> rows, CancellationToken ct)
    {
        var codeIds = rows.SelectMany(r => new[] { r.StatusId, r.TypeId }).OfType<long>().Distinct().ToList();
        var codes = await db.Codes.Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string? Name(long? id) => id is { } v && codes.TryGetValue(v, out var n) ? n : null;

        // 진행 단계(목록·홈 표시): 상세 화면과 같은 규칙(Compute)으로 대표 단계와 그 상태를 구한다
        var ids = rows.Select(r => r.Id).ToList();
        var stages = await StagesAsync(db, ct);
        var saved = (await db.ProjectStageStatuses.Where(x => ids.Contains(x.ProjectId)).Select(x => new { x.ProjectId, x.StageId, x.Status }).ToListAsync(ct))
            .GroupBy(x => x.ProjectId).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<long, string>)g.ToDictionary(x => x.StageId, x => x.Status));
        var empty = new Dictionary<long, string>();
        var activity = await ActivityAsync(db, ids, ct);
        // PM(project.project_member): 엔티티 없이 같은 연결로 읽는다
        await db.Database.OpenConnectionAsync(ct);
        Dictionary<long, string> pms;
        try { pms = await Org.OrgModule.PmNamesAsync((NpgsqlConnection)db.Database.GetDbConnection(), ids, ct); }
        finally { await db.Database.CloseConnectionAsync(); }

        return rows.Select(p =>
        {
            var gw = Name(p.StatusId);
            var (steps, cur) = Compute(stages, gw, saved.GetValueOrDefault(p.Id) ?? empty);
            var v = cur >= 0 ? steps[cur] : null;
            var act = activity.GetValueOrDefault(p.Id);
            return new ProjectDto(p.Id, p.Code, p.Name, p.PartnerName, p.StatusId, gw, Name(p.TypeId),
                p.ErpRegDate, p.StartDate, p.EndDate, p.IsActive, p.UpdatedAt, p.Description, p.SourceUpdatedAt,
                v?.Stage.Name, v is null ? null : v.FromGw ? "gw" : "dyone", v?.Status, act?.At, act?.Label, pms.GetValueOrDefault(p.Id), act?.By, act?.Recent7d ?? 0);
        }).ToList();
    }

    /// <summary>프로젝트별 최근 활동(ids가 null이면 전체). EF 연결을 그대로 빌려 쓴다.</summary>
    public static async Task<Dictionary<long, ProjectActivityInfo>> ActivityAsync(DyOneDbContext db, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try { return await ProjectActivity.LatestAsync((NpgsqlConnection)db.Database.GetDbConnection(), ids, ct); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    /// <summary>배당 레벨(factory·booth·zone)에 따라 비어 있는 상위 공정은 하위에서 거슬러 올라가 채운다.</summary>
    public static async Task<List<AssignmentDto>> AssignmentsAsync(DyOneDbContext db, long projectId, CancellationToken ct)
    {
        var rows = await db.Assignments.Where(a => a.ProjectId == projectId && a.IsActive).ToListAsync(ct);
        var zoneIds = rows.Select(r => r.ZoneId).OfType<long>().ToHashSet();
        var zones = await db.Zones.Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, ct);
        var boothIds = rows.Select(r => r.BoothId ?? (r.ZoneId is { } z && zones.TryGetValue(z, out var zz) ? zz.BoothId : null)).OfType<long>().ToHashSet();
        var booths = await db.Booths.Where(b => boothIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        long? BoothOf(Data.Entities.ProjectAssignment a) => a.BoothId ?? (a.ZoneId is { } z && zones.TryGetValue(z, out var zz) ? zz.BoothId : null);
        var factoryIds = rows.Select(r => r.FactoryId ?? (BoothOf(r) is { } b && booths.TryGetValue(b, out var bb) ? bb.FactoryId : null)).OfType<long>().ToHashSet();
        var factories = await db.Factories.Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var customerIds = factories.Values.Select(f => f.CustomerId).ToHashSet();
        var customers = await db.Customers.Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return rows.Select(a =>
            {
                var zone = a.ZoneId is { } z && zones.TryGetValue(z, out var zz) ? zz : null;
                var booth = BoothOf(a) is { } b && booths.TryGetValue(b, out var bb) ? bb : null;
                var factory = (a.FactoryId ?? booth?.FactoryId) is { } f && factories.TryGetValue(f, out var ff) ? ff : null;
                var customer = factory is not null && customers.TryGetValue(factory.CustomerId, out var cn) ? cn : null;
                return new AssignmentDto(a.Level, customer, factory?.Name, booth?.Name, zone?.Name);
            })
            .OrderBy(x => x.Customer).ThenBy(x => x.Factory).ThenBy(x => x.Booth).ThenBy(x => x.Zone)
            .ToList();
    }

    public static async Task<List<LabTestDto>> LabDtosAsync(DyOneDbContext db, List<Data.Entities.LabTest> rows, CancellationToken ct)
    {
        var typeIds = rows.Select(r => r.TestTypeId).OfType<long>().Distinct().ToList();
        var projectIds = rows.Select(r => r.ProjectId).OfType<long>().Distinct().ToList();
        var operatorIds = rows.Select(r => r.OperatorId).OfType<long>().Distinct().ToList();
        var types = await db.Codes.Where(c => typeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var operators = await db.Employees.Where(e => operatorIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        return rows.Select(l => new LabTestDto(
            l.Id, l.Name,
            l.TestTypeId is { } t && types.TryGetValue(t, out var tn) ? tn : null,
            l.ProjectId,
            l.ProjectId is { } p1 && projects.TryGetValue(p1, out var pr) ? pr.Code : null,
            l.ProjectId is { } p2 && projects.TryGetValue(p2, out var pr2) ? pr2.Name : null,
            l.OperatorId is { } o && operators.TryGetValue(o, out var on) ? on : null,
            l.StartedAt, l.EndedAt, l.Description)).ToList();
    }
}
