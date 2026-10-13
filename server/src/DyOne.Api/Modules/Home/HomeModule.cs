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
            // [진행 중 모집단] 그룹웨어 영업중(0)/수주(1) 또는 dyone 진행 단계(project_stage)가 있는 프로젝트.
            // 그룹웨어 마감/중단·미지정(단계 없음)은 제외. 효과적 상태 = dyone 단계 있으면 그것, 없으면 GW(영업중→영업/수주→수주).
            var salesStatusIds = await db.Codes.Where(c => c.GroupKey == "project_status" && (c.SortOrder == 0 || c.SortOrder == 1)).Select(c => c.Id).ToListAsync(ct);
            // 고객사별: 발주처명(partner_name)에서 고객사 그룹(현대/기아/GM/르노/쌍용/기타)으로 분류해 집계.
            // (미러엔 고객사 코드가 따로 없어 발주처명으로 판별. 클릭 시 그 고객사 키워드로 프로젝트 조회)
            var partnerCounts = await db.Projects.Where(p => p.IsActive && p.PartnerName != null && p.PartnerName != "" && (p.StatusId != null && salesStatusIds.Contains(p.StatusId.Value) || db.ProjectStages.Any(ps => ps.ProjectId == p.Id)))
                .GroupBy(p => p.PartnerName!).Select(g => new { name = g.Key, count = g.Count() }).ToListAsync(ct);
            var order = new[] { "현대", "기아", "GM", "르노", "쌍용", "기타" };
            var customerCounts = partnerCounts.GroupBy(x => CustomerGroup(x.name))
                .Select(g => new { name = g.Key, count = g.Sum(x => x.count) })
                .OrderBy(x => Array.IndexOf(order, x.name)).ToList();

            // 최근 등록: ERP 등록날짜(erp_reg_date) 최신순 — 실제 ERP에 등록된 날짜 기준
            var recentRegistered = await db.Projects.Where(p => p.IsActive && p.ErpRegDate != null && (p.StatusId != null && salesStatusIds.Contains(p.StatusId.Value) || db.ProjectStages.Any(ps => ps.ProjectId == p.Id)))
                .OrderByDescending(p => p.ErpRegDate).ThenByDescending(p => p.Code).Take(6).ToListAsync(ct);

            // 최근 변경: 그룹웨어 문서 수정일(source_updated_at) 최신순 — GW에서 마지막으로 바뀐 순
            var recentChanged = await db.Projects.Where(p => p.IsActive && p.SourceUpdatedAt != null && (p.StatusId != null && salesStatusIds.Contains(p.StatusId.Value) || db.ProjectStages.Any(ps => ps.ProjectId == p.Id)))
                .OrderByDescending(p => p.SourceUpdatedAt).ThenByDescending(p => p.Code).Take(6).ToListAsync(ct);

            var sync = await db.SyncStates.OrderByDescending(s => s.UpdatedAt).Take(5)
                .Select(s => new { s.Source, s.Entity, s.UpdatedAt }).ToListAsync(ct);

            return new
            {
                customerCounts,
                recentRegistered = await ProjectsModule.ToDtosAsync(db, recentRegistered, ct),
                recentChanged = await ProjectsModule.ToDtosAsync(db, recentChanged, ct),
                sync,
            };
        }).WithTags("Home");
    }

    /// <summary>발주처명으로 고객사 그룹을 판별한다(현대/기아/GM/르노/쌍용/기타).</summary>
    private static string CustomerGroup(string? partner)
    {
        var s = partner ?? "";
        if (s.Contains("현대")) return "현대";
        if (s.Contains("기아")) return "기아";
        if (s.Contains("지엠") || s.Contains("GM")) return "GM";
        if (s.Contains("르노")) return "르노";
        if (s.Contains("쌍용") || s.Contains("KG")) return "쌍용";
        return "기타";
    }
}
