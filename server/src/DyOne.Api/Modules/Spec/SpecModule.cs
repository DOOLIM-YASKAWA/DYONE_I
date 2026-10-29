using System.Collections.Concurrent;
using System.Security.Claims;
using DyOne.Api.Modules.Notifications;
using DyOne.Api.Modules.Projects;
using DyOne.Data;
using DyOne.Spec.Draft;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Api.Modules.Spec;

public record SpecFileDto(long Id, string FileName, DateTimeOffset UploadedAt, long SizeBytes);
public record LibFileDto(string FileName, DateTime Modified, int Revisions);
public record SimilarDto(
    string Id, string ProjectFolder, string? ProjectCode, string CustomerFolder, string Year,
    LibFileDto? Customer, LibFileDto? Summary, int MinutesCount, bool HasSheet,
    double Score, string MatchedBy, List<MatchReason> Reasons, long? DyoneProjectId, bool SameProject);
public record SpecAnalysisDto(
    SpecFileDto? File, List<SpecFileDto> Files, SpecFeatures? Features, Dictionary<string, SpecValue>? Values,
    int LibraryCount, int LibraryWithSheet, int LibraryWithCustomer, DateTimeOffset? LibraryBuiltAt, bool LibraryRunning,
    List<SimilarDto> Similar, SimilarDto? Template, List<LibFileDto> TemplateMinutes, string? Error);

/// <summary>
/// 사양서 분석: 이번 고객 사양서(프로젝트 파일, 분류 '사양서')
///  → NAS 과거 프로젝트 중 비슷한 고객 사양서를 받았던 프로젝트 찾기(고객 사양서끼리 비교, 고객 사양서가 없으면 사양정리로).
/// 사양정리 초안은 SpecDraftModule(값은 이번 사양서에서만, 모델은 과거 이력 제안). NAS와 업로드 파일은 읽기만 한다.
/// </summary>
public static class SpecModule
{
    // 업로드 파일 추출 결과 캐시(파일 id + 저장 경로 + 크기). 49쪽 PDF도 수 초라 메모리 캐시면 충분.
    private static readonly ConcurrentDictionary<string, Extraction> Extracted = new();

    // PDF가 아닌 사양서를 PDF로 바꿀 때 쓰는 Python(Spec:PythonPath)·LibreOffice(Spec:SofficePath, 없으면 Office COM만)
    private static string? _python, _soffice;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PdfLocks = new();

