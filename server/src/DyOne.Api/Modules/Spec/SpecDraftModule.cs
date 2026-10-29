using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DyOne.Api.Modules.Notifications;
using DyOne.Api.Modules.Projects;
using DyOne.Api.Platform;
using DyOne.Data;
using DyOne.Spec.Draft;
using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Spec;

public record DecisionRequest(string SlotId, string Kind, string? RecordId, string? Text);
public record CellEditRequest(string Key, string Field, int? Index, string? Text);
/// <summary>스프레드시트 편집 저장: 줄 순서대로 이름·공정 칸 글·병합 폭(덮인 칸 0)·기타 사항</summary>
public record SheetRow(string? Key, List<string?>? Cells, List<int>? Spans, string? Note);
public record SheetSaveRequest(List<SheetRow> Rows);
public record PageInfo(int Page, int Chars, string Head);
public record CompareRow(string Key, string ThisValue, string PastValue, string PastNote);
public record SimilarCompareDto(string Id, string ProjectFolder, string Year, string? SummaryFile, List<CompareRow> Rows);
public record SpecDraftDto(
    SummaryDraft? Draft, bool AiConfigured, bool IsPdf,
    int EquipmentCount, DateTimeOffset? EquipmentBuiltAt, bool EquipmentRunning,
    List<PageInfo> Pages, List<int> UnusedPages, SimilarCompareDto? Similar);

