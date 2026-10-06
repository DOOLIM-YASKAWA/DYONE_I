using DyOne.Data;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Projects;

public record ProjectDto(
    long Id, string Code, string Name, string? PartnerName, long? StatusId, string? Status, string? Type,
    DateOnly? ErpRegDate, DateOnly? StartDate, DateOnly? EndDate, bool IsActive, DateTimeOffset UpdatedAt, string? Description);

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

        g.MapGet("/projects", async (string? search, long? statusId, int? page, int? pageSize, DyOneDbContext db, CancellationToken ct) =>
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
            return Results.Ok(new { project = dto, assignments = await AssignmentsAsync(db, id, ct), apps, labTestCount = labCount });
        });

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

    public static async Task<List<ProjectDto>> ToDtosAsync(DyOneDbContext db, List<Data.Entities.Project> rows, CancellationToken ct)
    {
        var codeIds = rows.SelectMany(r => new[] { r.StatusId, r.TypeId }).OfType<long>().Distinct().ToList();
        var codes = await db.Codes.Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        string? Name(long? id) => id is { } v && codes.TryGetValue(v, out var n) ? n : null;
        return rows.Select(p => new ProjectDto(p.Id, p.Code, p.Name, p.PartnerName, p.StatusId, Name(p.StatusId), Name(p.TypeId),
            p.ErpRegDate, p.StartDate, p.EndDate, p.IsActive, p.UpdatedAt, p.Description)).ToList();
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