    public static void MapSpec(this IEndpointRouteBuilder app)
    {
        var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
        _python = cfg["Spec:PythonPath"] is { Length: > 0 } py ? py : null;
        _soffice = cfg["Spec:SofficePath"];
        var g = app.MapGroup("/api").WithTags("Spec").RequireAuthorization();

        g.MapGet("/spec/library/status", (SpecLibrary lib) => Results.Ok(new
        {
            count = lib.Docs.Count,
            withSheet = lib.Docs.Count(d => d.HasSheet),
            withCustomer = lib.Docs.Count(d => d.Customer is not null),
            builtAt = lib.BuiltAt,
            running = lib.Running,
            done = lib.Done,
            total = lib.Total,
            lastError = lib.LastError,
            roots = lib.Roots,
        }));

        g.MapPost("/spec/library/refresh", async (SpecLibrary lib, ClaimsPrincipal p, AppAudience audience, CancellationToken ct) =>
            await SpecDraftModule.IsAdminAsync(p, audience, ct) ? Results.Ok(new { started = lib.StartRefresh() })
                : Results.Json(new { message = "사양서 라이브러리 다시 읽기는 관리자만 할 수 있습니다." }, statusCode: 403));

        // 과거 프로젝트 원본 내려받기(NAS 읽기 전용): kind = summary(사양정리) | customer(고객 사양서) | minute(회의록, n번째)
        g.MapGet("/spec/library/{id}/file", (string id, string? kind, int? n, SpecLibrary lib) =>
        {
            var doc = lib.Find(id);
            var f = kind switch
            {
                "customer" => doc?.Customer,
                "minute" => doc?.Minutes.ElementAtOrDefault(n ?? 0),
                _ => doc?.Summary,
            };
            if (f is null || !File.Exists(f.Path)) return Results.NotFound(new { message = "파일을 찾을 수 없습니다." });
            return Results.File(f.Path, "application/octet-stream", f.FileName);
        });

        g.MapGet("/projects/{projectId:long}/spec-analysis", async (long projectId, long? fileId, string? template,
            DyOneDbContext db, FileStorage storage, SpecExtractor extractor, SpecLibrary lib, CancellationToken ct) =>
        {
            var project = await db.Projects.Where(p => p.Id == projectId)
                .Select(p => new { p.Code, p.Name, p.PartnerName }).FirstOrDefaultAsync(ct);
            if (project is null) return Results.NotFound();

            var files = await SpecFilesAsync(db, projectId, ct);
            var fileDtos = files.Select(f => new SpecFileDto(f.Id, f.FileName, f.UploadedAt, f.SizeBytes)).ToList();
            var file = fileId is { } fid ? files.FirstOrDefault(f => f.Id == fid) : files.FirstOrDefault();
            var empty = new SpecAnalysisDto(null, fileDtos, null, null,
                lib.Docs.Count, lib.Docs.Count(d => d.HasSheet), lib.Docs.Count(d => d.Customer is not null), lib.BuiltAt, lib.Running,
                [], null, [], null);
            if (file is null) return Results.Ok(empty);
            var fileDto = new SpecFileDto(file.Id, file.FileName, file.UploadedAt, file.SizeBytes);

            Extraction ex;
            try { ex = await ExtractAsync(file, storage, extractor, ct); }
            catch (Exception e) { return Results.Ok(empty with { File = fileDto, Error = $"사양서를 읽지 못했습니다: {e.Message}" }); }

            // 특징: 프로젝트 이름·발주처·파일 이름·첫 쪽이 '무엇에 관한 사양인가'(head), 나머지는 본문(body)
            var firstPages = string.Join(" ", ex.Blocks.Where(b => b.UnitIndex <= 2).Select(b => b.Text).Take(40));
            var features = SpecFeatures.From($"{project.PartnerName} {project.Name} {file.FileName} {firstPages}",
                string.Join("\n", ex.Blocks.Select(b => b.Text)));
            var values = SpecValues.Extract(ex.Blocks);

            var codes = lib.Docs.Where(d => d.ProjectCode is not null).Select(d => d.ProjectCode!).Distinct().ToList();
            var idByCode = (await db.Projects.Where(p => codes.Contains(p.Code)).Select(p => new { p.Code, p.Id }).ToListAsync(ct))
                .GroupBy(p => p.Code).ToDictionary(g2 => g2.Key, g2 => g2.First().Id);

            SimilarDto ToDto(LibraryDoc d, (double score, string by, List<MatchReason> reasons) m) => new(
                d.Id, d.ProjectFolder, d.ProjectCode, d.CustomerFolder, d.Year,
                d.Customer is { } c ? new LibFileDto(c.FileName, c.Modified, c.Revisions) : null,
                d.Summary is { } s ? new LibFileDto(s.FileName, s.Modified, s.Revisions) : null,
                d.Minutes.Count, d.HasSheet, m.score, m.by, m.reasons,
                d.ProjectCode is { } code && idByCode.TryGetValue(code, out var pid) ? pid : null,
                d.ProjectCode == project.Code);

            var similar = lib.Docs
                .Select(d => (d, m: Match(features, d)))
                .Where(x => x.m.score > 0)
                // 같은 점수면 사양정리가 있는(본보기로 쓸 수 있는) 프로젝트, 그다음 최근 프로젝트
                .OrderByDescending(x => x.m.score).ThenByDescending(x => x.d.HasSheet).ThenByDescending(x => x.d.Summary?.Modified ?? x.d.Customer?.Modified)
                .Take(10)
                .Select(x => ToDto(x.d, x.m))
                .ToList();

            // 본보기: 고른 것 → 없으면 이 프로젝트가 아니면서 사양정리 표가 있는 가장 비슷한 프로젝트
            var picked = template is not null ? lib.Find(template) : null;
            var templateDto = picked is not null
                ? similar.FirstOrDefault(s => s.Id == picked.Id) ?? ToDto(picked, Match(features, picked))
                : similar.FirstOrDefault(s => !s.SameProject && s.HasSheet) ?? similar.FirstOrDefault(s => s.HasSheet);
            picked ??= templateDto is not null ? lib.Find(templateDto.Id) : null;

            return Results.Ok(empty with
            {
                File = fileDto, Features = features, Values = values, Similar = similar, Template = templateDto,
                TemplateMinutes = picked?.Minutes.Select(m => new LibFileDto(m.FileName, m.Modified, 1)).ToList() ?? [],
            });
        });
    }

