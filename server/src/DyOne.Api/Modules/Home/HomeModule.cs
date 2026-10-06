using DyOne.Api.Modules.Projects;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Home;

public static class HomeModule
{
    public static void MapHome(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/home/summary", async (DyOneDbContext db, CancellationToken ct) =>
        {
            var statuses = await db.Codes.Where(c => c.GroupKey == "project_status" && c.IsActive).OrderBy(c => c.SortOrder).ToListAsync(ct);
            var counts = await db.Projects.Where(p => p.IsActive).GroupBy(p => p.StatusId)
                .Select(g => new { StatusId = g.Key, Count = g.Count() }).ToListAsync(ct);

            var statusCounts = statuses.Select(s => new { statusId = s.Id, status = s.Name, count = counts.FirstOrDefault(c => c.StatusId == s.Id)?.Count ?? 0 })
                .Append(new { statusId = 0L, status = "상태 미지정", count = counts.FirstOrDefault(c => c.StatusId == null)?.Count ?? 0 })
                .ToList();

            // 주요 프로젝트: 수주 상태 중 최근 ERP 등록 순
            var wonId = statuses.FirstOrDefault(s => s.Name == "수주")?.Id;
            var recent = await db.Projects.Where(p => p.IsActive && (wonId == null || p.StatusId == wonId))
                .OrderBy(p => p.ErpRegDate == null).ThenByDescending(p => p.ErpRegDate).ThenByDescending(p => p.Code).Take(6).ToListAsync(ct);

            var labs = await db.LabTests.OrderBy(l => l.StartedAt == null).ThenByDescending(l => l.StartedAt).ThenByDescending(l => l.Id).Take(5).ToListAsync(ct);
            var sync = await db.SyncStates.OrderByDescending(s => s.UpdatedAt).Take(5)
                .Select(s => new { s.Source, s.Entity, s.UpdatedAt }).ToListAsync(ct);

            return new
            {
                statusCounts,
                recentProjects = await ProjectsModule.ToDtosAsync(db, recent, ct),
                recentLabTests = await ProjectsModule.LabDtosAsync(db, labs, ct),
                sync,
            };
        }).WithTags("Home");
    }
}
