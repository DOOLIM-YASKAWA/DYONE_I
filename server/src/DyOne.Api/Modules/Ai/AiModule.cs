using System.Security.Claims;
using System.Text.RegularExpressions;
using DyOne.Api.Modules.Projects;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Ai;

public record AskRequest(string Question, long? ProjectId);
public record AiSource(string Kind, long Id, string Label);
public record AskResponse(string Answer, IReadOnlyList<AiSource> Sources, string Engine);

/// <summary>
/// AI 답변 엔진. 지금은 dyone DB를 조회하는 규칙형이고, 로컬 LLM + RAG(pgvector, 권한 필터)가 준비되면 교체한다.
/// 프로젝트를 지정하면 그 프로젝트 기록만 참고한다(프로젝트 AI).
/// </summary>
public interface IAiAssistant
{
    Task<AskResponse> AskAsync(AskRequest request, CurrentUser user, CancellationToken ct);
}

public partial class RuleBasedAssistant(DyOneDbContext db) : IAiAssistant
{
    public async Task<AskResponse> AskAsync(AskRequest req, CurrentUser user, CancellationToken ct)
    {
        var q = req.Question.Trim();
        var project = req.ProjectId is { } pid ? await db.Projects.FirstOrDefaultAsync(p => p.Id == pid, ct) : null;

        // 질문에 프로젝트 코드가 있으면 그 프로젝트로 범위를 좁힌다(예: DLP-24-182)
        if (project is null && ProjectCode().Match(q) is { Success: true } m)
            project = await db.Projects.FirstOrDefaultAsync(p => p.Code == m.Value.ToUpper(), ct);

        var sources = new List<AiSource>();
        string text;

        if (q.Contains("문서"))
        {
            text = "아직 프로젝트 문서가 연결되어 있지 않습니다. 문서 저장소(Google Drive)와 문서 권한 연동 후 검색할 수 있습니다.";
        }
        else if (q.Contains("랩") || q.Contains("테스트"))
        {
            var rows = await db.LabTests.Where(l => project == null || l.ProjectId == project.Id)
                .OrderBy(l => l.StartedAt == null).ThenByDescending(l => l.StartedAt).Take(8).ToListAsync(ct);
            var dtos = await ProjectsModule.LabDtosAsync(db, rows, ct);
            text = dtos.Count == 0 ? "연결된 랩 테스트가 없습니다." : string.Join("\n\n", dtos.Select(l =>
                $"{l.Name} · {l.Type}\n{l.ProjectCode} {l.ProjectName}\n{l.StartedAt:yyyy-MM-dd} ~ {l.EndedAt:yyyy-MM-dd}"));
            sources.AddRange(dtos.Select(l => new AiSource("lab", l.Id, l.Name)));
        }
        else if (q.Contains("공정") || q.Contains("배당") || q.Contains("존") || q.Contains("zone", StringComparison.OrdinalIgnoreCase) || q.Contains("부스"))
        {
            if (project is null)
                text = "공정 배당은 프로젝트를 선택하거나 질문에 프로젝트 코드(예: DLP-24-182)를 넣어 주세요.";
            else
            {
                var rows = await ProjectsModule.AssignmentsAsync(db, project.Id, ct);
                text = rows.Count == 0 ? "배당된 공정이 없습니다."
                    : $"배당 공정 {rows.Count}건\n" + string.Join("\n", rows.Take(20).Select(a => string.Join(" › ", new[] { a.Customer, a.Factory, a.Booth, a.Zone }.Where(x => !string.IsNullOrEmpty(x)))));
                sources.Add(new AiSource("project", project.Id, project.Name));
            }
        }
        else if (q.Contains("담당") || q.Contains("맡고"))
        {
            text = "현재 프로젝트 데이터(그룹웨어 applet285 미러)에는 프로젝트 담당자 항목이 없습니다. 담당자 정보 원천이 정해지면 답변할 수 있습니다.";
        }
        else if (project is not null)
        {
            var dto = (await ProjectsModule.ToDtosAsync(db, [project], ct))[0];
            var assignments = await db.Assignments.CountAsync(a => a.ProjectId == project.Id && a.IsActive, ct);
            var labs = await db.LabTests.CountAsync(l => l.ProjectId == project.Id, ct);
            text = $"{dto.Code} · {dto.Name}\n상태 {dto.Status ?? "미지정"} · 종류 {dto.Type ?? "-"} · 발주처 {dto.PartnerName ?? "-"}\nERP 등록 {dto.ErpRegDate}\n배당 공정 {assignments}건 · 랩 테스트 {labs}건";
            sources.Add(new AiSource("project", project.Id, project.Name));
        }
        else if (q.Contains("현황") || q.Contains("요약") || q.Contains("상태") || q.Contains("몇"))
        {
            var statuses = await db.Codes.Where(c => c.GroupKey == "project_status").ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var counts = await db.Projects.Where(p => p.IsActive).GroupBy(p => p.StatusId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            text = "전체 프로젝트 현황\n" + string.Join("\n", counts.OrderByDescending(c => c.Count)
                .Select(c => $"{(c.Key is { } k && statuses.TryGetValue(k, out var n) ? n : "상태 미지정")} {c.Count}건"));
        }
        else
        {
            // 프로젝트 이름 검색
            var keyword = q.Replace("프로젝트", "").Replace("알려줘", "").Replace("찾아줘", "").Trim();
            var rows = keyword.Length < 2 ? [] : await db.Projects
                .Where(p => p.IsActive && (EF.Functions.ILike(p.Name, $"%{keyword}%") || EF.Functions.ILike(p.PartnerName ?? "", $"%{keyword}%")))
                .OrderBy(p => p.ErpRegDate == null).ThenByDescending(p => p.ErpRegDate).Take(10).ToListAsync(ct);
            var dtos = await ProjectsModule.ToDtosAsync(db, rows, ct);
            text = dtos.Count == 0
                ? "프로젝트 이름·코드·발주처, 랩 테스트, 공정 배당, 전체 현황을 질문해 주세요. 지금은 규칙형 답변입니다."
                : $"'{keyword}' 관련 프로젝트 {dtos.Count}건\n" + string.Join("\n", dtos.Select(p => $"{p.Code} · {p.Name} · {p.Status ?? "미지정"}"));
            sources.AddRange(dtos.Select(p => new AiSource("project", p.Id, p.Name)));
        }

        var answer = project is not null ? $"{project.Code} {project.Name} 기준\n\n{text}" : text;
        return new AskResponse(answer, sources, "rule-based");
    }

    [GeneratedRegex(@"\bD[A-Z]{1,3}-\d{2}-\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectCode();
}

public static class AiModule
{
    public static void MapAi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/ask", async (AskRequest req, ClaimsPrincipal p, IAiAssistant ai, AuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > 1000)
                return Results.Problem("질문은 1~1000자로 입력하세요.", statusCode: 400);
            var result = await ai.AskAsync(req, p.Current(), ct);
            audit.Record("ai.ask", "ai", req.ProjectId?.ToString() ?? "all", req.Question.Length > 200 ? req.Question[..200] : req.Question);
            return Results.Ok(result);
        }).WithTags("AI");
    }
}