    /// <summary>
    /// 비슷한 정도. 과거 고객 사양서가 있으면 고객 사양서끼리(같은 고객은 같은 양식이라 가장 정확), 없으면 과거 사양정리와 비교한다.
    /// 사양정리와의 비교는 글 성격이 달라 0.9를 곱한다. 둘 다 있으면 높은 쪽.
    /// </summary>
    internal static (double score, string by, List<MatchReason> reasons) Match(SpecFeatures f, LibraryDoc d)
    {
        (double score, List<MatchReason> reasons) none = (0, []);
        var byCustomer = d.CustomerFeatures is { } cf ? SpecMatch.Score(f, cf) : none;
        var bySummary = d.HasSheet ? SpecMatch.Score(f, d.Features) : none;
        return byCustomer.score >= bySummary.score * 0.9
            ? (byCustomer.score, "customer", byCustomer.reasons)
            : (Math.Round(bySummary.score * 0.9, 1), "summary", bySummary.reasons);
    }

    internal sealed record SpecFileRow(long Id, string FileName, string StoredName, DateTimeOffset UploadedAt, long SizeBytes);

    /// <summary>이 프로젝트의 고객 사양서 = 분류 '사양서'(spec)로 올린, 읽을 수 있는 형식의 파일(최근 순)</summary>
    internal static async Task<List<SpecFileRow>> SpecFilesAsync(DyOneDbContext db, long projectId, CancellationToken ct)
    {
        var specCat = await db.Codes.Where(c => c.GroupKey == "document_category" && c.Code == "spec").Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        var rows = await db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.IsActive && f.CategoryId == specCat)
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new SpecFileRow(f.Id, f.FileName, f.StoredName, f.UploadedAt, f.SizeBytes))
            .ToListAsync(ct);
        return rows.Where(f => SpecExtractor.Supported.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())).ToList();
    }

    internal static async Task<Extraction> ExtractAsync(long fileId, string fileName, string storedName, long size,
        FileStorage storage, SpecExtractor extractor, CancellationToken ct)
    {
        var key = $"{fileId}|{storedName}|{size}";
        if (Extracted.TryGetValue(key, out var ex)) return ex;
        // PDF가 아닌 사양서는 PDF로 바꾼 것을 읽는다 — 쪽 번호·쪽 그림·하이라이트가 PDF 사양서와 같은 모양(2026-10-27 사용자)
        var pdf = await ViewPdfAsync(fileName, storedName, storage, ct);
        ex = pdf is not null && !IsPdfName(fileName)
            ? extractor.Extract(await File.ReadAllBytesAsync(pdf, ct), Path.ChangeExtension(fileName, ".pdf"))
            : extractor.Extract(await File.ReadAllBytesAsync(storage.FullPath(storedName), ct), fileName);
        Extracted[key] = ex;
        return ex;
    }

    internal static Task<Extraction> ExtractAsync(SpecFileRow f, FileStorage storage, SpecExtractor extractor, CancellationToken ct) =>
        ExtractAsync(f.Id, f.FileName, f.StoredName, f.SizeBytes, storage, extractor, ct);

    internal static bool IsPdfName(string fileName) => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 화면·초안이 쓰는 PDF 경로: PDF면 원본, pptx·xlsx·docx 등은 PDF로 바꿔 저장 파일 옆에 둔 것({저장 이름}.view.pdf, 한 번만 바꾼다).
    /// 바꿀 수 없으면(Office·LibreOffice 없음, txt) null — 그때는 원본 글만 읽는다.
    /// </summary>
    internal static async Task<string?> ViewPdfAsync(string fileName, string storedName, FileStorage storage, CancellationToken ct)
    {
        var src = storage.FullPath(storedName);
        if (IsPdfName(fileName)) return src;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (_python is null || ext is not (".pptx" or ".ppt" or ".xlsx" or ".xlsm" or ".xls" or ".docx" or ".doc")) return null;
        var view = src + ".view.pdf";
        if (File.Exists(view)) return view;
        var gate = PdfLocks.GetOrAdd(view, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(view)) return view;
            // Office는 확장자로 형식을 알아보므로 원래 확장자를 붙인 임시 사본으로 바꾼다
            var tmp = Path.Combine(Path.GetTempPath(), $"spec-{Guid.NewGuid():N}{ext}");
            File.Copy(src, tmp);
            try
            {
                var (exit, _, _) = await SpecPython.RunAsync(_python, "spec_topdf.py", [tmp, view + ".tmp", _soffice ?? ""], TimeSpan.FromMinutes(4), ct);
                if (exit != 0 || !File.Exists(view + ".tmp")) return null;
                File.Move(view + ".tmp", view, true);
                return view;
            }
            finally { File.Delete(tmp); }
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException) { return null; }
        finally { gate.Release(); }
    }

    internal static Task<string?> ViewPdfAsync(SpecFileRow f, FileStorage storage, CancellationToken ct) => ViewPdfAsync(f.FileName, f.StoredName, storage, ct);
}