/// <summary>
/// 사양정리 초안(영업부 V5 양식): 고객 사양서 → 초안. 값은 이번 사양서에서만(근거 쪽),
/// 모델이 '기설치와 동일'이거나 정해지지 않은 칸은 설비 이력(SpecEquipment)에서 가장 비슷한 과거 모델을 제안하고 사용자가 채택한다.
/// 초안은 FileStorage:Root\_spec\drafts\{프로젝트}_{파일}.json에 둔다(DB 변경 없음). pptx는 python/render_spec_summary.py로 만든다.
/// </summary>
public static class SpecDraftModule
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    // 2장 표 줄 → 과거 사양정리 표 항목(SpecItems) 키
    private static readonly Dictionary<string, string> RowToItem = new()
    {
        ["Robot"] = "robot", ["Booth size"] = "booth", ["Basic Condition"] = "basic", ["Body Size"] = "body",
        ["Utility"] = "utility", ["Atomizer"] = "atomizer", ["Color(CCV)"] = "color", ["FGP"] = "fgp",
        ["Panel"] = "panel", ["System"] = "system", ["Training"] = "training", ["Schedule"] = "schedule", ["Stand by"] = "standby",
    };

    public static void MapSpecDraft(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("SpecDraft").RequireAuthorization();

        // 설비 이력은 Worker spec-index가 매일 새로 만든다. 즉시 다시 읽기는 관리자(AppAudience '*')만.
        g.MapGet("/spec/equipment/status", async (SpecEquipment eq, ClaimsPrincipal p, AppAudience audience, CancellationToken ct) => Results.Ok(new
        {
            count = eq.Units.Count, projects = eq.Projects, builtAt = eq.BuiltAt,
            running = eq.Running, done = eq.Done, total = eq.Total, lastError = eq.LastError,
            canRefresh = await IsAdminAsync(p, audience, ct),
        }));
        g.MapPost("/spec/equipment/refresh", async (SpecEquipment eq, ClaimsPrincipal p, AppAudience audience, CancellationToken ct) =>
            await IsAdminAsync(p, audience, ct) ? Results.Ok(new { started = eq.StartRefresh() })
                : Results.Json(new { message = "설비 이력 다시 읽기는 관리자만 할 수 있습니다." }, statusCode: 403));

        // 후보의 원본 파일(NAS, 읽기 전용) — 색인에 있는 파일만
        g.MapGet("/spec/equipment/{id}/file", (string id, SpecEquipment eq) =>
            eq.Find(id) is { } u && File.Exists(u.FilePath)
                ? Results.File(u.FilePath, "application/octet-stream", u.FileName)
                : Results.NotFound(new { message = "파일을 찾을 수 없습니다." }));

        g.MapGet("/projects/{projectId:long}/spec-draft", async (long projectId, long fileId, DyOneDbContext db, FileStorage storage,
            SpecExtractor extractor, SpecLibrary lib, SpecEquipment eq, SpecAi ai, IConfiguration config, CancellationToken ct) =>
        {
            var ctxRes = await LoadAsync(projectId, fileId, db, storage, extractor, ct);
            if (ctxRes is null) return Results.NotFound(new { message = "사양서를 찾을 수 없습니다." });
            var (project, file, ex) = ctxRes.Value;
            var draft = Read(config, projectId, fileId);
            // 기본 사양 기능 전에 만든 초안: 정하지 않은 자리에 1순위를 넣어 저장
            if (draft is not null && draft.Rows.Any(r => !r.Edited && r.Slots.Any(s => s.Decision is null && s.Status != "given" && s.Candidates.Count > 0)))
            {
                var gate = Locks.GetOrAdd($"{projectId}_{fileId}", _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    draft = Read(config, projectId, fileId)!;
                    DraftEdit.ApplyDefaults(draft);
                    await WriteAsync(config, projectId, fileId, draft, ct);
                }
                finally { gate.Release(); }
            }
            return Results.Ok(Dto(draft, ai, eq, file, ex, lib, project.Code, project.Name, project.PartnerName));
        });

        // 초안 만들기(다시 만들기). AI가 연결돼 있으면 AI, 아니면 규칙. 이전 초안의 모델 결정은 같은 자리면 유지.
        g.MapPost("/projects/{projectId:long}/spec-draft", async (long projectId, long fileId, DyOneDbContext db, FileStorage storage,
            SpecExtractor extractor, SpecLibrary lib, SpecEquipment eq, SpecAi ai, IConfiguration config, ILoggerFactory lf, CancellationToken ct) =>
        {
            var ctxRes = await LoadAsync(projectId, fileId, db, storage, extractor, ct);
            if (ctxRes is null) return Results.NotFound(new { message = "사양서를 찾을 수 없습니다." });
            var (project, file, ex) = ctxRes.Value;
            var head = $"{project.PartnerName} {project.Name} {file.FileName} " + string.Join(" ", ex.Blocks.Where(b => b.UnitIndex <= 2).Select(b => b.Text).Take(30));
            var context = EquipmentIndex.ContextOf(head);
            // PDF '보이는 줄': 표 인식이 여러 줄을 한 칸으로 합쳐 수량이 사라진 경우(판넬별 SET) 같은 줄의 값을 찾는다
            var viewPdf = await SpecModule.ViewPdfAsync(file, storage, ct);
            var visible = viewPdf is not null
                ? extractor.ExtractPdfLines(await File.ReadAllBytesAsync(viewPdf, ct), Path.ChangeExtension(file.FileName, ".pdf")) : null;

            SummaryDraft draft;
            string? aiError = null;
            if (ai.IsConfigured)
            {
                try { draft = await ai.DraftAsync(ex.Blocks, project.Name, context, ct); }
                catch (Exception e)
                {
                    lf.CreateLogger("SpecDraft").LogWarning(e, "사내 AI 초안 실패 → 규칙 초안");
                    aiError = e.Message;
                    draft = DraftBuilder.FromRules(ex.Blocks, project.Name, context, visible);
                }
            }
            else draft = DraftBuilder.FromRules(ex.Blocks, project.Name, context, visible);
            if (aiError is not null) draft.EngineNote = $"사내 AI 오류({aiError})로 규칙 초안을 만들었습니다. " + draft.EngineNote;

            draft.FileId = file.Id;
            draft.FileName = file.FileName;
            // 표 위 그림(영업부 사양정리처럼): 사양서 레이아웃·전체 시스템 구성도 영역. AI가 고른 쪽이 없으면 규칙으로 찾는다
            if (draft.LayoutImage is null && viewPdf is not null && config["Spec:PythonPath"] is { Length: > 0 } lpy)
            {
                try
                {
                    var (lexit, lout, _) = await SpecPython.RunAsync(lpy, "spec_layout.py", [viewPdf], TimeSpan.FromSeconds(60), ct);
                    if (lexit == 0 && JsonNode.Parse(Encoding.UTF8.GetString(lout)) is JsonObject lj && lj["page"] is { } lp && lj["clip"] is JsonArray lc)
                        draft.LayoutImage = new PageClip((int)lp, lc.Select(v => (double)v!).ToArray());
                }
                catch (Exception e) when (e is TimeoutException or JsonException) { /* 그림 없이 */ }
            }
            // 3장 Master schedule: 사양서 일정표·간트 쪽 영역(AI가 못 찾았을 때 규칙으로 — 표지·목차 1쪽 빼고 첨부까지)
            if (draft.ScheduleImage is null && viewPdf is not null && config["Spec:PythonPath"] is { Length: > 0 } spy)
            {
                try
                {
                    var (sexit, sout, _) = await SpecPython.RunAsync(spy, "spec_layout.py", [viewPdf, "schedule"], TimeSpan.FromSeconds(60), ct);
                    if (sexit == 0 && JsonNode.Parse(Encoding.UTF8.GetString(sout)) is JsonObject sj && sj["page"] is { } sp && sj["clip"] is JsonArray sc)
                        draft.ScheduleImage = new PageClip((int)sp, sc.Select(v => (double)v!).ToArray());
                }
                catch (Exception e) when (e is TimeoutException or JsonException) { /* 그림 없이 */ }
            }
            // AI 초안도 Basic Condition에 사이클타임이 빠졌으면 최대 UPH로 계산해 덧붙인다(규칙 초안은 FromRules에서 이미 채움)
            if (draft.Engine == "ai") BasicCondition.Fill(draft, ex.Blocks, onlyMissing: true);
            // 모델(로봇·오프너·도장기·FGP): 사양서 지정 → 기설치 이력 → 같은 공장 이력 → 경향 규칙 (이번 프로젝트 자신의 기록은 뺌)
            // 이번 프로젝트의 NAS 폴더 = 올린 사양서와 같은 이름의 고객 사양서가 있는 곳(DY ONE 번호와 NAS 폴더 번호가 다르거나 폴더에 번호가 없어도 뺀다)
            var specStem = SpecStem(file.FileName);
            var selfDirs = lib.Docs.Where(d => d.Customer is { } c && SpecStem(c.FileName) == specStem)
                .Select(d => d.ProjectDir).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var conditions = ModelPicker.ReadConditions(ex.Blocks, project.Name, file.FileName, eq.Units, project.Code, selfDirs);
            var picks = ModelPicker.Pick(ex.Blocks, conditions, eq.Units, project.Code, selfDirs);
            var previous = Read(config, projectId, fileId);
            DraftBuilder.ApplyPicks(draft, conditions, picks, previous);
            // 여러 공정 초안(공정별 설비사양 표): 공정 열마다 조건을 따로 잡아 로봇·도장기·FGP 모델을 그 열에만
            if (draft.ProcessTable)
                DraftBuilder.ApplyColumnPicks(draft, ColumnPicks.Pick(draft, ex.Blocks, conditions, eq.Units, project.Code, selfDirs, lib.Docs), previous);
            // 사양서에 없는 칸(Panel·System·Training·Stand by·Utility): 과거 사양정리의 같은 칸을 참고로(값은 만들지 않음)
            RowHints.Apply(draft, lib.Docs, conditions, project.Code, selfDirs, previous);
            // 표에서 고친 줄은 고친 글 그대로, 나머지 줄은 이전에 채택한 모델을 다시 넣는다
            DraftEdit.Restore(draft, previous);
            // 아직 정하지 않은 자리는 1순위 후보를 기본 사양으로 칸에 넣는다(화면 '참고'에서 바꿈)
            DraftEdit.ApplyDefaults(draft);
            await WriteAsync(config, projectId, fileId, draft, ct);
            return Results.Ok(Dto(draft, ai, eq, file, ex, lib, project.Code, project.Name, project.PartnerName));
        });

        // 모델 자리 결정: candidate(후보 채택) | custom(직접 입력) | none(선정 필요로 둠) | reset(결정 취소)
        g.MapPut("/projects/{projectId:long}/spec-draft/decision", async (long projectId, long fileId, DecisionRequest req,
            ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            var gate = Locks.GetOrAdd($"{projectId}_{fileId}", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var draft = Read(config, projectId, fileId);
                var slot = draft?.Rows.SelectMany(r => r.Slots).FirstOrDefault(s => s.Id == req.SlotId);
                if (draft is null || slot is null) return Results.NotFound(new { message = "초안 또는 모델 자리를 찾을 수 없습니다." });
                if (req.Kind == "candidate" && slot.Candidates.All(c => c.RecordId != req.RecordId))
                    return Results.BadRequest(new { message = "후보를 찾을 수 없습니다." });
                if (req.Kind == "custom" && string.IsNullOrWhiteSpace(req.Text))
                    return Results.BadRequest(new { message = "모델을 입력해 주세요." });
                var decision = req.Kind switch
                {
                    "candidate" or "custom" or "none" => new SlotDecision(req.Kind, req.RecordId, req.Text?.Trim(),
                        user.FindFirstValue(ClaimTypes.Name) ?? "", DateTimeOffset.Now),
                    _ => null,
                };
                // 채택·직접 입력한 모델은 그 칸 값에 넣는다(로봇은 역할 뒤 "스프레이 로봇 MPX3500 * 2ea"). 바꾸거나 취소하면 넣은 글을 뺀다.
                var model = req.Kind switch
                {
                    "candidate" => slot.Candidates.First(c => c.RecordId == req.RecordId).Model,
                    "custom" => req.Text,
                    _ => null,
                };
                if (DraftEdit.RowOf(draft, slot) is { } row)
                {
                    slot.Decision = DraftEdit.Decide(row, slot, decision, model);
                    // 결정 취소 → 기본 사양(1순위)으로 돌아간다
                    if (!row.Edited) DraftEdit.ApplyDefault(row, slot);
                }
                else slot.Decision = decision;
                await WriteAsync(config, projectId, fileId, draft, ct);
                return Results.Ok(draft);
            }
            finally { gate.Release(); }
        });

        // 표에서 직접 고치기: field = value | note | split(index = 공정 순서). 고친 줄은 초안을 다시 만들어도 그대로 둔다.
        g.MapPut("/projects/{projectId:long}/spec-draft/cell", async (long projectId, long fileId, CellEditRequest req,
            ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            var gate = Locks.GetOrAdd($"{projectId}_{fileId}", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var draft = Read(config, projectId, fileId);
                var row = draft?.Rows.FirstOrDefault(r => r.Key == req.Key);
                if (draft is null || row is null) return Results.NotFound(new { message = "초안 또는 칸을 찾을 수 없습니다." });
                // 행 추가(이 줄 아래, text = 새 줄 이름)·행 삭제(추가한 줄은 지우고, 양식 줄은 표에서 뺌)
                if (req.Field == "addrow")
                {
                    var name = (req.Text ?? "").Trim();
                    if (name.Length == 0 || draft.Rows.Any(r => r.Key == name)) return Results.BadRequest(new { message = "새 줄 이름이 비었거나 이미 있는 이름입니다." });
                    var n = draft.Processes.Count >= 2 ? draft.Processes.Count : 0;
                    draft.Rows.Insert(draft.Rows.IndexOf(row) + 1, new DraftRow
                    {
                        Key = name, Custom = true, Edited = true, EditedBy = user.FindFirstValue(ClaimTypes.Name), EditedAt = DateTimeOffset.Now,
                        Split = n > 0 ? [.. Enumerable.Repeat("", n)] : [],
                    });
                }
                else if (req.Field == "deleterow")
                {
                    if (row.Custom) draft.Rows.Remove(row); else row.Hidden = true;
                }
                else if (!DraftEdit.SetCell(row, req.Field, req.Index, req.Text ?? "", user.FindFirstValue(ClaimTypes.Name) ?? ""))
                    return Results.BadRequest(new { message = "고칠 수 없는 칸입니다." });
                await WriteAsync(config, projectId, fileId, draft, ct);
                return Results.Ok(draft);
            }
            finally { gate.Release(); }
        });

        // 스프레드시트 편집 저장: 화면 시트(행 순서·글·공정 칸 병합·기타 사항)를 그대로 초안 줄로. 바뀐 줄만 '직접 고침',
        // 시트에 없는 줄은 추가한 줄이면 지우고 양식 줄이면 표에서 뺀다. 모델 자리·근거·참고 사항은 같은 이름 줄에서 이어받는다.
        g.MapPut("/projects/{projectId:long}/spec-draft/sheet", async (long projectId, long fileId, SheetSaveRequest req,
            ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            var gate = Locks.GetOrAdd($"{projectId}_{fileId}", _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var draft = Read(config, projectId, fileId);
                if (draft is null) return Results.NotFound(new { message = "초안을 찾을 수 없습니다." });
                var by = user.FindFirstValue(ClaimTypes.Name) ?? "";
                var n = draft.Processes.Count >= 2 ? draft.Processes.Count : 1;
                var next = new List<DraftRow>();
                foreach (var s in req.Rows)
                {
                    var key = (s.Key ?? "").Trim();
                    if (key.Length == 0 || next.Any(r => r.Key == key)) continue;
                    var row = draft.Rows.FirstOrDefault(r => r.Key == key) ?? new DraftRow { Key = key, Custom = true };
                    var cells = (s.Cells ?? []).Take(n).Select(c => (c ?? "").Trim()).ToList();
                    while (cells.Count < n) cells.Add("");
                    var spans = (s.Spans ?? []).Take(n).ToList();
                    while (spans.Count < n) spans.Add(1);
                    // 모든 공정 칸이 한 칸으로 합쳐졌거나 공정이 하나면 한 칸 글, 아니면 공정 칸(병합 폭 포함)
                    string value; List<string> split; List<int> span;
                    if (n == 1 || spans[0] >= n) { value = cells[0]; split = []; span = []; }
                    else { value = string.Join(" / ", cells.Where(c => c.Length > 0).Distinct()); split = cells; span = spans.Any(x => x != 1) ? spans : []; }
                    var note = (s.Note ?? "").Trim();
                    // 시트는 꾸밈 표시(**굵게** {{파랑}} …) 없이 글만 보여 주므로 비교도 글만 — 안 고친 줄은 그대로(꾸밈 유지)
                    static string Plain(string x) => Regex.Replace(x ?? "", @"\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>", "").Trim();
                    var changed = row.Custom && !draft.Rows.Contains(row)
                        || (split.Count == 0 ? row.Split.Count > 0 || Plain(row.Value) != value
                            : !row.Split.Select(Plain).SequenceEqual(split) || !DraftEdit.Spans(row).SequenceEqual(span.Count > 0 ? span : Enumerable.Repeat(1, n)))
                        || Plain(row.Note) != note || row.Hidden;
                    if (changed)
                    {
                        row.Value = value; row.Split = split; row.Span = span; row.Note = note; row.Hidden = false;
                        row.Merged = n > 1 && split.Count == 0 && draft.Rows.FirstOrDefault(r => r.Key == key)?.Split.Count > 0;
                        row.Edited = true; row.EditedBy = by; row.EditedAt = DateTimeOffset.Now;
                    }
                    next.Add(row);
                }
                // 시트에서 지운 줄: 추가한 줄은 지우고, 양식 줄은 '표에서 뺀 칸'으로
                foreach (var old in draft.Rows.Where(r => next.All(x => x.Key != r.Key) && !r.Custom))
                {
                    old.Hidden = true;
                    next.Add(old);
                }
                draft.Rows = next;
                await WriteAsync(config, projectId, fileId, draft, ct);
                return Results.Ok(draft);
            }
            finally { gate.Release(); }
        });

        // 사양서 원문 한 쪽(그림). PDF만. 다른 형식은 쪽 글을 돌려준다.
        // dpi: 근거 패널은 기본 110, 크게 보기는 더 높게(72~220 사이로 제한)
        // mark: 그 쪽에서 노란색으로 칠할 글(칸 값·원문 조각, 여러 개) — 쪽 번호를 누른 칸의 근거 자리
        g.MapGet("/projects/{projectId:long}/spec-draft/page", async (long projectId, long fileId, int page, int? dpi, string[]? mark, DyOneDbContext db,
            FileStorage storage, SpecExtractor extractor, IConfiguration config, CancellationToken ct) =>
        {
            var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
            if (file is null) return Results.NotFound();
            var viewPdf = await SpecModule.ViewPdfAsync(file, storage, ct);
            if (viewPdf is null || config["Spec:PythonPath"] is not { Length: > 0 } py)
            {
                var ex = await SpecModule.ExtractAsync(file, storage, extractor, ct);
                return Results.Ok(new { page, lines = ex.Blocks.Where(b => b.UnitIndex == page).Select(b => b.Text).ToList() });
            }
            var marks = JsonSerializer.Serialize((mark ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Take(40).ToArray());
            var (exit, png, _) = await SpecPython.RunAsync(py, "spec_page.py", [viewPdf, page.ToString(), Math.Clamp(dpi ?? 110, 72, 220).ToString(), marks], TimeSpan.FromSeconds(60), ct);
            return exit == 0 ? Results.File(png, "image/png") : Results.NotFound();
        });

        // 표 위 그림: 초안에 정해 둔 사양서 레이아웃 영역(PageClip)을 잘라 PNG로 — pptx 2장 표 위에 붙는 그림과 같다
        g.MapGet("/projects/{projectId:long}/spec-draft/layout", async (long projectId, long fileId, DyOneDbContext db,
            FileStorage storage, IConfiguration config, CancellationToken ct) =>
        {
            var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
            if (file is null || Read(config, projectId, fileId)?.LayoutImage is not { } li || config["Spec:PythonPath"] is not { Length: > 0 } py) return Results.NotFound();
            if (await SpecModule.ViewPdfAsync(file, storage, ct) is not { } pdf) return Results.NotFound();
            var (exit, png, _) = await SpecPython.RunAsync(py, "spec_page.py", [pdf, li.Page.ToString(), "150", "", JsonSerializer.Serialize(li.Clip)], TimeSpan.FromSeconds(60), ct);
            return exit == 0 ? Results.File(png, "image/png") : Results.NotFound();
        });

        g.MapGet("/projects/{projectId:long}/spec-draft/pptx", async (long projectId, long fileId, DyOneDbContext db, FileStorage storage,
            IConfiguration config, CancellationToken ct) =>
        {
            var draft = Read(config, projectId, fileId);
            var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
            var project = await db.Projects.Where(p => p.Id == projectId).Select(p => new { p.Code, p.Name }).FirstOrDefaultAsync(ct);
            if (draft is null || file is null || project is null) return Results.NotFound(new { message = "초안을 먼저 만들어 주세요." });
            if (config["Spec:PythonPath"] is not { Length: > 0 } py) return Results.BadRequest(new { message = "Spec:PythonPath(python-pptx·PyMuPDF)가 설정되지 않았습니다." });

            var tmp = Directory.CreateTempSubdirectory("spec-draft-");
            try
            {
                var json = DraftBuilder.ToRenderJson(draft, await SpecModule.ViewPdfAsync(file, storage, ct));
                var jsonPath = Path.Combine(tmp.FullName, "draft.json");
                var outPath = Path.Combine(tmp.FullName, "draft.pptx");
                await File.WriteAllTextAsync(jsonPath, json.ToJsonString(), ct);
                var (exit, _, err) = await SpecPython.RunAsync(py, "render_spec_summary.py",
                    [SpecPython.Template("spec_summary_v5.pptx"), jsonPath, outPath, tmp.FullName], TimeSpan.FromMinutes(2), ct);
                if (exit != 0 || !File.Exists(outPath))
                    return Results.Problem($"pptx를 만들지 못했습니다: {err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
                var name = $"[{project.Code}] {project.Name} 사양정리 초안.pptx";
                foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                return Results.File(await File.ReadAllBytesAsync(outPath, ct), "application/vnd.openxmlformats-officedocument.presentationml.presentation", name);
            }
            finally { try { tmp.Delete(true); } catch { /* 임시 폴더 */ } }
        });
    }

    private static async Task<((string Code, string Name, string? PartnerName) project, SpecModule.SpecFileRow file, Extraction ex)?> LoadAsync(
        long projectId, long fileId, DyOneDbContext db, FileStorage storage, SpecExtractor extractor, CancellationToken ct)
    {
        var p = await db.Projects.Where(x => x.Id == projectId).Select(x => new { x.Code, x.Name, x.PartnerName }).FirstOrDefaultAsync(ct);
        var file = (await SpecModule.SpecFilesAsync(db, projectId, ct)).FirstOrDefault(f => f.Id == fileId);
        if (p is null || file is null) return null;
        var ex = await SpecModule.ExtractAsync(file, storage, extractor, ct);
        return ((p.Code, p.Name, p.PartnerName), file, ex);
    }

    private static SpecDraftDto Dto(SummaryDraft? draft, SpecAi ai, SpecEquipment eq, SpecModule.SpecFileRow file, Extraction ex,
        SpecLibrary lib, string code, string name, string? partner)
    {
        var pages = ex.Blocks.GroupBy(b => b.UnitIndex).OrderBy(g => g.Key)
            .Select(g => new PageInfo(g.Key, g.Sum(b => b.Text.Length), Head(g.Select(b => b.Text)))).ToList();
        var used = new HashSet<int>();
        if (draft is not null)
        {
            foreach (var r in draft.Rows) { used.UnionWith(r.Pages); used.UnionWith(r.Slots.Where(s => s.SpecPage is not null).Select(s => s.SpecPage!.Value)); }
            foreach (var c in draft.Etc.Concat(draft.Pcs)) used.UnionWith(c.Pages);
            if (draft.ScheduleImage is { } si) used.Add(si.Page);
            if (draft.LayoutImage is { } li) used.Add(li.Page);
        }
        // 초안에 안 쓰인 쪽(글이 충분한 쪽만): 빠뜨린 요구 사항이 없는지 사람이 훑어볼 곳
        var unused = draft is null ? [] : pages.Where(p => p.Chars >= 150 && !used.Contains(p.Page)).Select(p => p.Page).ToList();

        // 비슷한 과거 프로젝트(사양정리 표가 있는 것, 이 프로젝트 제외)와 줄별 값 비교
        SimilarCompareDto? similar = null;
        if (draft is not null && lib.Docs.Count > 0)
        {
            var body = string.Join("\n", ex.Blocks.Select(b => b.Text));
            var features = SpecFeatures.From($"{partner} {name} {file.FileName} " + string.Join(" ", ex.Blocks.Where(b => b.UnitIndex <= 2).Select(b => b.Text).Take(40)), body);
            var best = lib.Docs.Where(d => d.HasSheet && d.ProjectCode != code)
                .Select(d => (d, m: SpecModule.Match(features, d))).Where(x => x.m.score > 0)
                .OrderByDescending(x => x.m.score).ThenByDescending(x => x.d.Year).Select(x => x.d).FirstOrDefault();
            if (best is not null)
                similar = new SimilarCompareDto(best.Id, best.ProjectFolder, best.Year, best.Summary?.FileName,
                    draft.Rows.Select(r =>
                    {
                        var past = RowToItem.TryGetValue(r.Key, out var k) ? best.Rows.FirstOrDefault(x => x.Key == k) : null;
                        return new CompareRow(r.Key, r.Value, past?.Value ?? "", past?.Note ?? "");
                    }).ToList());
        }
        // PDF로 바꿔 읽은 사양서(pptx 등)도 쪽 그림으로 보인다
        return new SpecDraftDto(draft, ai.IsConfigured, IsPdf(ex.FileName), eq.Units.Count, eq.BuiltAt, eq.Running, pages, unused, similar);
    }

    private static string Head(IEnumerable<string> lines)
    {
        var t = Regex.Replace(string.Join(" ", lines.Take(6)), @"\s+", " ").Trim();
        return t.Length <= 80 ? t : t[..80] + "…";
    }

    /// <summary>사양서 이름에서 판(V4·V5)·날짜·괄호 표시를 뺀 줄기 — 같은 사양서의 다른 판을 같은 것으로 본다</summary>
    private static string SpecStem(string fileName) =>
        Regex.Replace(Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"[_\s-]*(?:V(?:ER)?\s*\d+|rev\s*\d+|\d{6,8}|\(\d+\)|\(원본\))", "", RegexOptions.IgnoreCase),
            @"[\s_\-\[\]()]+", "").ToLowerInvariant();

    private static bool IsPdf(string fileName) => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>NAS 색인(라이브러리·설비 이력) 즉시 다시 읽기 권한 — 관리자만. 평소에는 Worker spec-index가 매일 만든다.</summary>
    public static async Task<bool> IsAdminAsync(ClaimsPrincipal p, AppAudience audience, CancellationToken ct) =>
        long.TryParse(p.Current().Id, out var id) && await audience.IsAdminAsync(id, ct);

    private static string PathOf(IConfiguration config, long projectId, long fileId) =>
        Path.Combine(config["FileStorage:Root"] ?? AppContext.BaseDirectory, "_spec", "drafts", $"{projectId}_{fileId}.json");

    private static SummaryDraft? Read(IConfiguration config, long projectId, long fileId)
    {
        var path = PathOf(config, projectId, fileId);
        return File.Exists(path) ? JsonSerializer.Deserialize<SummaryDraft>(File.ReadAllText(path), Json) : null;
    }

    private static async Task WriteAsync(IConfiguration config, long projectId, long fileId, SummaryDraft draft, CancellationToken ct)
    {
        var path = PathOf(config, projectId, fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(draft, Json), ct);
        File.Move(tmp, path, overwrite: true);
    }
}
