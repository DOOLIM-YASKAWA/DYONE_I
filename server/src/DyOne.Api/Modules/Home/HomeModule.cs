using System.Security.Claims;
using DyOne.Api.Modules.Projects;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Home;

public static class HomeModule
{
    public static void MapHome(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/home/summary", async (ClaimsPrincipal principal, DyOneDbContext db, CancellationToken ct) =>
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

            // 최근 활동 프로젝트: 단계·파일·인원·그룹웨어 수정·ERP 등록(새 프로젝트)·모듈라·ES·SmartLab·SRS 중 가장 최근 작업이 최신인 6건(ProjectActivity)
            var activity = await ProjectsModule.ActivityAsync(db, null, ct);
            var recentIds = activity.OrderByDescending(kv => kv.Value.At).Select(kv => kv.Key).Take(30).ToList();
            var recentRows = await db.Projects.Where(p => recentIds.Contains(p.Id) && p.IsActive).ToListAsync(ct);
            var recentDtos = (await ProjectsModule.ToDtosAsync(db, recentRows, ct))
                .OrderByDescending(d => d.LastChangedAt).ThenByDescending(d => d.Code).Take(6).ToList();

            // MY 프로젝트: 내가 PM·투입 인원이거나, 내가 진행 상황을 바꿨거나 파일을 올렸거나 인원을 배정한 프로젝트(내 마지막 관여 최신순)
            var myProjects = new List<object>();
            if (long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var me))
            {
                var mine = await MyProjectsAsync(db, me, ct);
                var rows = await db.Projects.Where(p => mine.Select(m => m.ProjectId).Contains(p.Id)).ToListAsync(ct);
                var dtos = (await ProjectsModule.ToDtosAsync(db, rows, ct)).ToDictionary(d => d.Id);
                myProjects = mine.Where(m => dtos.ContainsKey(m.ProjectId))
                    .Select(m => (object)new { project = dtos[m.ProjectId], myLastAt = m.At, reasons = m.Reasons }).ToList();
            }

            var sync = await db.SyncStates.OrderByDescending(s => s.UpdatedAt).Take(5)
                .Select(s => new { s.Source, s.Entity, s.UpdatedAt }).ToListAsync(ct);

            return new
            {
                customerCounts,
                myProjects,
                recentChanged = recentDtos,
                sync,
            };
        }).WithTags("Home");
    }

    private static readonly Dictionary<string, string> MyReasonLabels = new()
    {
        ["pm"] = "PM", ["team"] = "투입 인원", ["stage"] = "진행 상황 변경", ["file"] = "파일 업로드", ["member"] = "인원 배정",
    };

    /// <summary>내가 관여한 프로젝트 최대 8건: 맡은 역할(PM·투입) + 내가 한 일(진행 상황·파일·인원 배정). 내 마지막 관여 시각 최신순.</summary>
    private static async Task<List<(long ProjectId, DateTimeOffset At, List<string> Reasons)>> MyProjectsAsync(DyOneDbContext db, long me, CancellationToken ct)
    {
        var list = new List<(long, DateTimeOffset, List<string>)>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand($@"
                with ev as ({ProjectActivity.Events}),
                mine as (
                  select project_id, at, kind from ev where emp = @me and kind in ('stage', 'file', 'member')
                  union all
                  select project_id, assigned_at, case when role = 'pm' then 'pm' else 'team' end from project.project_member where employee_id = @me and is_active)
                select project_id, max(at), array_agg(distinct kind)
                  from mine join project.project p on p.id = mine.project_id and p.is_active
                 group by project_id order by max(at) desc limit 8", (NpgsqlConnection)db.Database.GetDbConnection());
            cmd.Parameters.AddWithValue("me", me);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var order = MyReasonLabels.Keys.ToList();
            while (await r.ReadAsync(ct))
                list.Add((r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1),
                    r.GetFieldValue<string[]>(2).OrderBy(k => order.IndexOf(k)).Select(k => MyReasonLabels.GetValueOrDefault(k, k)).ToList()));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { }
        finally { await db.Database.CloseConnectionAsync(); }
        return list;
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
