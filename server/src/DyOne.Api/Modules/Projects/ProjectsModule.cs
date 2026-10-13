using System.Security.Claims;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

public record StageUpdate(long StageId);

public record ProjectDto(
    long Id, string Code, string Name, string? PartnerName, long? StatusId, string? Status, string? Type,
    DateOnly? ErpRegDate, DateOnly? StartDate, DateOnly? EndDate, bool IsActive, DateTimeOffset UpdatedAt, string? Description,
    DateTime? SourceUpdatedAt = null, string? EffectiveStage = null, string? EffectiveSource = null);

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
                .Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(ct));

        g.MapGet("/projects", async (string? search, long? statusId, string? customer, int? page, int? pageSize, DyOneDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var current = Math.Max(page ?? 1, 1);
            var q = db.Projects.Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = $"%{search.Trim()}%";
                q = q.Where(p => EF.Functions.ILike(p.Code, s) || EF.Functions.ILike(p.Name, s) || EF.Functions.ILike(p.PartnerName ?? "", s));
            }
            if (statusId is not null) q = statusId == 0 ? q.Where(p => p.StatusId == null) : q.Where(p => p.StatusId == statusId);
            else
            {
                // [진행 중 모집단] 상태 미선택 시: 그룹웨어 영업중(0)/수주(1) '또는' dyone 진행 단계(project_stage)가 있는 것.
                // → 영업부가 ERP에 넣는 영업·수주 + 대응 인원이 dyone에서 진행시킨 건. 그룹웨어 마감/중단·미지정(단계 없음)은 제외.
                // 사용자가 상태 드롭다운에서 마감/중단/미지정을 직접 고르면 그 값은 그대로 보여준다.
                var salesStatusIds = await db.Codes.Where(c => c.GroupKey == "project_status" && (c.SortOrder == 0 || c.SortOrder == 1)).Select(c => c.Id).ToListAsync(ct);
                q = q.Where(p => (p.StatusId != null && salesStatusIds.Contains(p.StatusId.Value)) || db.ProjectStages.Any(ps => ps.ProjectId == p.Id));
            }
            if (!string.IsNullOrWhiteSpace(customer)) q = FilterByCustomer(q, customer.Trim());

            var total = await q.CountAsync(ct);
            var rows = await q.OrderBy(p => p.ErpRegDate == null).ThenByDescending(p => p.ErpRegDate).ThenByDescending(p => p.Code)
                .Skip((current - 1) * size).Take(size).ToListAsync(ct);
            return new PagedResult<ProjectDto>(total, current, size, await ToDtosAsync(db, rows, ct));
        });

        // 프로젝트 선택 상자용 검색(최대 20건)
        g.MapGet("/projects/lookup", async (string? search, DyOneDbContext db, CancellationToken ct) =>
        {
            var s = $"%{search?.Trim()}%";
            return await db.Projects.Where(p => p.IsActive && (EF.Functions.ILike(p.Code, s) || EF.Functions.ILike(p.Name, s)))
                .OrderBy(p => p.ErpRegDate == null).ThenByDescending(p => p.ErpRegDate).Take(20)
                .Select(p => new { p.Id, p.Code, p.Name }).ToListAsync(ct);
        });

        g.MapGet("/projects/{id:long}", async (long id, DyOneDbContext db, CancellationToken ct) =>
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
                                    select new { stageId = ps.StageId, stageCode = c.Code, stageName = c.Name, ps.UpdatedAt, updatedByName = e != null ? e.Name : null })
                                    .FirstOrDefaultAsync(ct);

            return Results.Ok(new { project = dto, assignments = await AssignmentsAsync(db, id, ct), apps, labTestCount = labCount, dyoneStage });
        });

        // dyone 자체 진행 단계 변경(사용자 편집). 그룹웨어는 안 건드림. 지금은 로그인한 전원 가능.
        g.MapPut("/projects/{id:long}/stage", async (long id, StageUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!await db.Projects.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            if (!await db.Codes.AnyAsync(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive, ct))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });
            long? empId = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;

            // 읽기 전용 연결(ConnectionStrings:DyOne)에서 read-only 옵션만 제거해 쓰기 연결을 만든다(사용자 편집 데이터 전용).
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"insert into project.project_stage (project_id, stage_id, updated_by, updated_at) values (@pid, @sid, @uid, now())
                  on conflict (project_id) do update set stage_id = excluded.stage_id, updated_by = excluded.updated_by, updated_at = now()", conn);
            cmd.Parameters.AddWithValue("pid", id);
            cmd.Parameters.AddWithValue("sid", body.StageId);
            cmd.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

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

        // 효과적 상태: dyone 단계(project_stage)가 있으면 그 단계, 없으면 그룹웨어 상태(영업중→영업, 수주→수주)
        var ids = rows.Select(r => r.Id).ToList();
        var stageMap = await (from ps in db.ProjectStages.Where(x => ids.Contains(x.ProjectId))
                              join c in db.Codes on ps.StageId equals c.Id
                              select new { ps.ProjectId, c.Name }).ToDictionaryAsync(x => x.ProjectId, x => x.Name, ct);

        return rows.Select(p =>
        {
            var gw = Name(p.StatusId);
            var dyone = stageMap.GetValueOrDefault(p.Id);
            var effective = dyone ?? (gw == "영업중" ? "영업 기회" : gw == "수주" ? "수주·착수" : null);
            var source = dyone != null ? "dyone" : (effective != null ? "gw" : null);
            return new ProjectDto(p.Id, p.Code, p.Name, p.PartnerName, p.StatusId, gw, Name(p.TypeId),
                p.ErpRegDate, p.StartDate, p.EndDate, p.IsActive, p.UpdatedAt, p.Description, p.SourceUpdatedAt, effective, source);
        }).ToList();
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
