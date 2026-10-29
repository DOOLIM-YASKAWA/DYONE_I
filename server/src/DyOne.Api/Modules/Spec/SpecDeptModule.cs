using System.Text.Json;
using DyOne.Api.Modules.Projects;
using DyOne.Data;
using DyOne.Spec.Dept;
using DyOne.Spec.Draft;
using DyOne.Spec.Extraction;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Spec;

/// <summary>pptx 부서별 장 한 줄: 화면에서 정한 상태·코멘트를 그대로 받는다(상태는 아직 DB에 두지 않음 — 화면 먼저, 표는 나중)</summary>
public record DeptSlideItem(string Title, string Status, string? Evidence, string? Incident, string? Note, string? By);
public record DeptSlide(string Dept, string Group, string? Budget, List<DeptSlideItem> Items);
public record DeptPptxRequest(List<DeptSlide> Roles);

/// <summary>
/// 사양서 분석 '부서별 확인': 사양정리 초안을 만든 뒤 역할(부서)마다 사양서에서 확인할 항목과 근거 쪽을 보여 주고,
/// 사양정리 pptx 뒤에 역할별 한 장씩 붙여 내려받는다. 사양서·초안은 읽기만 한다(DB 변경 없음).
/// </summary>
public static class SpecDeptModule
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapSpecDept(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("SpecDept").RequireAuthorization();

        g.MapGet("/projects/{projectId:long}/spec-dept", async (long projectId, long fileId, DyOneDbContext db, FileStorage storage,
            SpecExtractor extractor, IConfiguration config, CancellationToken ct) =>
        {
            var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
            if (file is null) return Results.NotFound(new { message = "사양서를 찾을 수 없습니다." });
            var ex = await SpecModule.ExtractAsync(file, storage, extractor, ct);
            var draft = ReadDraft(config, projectId, fileId);
            return Results.Ok(new { draftReady = draft is not null, fileName = file.FileName, roles = DeptMatcher.Match(ex.Blocks, draft) });
        });

        // 사양정리 초안 pptx + 역할별 한 장(render_spec_summary.py 뒤에 spec_dept_pages.py)
        g.MapPost("/projects/{projectId:long}/spec-dept/pptx", async (long projectId, long fileId, DeptPptxRequest req, DyOneDbContext db,
            FileStorage storage, IConfiguration config, CancellationToken ct) =>
        {
            var draft = ReadDraft(config, projectId, fileId);
            var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
            var project = await db.Projects.Where(p => p.Id == projectId).Select(p => new { p.Code, p.Name }).FirstOrDefaultAsync(ct);
            if (draft is null || file is null || project is null) return Results.NotFound(new { message = "사양정리 초안을 먼저 만들어 주세요." });
            if (config["Spec:PythonPath"] is not { Length: > 0 } py) return Results.BadRequest(new { message = "Spec:PythonPath(python-pptx·PyMuPDF)가 설정되지 않았습니다." });

            var tmp = Directory.CreateTempSubdirectory("spec-dept-");
            try
            {
                var draftJson = Path.Combine(tmp.FullName, "draft.json");
                var draftPptx = Path.Combine(tmp.FullName, "draft.pptx");
                var deptJson = Path.Combine(tmp.FullName, "dept.json");
                var outPath = Path.Combine(tmp.FullName, "out.pptx");
                await File.WriteAllTextAsync(draftJson, DraftBuilder.ToRenderJson(draft, await SpecModule.ViewPdfAsync(file, storage, ct)).ToJsonString(), ct);
                var (exit, _, err) = await SpecPython.RunAsync(py, "render_spec_summary.py",
                    [SpecPython.Template("spec_summary_v5.pptx"), draftJson, draftPptx, tmp.FullName], TimeSpan.FromMinutes(2), ct);
                if (exit != 0 || !File.Exists(draftPptx))
                    return Results.Problem($"pptx를 만들지 못했습니다: {LastLine(err)}");
                await File.WriteAllTextAsync(deptJson, JsonSerializer.Serialize(new { title = $"[{project.Code}] {project.Name}", roles = req.Roles }, Json), ct);
                (exit, _, err) = await SpecPython.RunAsync(py, "spec_dept_pages.py", [draftPptx, deptJson, outPath], TimeSpan.FromMinutes(1), ct);
                if (exit != 0 || !File.Exists(outPath))
                    return Results.Problem($"부서별 장을 붙이지 못했습니다: {LastLine(err)}");
                var name = $"[{project.Code}] {project.Name} 사양정리 초안(부서별 확인).pptx";
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return Results.File(await File.ReadAllBytesAsync(outPath, ct), "application/vnd.openxmlformats-officedocument.presentationml.presentation", name);
            }
            finally { try { tmp.Delete(true); } catch { /* 임시 폴더 */ } }
        });
    }

    private static string? LastLine(string err) => err.Split('\n').LastOrDefault(l => l.Trim().Length > 0);

    /// <summary>사양정리 초안(SpecDraftModule이 FileStorage:Root\_spec\drafts\{프로젝트}_{파일}.json에 둔 것) — 읽기만</summary>
    private static SummaryDraft? ReadDraft(IConfiguration config, long projectId, long fileId)
    {
        var path = Path.Combine(config["FileStorage:Root"] ?? AppContext.BaseDirectory, "_spec", "drafts", $"{projectId}_{fileId}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SummaryDraft>(File.ReadAllText(path), Json) : null;
    }
}
