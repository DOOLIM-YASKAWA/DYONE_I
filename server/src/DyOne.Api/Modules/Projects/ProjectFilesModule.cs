using System.Security.Claims;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.IO.Compression;

namespace DyOne.Api.Modules.Projects;

public record ProjectFileDto(
    long Id, long ProjectId, long? CategoryId, string? Category, string? CategoryCode,
    long? StageId, string? Stage, string FileName, string? ContentType, long SizeBytes,
    string? Description, string? UploadedByName, long? DepartmentId, string? Department, DateTimeOffset UploadedAt,
    string Share, bool Mine, long? ProjectCategoryId, Guid? BundleId, string? RelPath);

/// <param name="ProjectCategoryId">프로젝트별 직접 입력 종류. 있으면 CategoryId 대신 쓴다.</param>
public record FileCategoryUpdate(long? CategoryId, long? ProjectCategoryId = null);
public record FileStageUpdate(long? StageId);
public record FileShareUpdate(string Share);
public record ProjectCategoryDto(long Id, string Name);
public record ProjectCategoryCreate(string Name);
public record BundleCategoryUpdate(long? CategoryId, long? ProjectCategoryId);

/// <summary>자료·일지 공유 범위: me(본인) · dept(같은 부서) · project(모든 부서)</summary>
public static class Share
{
    public static readonly string[] All = ["me", "dept", "project"];
    public static bool Valid(string? s) => s is not null && All.Contains(s);

    /// <summary>보는 사람: 직원 id · 지금 부서 id · 협력사 여부</summary>
    public record Viewer(long? EmpId, long? DeptId, bool Partner);

    public static async Task<Viewer> ViewerAsync(ClaimsPrincipal p, DyOneDbContext db, CancellationToken ct)
    {
        if (p.IsPartner()) return new(null, null, true);
        long? emp = long.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;
        long? dept = emp is not null ? await db.Employees.Where(x => x.Id == emp).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct) : null;
        return new(emp, dept, false);
    }
}

/// <summary>
/// 프로젝트별 업로드 문서. 메타데이터는 project.project_file(dyone 자체 데이터), 실물은 FileStorage(NAS 등).
/// 그룹웨어/ERP와 무관하게 DY ONE에서 직접 쓴다(쓰기 연결은 단계 변경과 동일 패턴).
/// </summary>
public static class ProjectFilesModule
{
    // 읽기 전용 연결에서 read-only 옵션만 제거(사용자 편집 데이터 전용)
    static async Task<NpgsqlConnection> WriteAsync(IConfiguration config, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public static void MapProjectFiles(this IEndpointRouteBuilder app)
    {
        // 그룹 수준 권한은 걸지 않는다: 기본(Fallback)이 직원 전용이고, 목록·내려받기만 협력사 허용(.PartnerAllowed, 배정 프로젝트만)
        var g = app.MapGroup("/api/projects/{projectId:long}/files").WithTags("ProjectFiles");

        // 목록(활성 문서만, 최신 업로드 순). 분류·단계·업로더 이름을 함께 내려준다.
        // 공유 범위: project = 모두, dept = 올린 사람과 같은 부서(올릴 때 부서 스냅샷), me = 올린 사람만. 협력사는 project만.
        g.MapGet("", async (long projectId, ClaimsPrincipal principal, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            var rows = await (from f in db.ProjectFiles.Where(x => x.ProjectId == projectId && x.IsActive)
                              where f.Share == "project" || (!v.Partner && (f.UploadedBy == v.EmpId || (f.Share == "dept" && f.DepartmentId != null && f.DepartmentId == v.DeptId)))
                              join pc in db.ProjectCategories on f.ProjectCategoryId equals pc.Id into pcj
                              from pc in pcj.DefaultIfEmpty()
                              join c in db.Codes on f.CategoryId equals c.Id into cj
                              from c in cj.DefaultIfEmpty()
                              join s in db.Codes on f.StageId equals s.Id into sj
                              from s in sj.DefaultIfEmpty()
                              join e in db.Employees on f.UploadedBy equals e.Id into ej
                              from e in ej.DefaultIfEmpty()
                              join d in db.Departments on f.DepartmentId equals d.Id into dj
                              from d in dj.DefaultIfEmpty()
                              orderby f.UploadedAt descending
                              select new ProjectFileDto(
                                  f.Id, f.ProjectId, f.CategoryId, pc != null ? pc.Name : c != null ? c.Name : null, pc != null ? null : c != null ? c.Code : null,
                                  f.StageId, s != null ? s.Name : null, f.FileName, f.ContentType, f.SizeBytes,
                                  f.Description, e != null ? e.Name : null, f.DepartmentId, d != null ? d.Name : null, f.UploadedAt,
                                  f.Share, v.EmpId != null && f.UploadedBy == v.EmpId, f.ProjectCategoryId, f.BundleId, f.RelPath))
                              .ToListAsync(ct);
            return Results.Ok(rows);
        }).PartnerAllowed();

        // 업로드(여러 개 가능). multipart/form-data: files[], categoryId?, stageId?, description?
        g.MapPost("", async (long projectId, HttpRequest request, ClaimsPrincipal principal,
                             IConfiguration config, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "multipart/form-data가 필요합니다." });
            var project = await db.Projects.Where(x => x.Id == projectId).Select(x => new { x.Code, x.Name }).FirstOrDefaultAsync(ct);
            if (project is null) return Results.NotFound();

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0) return Results.BadRequest(new { message = "파일이 없습니다." });

            long? categoryId = long.TryParse(form["categoryId"], out var cid) ? cid : null;
            long? stageId = long.TryParse(form["stageId"], out var sid) ? sid : null;
            var description = form["description"].ToString() is { Length: > 0 } d ? d : null;
            var share = form["share"].ToString() is { Length: > 0 } sh ? sh : "me";
            if (!Share.Valid(share)) return Results.BadRequest(new { message = "올바르지 않은 공유 범위입니다." });
            long? projectCategoryId = long.TryParse(form["projectCategoryId"], out var pcid) ? pcid : null;
            // 폴더째 올리기: bundleId(한 폴더 = 한 묶음) + relPath(맨 위 폴더부터, 파일 이름 제외). 파일마다 따로 올려도 같은 묶음 폴더에 모인다
            Guid? bundleId = Guid.TryParse(form["bundleId"], out var bid) ? bid : null;
            var relPath = bundleId is null ? null : string.Join('/', form["relPath"].ToString().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(x => x is not "." and not ".."));
            if (bundleId is not null && string.IsNullOrEmpty(relPath)) return Results.BadRequest(new { message = "폴더 경로(relPath)가 없습니다." });
            string? categoryName = null;
            if (projectCategoryId is not null)
            {
                categoryName = await db.ProjectCategories.Where(c => c.Id == projectCategoryId && c.ProjectId == projectId).Select(c => c.Name).FirstOrDefaultAsync(ct);
                if (categoryName is null) return Results.BadRequest(new { message = "올바르지 않은 종류입니다." });
                categoryId = null;
            }
            else if (categoryId is not null)
            {
                categoryName = await db.Codes.Where(c => c.Id == categoryId && c.GroupKey == "document_category" && c.IsActive).Select(c => c.Name).FirstOrDefaultAsync(ct);
                if (categoryName is null) return Results.BadRequest(new { message = "올바르지 않은 분류입니다." });
            }
            // 단계 화면에서 올리면 그 단계로 기록(stage_id). 진행 단계 코드만 허용.
            if (stageId is not null && !await db.Codes.AnyAsync(c => c.Id == stageId && c.GroupKey == "project_stage" && c.IsActive, ct))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });
            long? empId = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e2) ? e2 : null;
            // 업로더 소속 부서 스냅샷(앱 부서별 검색용)
            long? deptId = empId is not null ? await db.Employees.Where(x => x.Id == empId).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct) : null;

            // 사람이 읽는 폴더: "<코드> <이름>" (code=안정 기준). 분류 하위폴더 + 원본 파일명으로 저장.
            var projectFolder = $"{project.Code} {project.Name}".Trim();

            // 읽기 전용 연결에서 read-only 옵션만 제거(사용자 편집 데이터 전용) — 단계 변경(PUT)과 동일.
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);

            // 묶음 폴더 자리: 같은 묶음 파일이 이미 있으면 그 폴더(projects/<프로젝트>/<분류>/<맨 위 폴더>), 처음이면 새로 잡는다
            string? bundleDir = null;
            string? subPath = null;
            if (bundleId is not null)
            {
                var first = await db.ProjectFiles.Where(x => x.ProjectId == projectId && x.BundleId == bundleId).Select(x => x.StoredName).FirstOrDefaultAsync(ct);
                bundleDir = first is not null ? string.Join('/', first.Split('/').Take(4)) : storage.NewBundleDir(projectFolder, categoryName, relPath!.Split('/')[0]);
                subPath = string.Join('/', relPath!.Split('/').Skip(1));
            }

            var saved = new List<object>();
            foreach (var file in form.Files)
            {
                if (file.Length == 0) continue;
                string storedRel;
                await using (var stream = file.OpenReadStream())
                    storedRel = bundleDir is not null
                        ? await storage.SaveInBundleAsync(bundleDir, subPath, file.FileName, stream, ct)
                        : await storage.SaveAsync(projectFolder, categoryName, file.FileName, stream, ct);

                await using var cmd = new NpgsqlCommand(
                    @"insert into project.project_file
                        (project_id, category_id, stage_id, file_name, stored_name, content_type, size_bytes, description, uploaded_by, department_id, uploaded_at, is_active, share, project_category_id, bundle_id, rel_path)
                      values (@pid, @cid, @sid, @fn, @sn, @ctype, @sz, @ds, @uid, @dept, now(), true, @share, @pcid, @bid, @rel) returning id", conn);
                cmd.Parameters.AddWithValue("pid", projectId);
                cmd.Parameters.AddWithValue("cid", (object?)categoryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("sid", (object?)stageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("fn", file.FileName);
                cmd.Parameters.AddWithValue("sn", storedRel);
                cmd.Parameters.AddWithValue("ctype", (object?)file.ContentType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("sz", file.Length);
                cmd.Parameters.AddWithValue("ds", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("uid", (object?)empId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("dept", (object?)deptId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("share", share);
                cmd.Parameters.AddWithValue("pcid", (object?)projectCategoryId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("bid", (object?)bundleId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("rel", (object?)relPath ?? DBNull.Value);
                var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                saved.Add(new { id, fileName = file.FileName });
            }
            return Results.Ok(new { ok = true, count = saved.Count, files = saved });
        }).DisableAntiforgery();

        // 다운로드(원본 파일명으로 내려받기)
        g.MapGet("/{fileId:long}/download", async (long projectId, long fileId, ClaimsPrincipal principal, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            var f = await db.ProjectFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive, ct);
            if (f is null) return Results.NotFound();
            var v = await Share.ViewerAsync(principal, db, ct);
            var visible = f.Share == "project" || (!v.Partner && (f.UploadedBy == v.EmpId || (f.Share == "dept" && f.DepartmentId != null && f.DepartmentId == v.DeptId)));
            if (!visible) return Results.NotFound();
            if (!storage.Exists(f.StoredName)) return Results.NotFound(new { message = "저장소에 파일이 없습니다." });
            var stream = new FileStream(storage.FullPath(f.StoredName), FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.File(stream, f.ContentType ?? "application/octet-stream", f.FileName);
        }).PartnerAllowed();

        // 분류 변경(업로드 후). 실물 파일을 새 분류 폴더로 옮기고 경로도 갱신. 지금은 로그인 전원 가능(권한은 추후).
        g.MapPut("/{fileId:long}", async (long projectId, long fileId, FileCategoryUpdate body, IConfiguration config, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            var f = await db.ProjectFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive, ct);
            if (f is null) return Results.NotFound();
            string? categoryName = null;
            if (body.ProjectCategoryId is not null)
            {
                categoryName = await db.ProjectCategories.Where(c => c.Id == body.ProjectCategoryId && c.ProjectId == projectId).Select(c => c.Name).FirstOrDefaultAsync(ct);
                if (categoryName is null) return Results.BadRequest(new { message = "올바르지 않은 종류입니다." });
            }
            else if (body.CategoryId is not null)
            {
                categoryName = await db.Codes.Where(c => c.Id == body.CategoryId && c.GroupKey == "document_category" && c.IsActive).Select(c => c.Name).FirstOrDefaultAsync(ct);
                if (categoryName is null) return Results.BadRequest(new { message = "올바르지 않은 분류입니다." });
            }
            var project = await db.Projects.Where(x => x.Id == projectId).Select(x => new { x.Code, x.Name }).FirstOrDefaultAsync(ct);
            var projectFolder = $"{project!.Code} {project.Name}".Trim();

            // 실물 파일을 새 분류 폴더로 이동(원본 이름 유지). 파일이 없으면 경로는 그대로 두고 분류만 변경.
            var newRel = storage.Exists(f.StoredName) ? storage.MoveToCategory(f.StoredName, projectFolder, categoryName) : f.StoredName;

            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set category_id = @cid, project_category_id = @pcid, stored_name = @sn where id = @id and project_id = @pid", conn);
            cmd.Parameters.AddWithValue("cid", body.ProjectCategoryId is null ? (object?)body.CategoryId ?? DBNull.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("pcid", (object?)body.ProjectCategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sn", newRel);
            cmd.Parameters.AddWithValue("id", fileId);
            cmd.Parameters.AddWithValue("pid", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // 단계 꼬리표 변경(업로드 후). 단계는 꼬리표라 실물 파일은 옮기지 않는다. null = 단계 없음. 지금은 로그인 전원 가능(권한은 추후).
        g.MapPut("/{fileId:long}/stage", async (long projectId, long fileId, FileStageUpdate body, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!await db.ProjectFiles.AnyAsync(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive, ct)) return Results.NotFound();
            if (body.StageId is not null && !await db.Codes.AnyAsync(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive, ct))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set stage_id = @sid where id = @id and project_id = @pid", conn);
            cmd.Parameters.AddWithValue("sid", (object?)body.StageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", fileId);
            cmd.Parameters.AddWithValue("pid", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // 공유 범위 변경 — 올린 사람만
        g.MapPut("/{fileId:long}/share", async (long projectId, long fileId, FileShareUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!Share.Valid(body.Share)) return Results.BadRequest(new { message = "올바르지 않은 공유 범위입니다." });
            var f = await db.ProjectFiles.Where(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive).Select(x => new { x.UploadedBy }).FirstOrDefaultAsync(ct);
            if (f is null) return Results.NotFound();
            var v = await Share.ViewerAsync(principal, db, ct);
            if (v.EmpId is null || f.UploadedBy != v.EmpId) return Results.Json(new { message = "공유 범위는 올린 사람만 바꿀 수 있습니다." }, statusCode: 403);
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set share = @sh where id = @id and project_id = @pid", conn);
            cmd.Parameters.AddWithValue("sh", body.Share);
            cmd.Parameters.AddWithValue("id", fileId);
            cmd.Parameters.AddWithValue("pid", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // 삭제(소프트 삭제). 지금은 로그인 전원 가능(권한은 추후).
        g.MapDelete("/{fileId:long}", async (long projectId, long fileId, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!await db.ProjectFiles.AnyAsync(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive, ct)) return Results.NotFound();
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set is_active = false where id = @id and project_id = @pid", conn);
            cmd.Parameters.AddWithValue("id", fileId);
            cmd.Parameters.AddWithValue("pid", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // ───────── 폴더 묶음: 종류·단계·공유 범위·삭제를 묶음째, 내려받기는 ZIP ─────────
        var b = g.MapGroup("/bundles/{bundleId:guid}");

        b.MapGet("/zip", async (long projectId, Guid bundleId, ClaimsPrincipal principal, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            var files = await db.ProjectFiles.Where(x => x.ProjectId == projectId && x.BundleId == bundleId && x.IsActive).ToListAsync(ct);
            if (files.Count == 0) return Results.NotFound();
            var f0 = files[0];
            var visible = f0.Share == "project" || (!v.Partner && (f0.UploadedBy == v.EmpId || (f0.Share == "dept" && f0.DepartmentId != null && f0.DepartmentId == v.DeptId)));
            if (!visible) return Results.NotFound();
            var top = f0.RelPath?.Split('/')[0] ?? "folder";
            // 메모리에 쌓지 않게 임시 파일에 ZIP을 만들고, 내려보낸 뒤 지운다(묶음이 수 GB일 수 있음)
            var tmp = Path.Combine(Path.GetTempPath(), $"dyone-bundle-{Guid.NewGuid():N}.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(tmp, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var f in files.Where(f => storage.Exists(f.StoredName)))
                    zip.CreateEntryFromFile(storage.FullPath(f.StoredName), $"{f.RelPath}/{f.FileName}", CompressionLevel.Fastest);
            var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"{top}.zip");
        }).PartnerAllowed();

        // 묶음 종류 변경: 묶음 폴더째 새 분류 폴더로 옮기고 경로 접두를 바꾼다
        b.MapPut("/category", async (long projectId, Guid bundleId, BundleCategoryUpdate body, IConfiguration config, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            var stored = await db.ProjectFiles.Where(x => x.ProjectId == projectId && x.BundleId == bundleId && x.IsActive).Select(x => x.StoredName).FirstOrDefaultAsync(ct);
            if (stored is null) return Results.NotFound();
            string? categoryName = null;
            if (body.ProjectCategoryId is not null)
                categoryName = await db.ProjectCategories.Where(c => c.Id == body.ProjectCategoryId && c.ProjectId == projectId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            else if (body.CategoryId is not null)
                categoryName = await db.Codes.Where(c => c.Id == body.CategoryId && c.GroupKey == "document_category" && c.IsActive).Select(c => c.Name).FirstOrDefaultAsync(ct);
            if ((body.CategoryId ?? body.ProjectCategoryId) is not null && categoryName is null) return Results.BadRequest(new { message = "올바르지 않은 종류입니다." });
            var project = await db.Projects.Where(x => x.Id == projectId).Select(x => new { x.Code, x.Name }).FirstAsync(ct);
            var oldDir = string.Join('/', stored.Split('/').Take(4));
            var newDir = storage.MoveBundleDir(oldDir, $"{project.Code} {project.Name}".Trim(), categoryName);
            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand(
                @"update project.project_file set category_id = @cid, project_category_id = @pcid, stored_name = @new || substr(stored_name, length(@old) + 1)
                  where project_id = @pid and bundle_id = @bid and starts_with(stored_name, @old)", conn);
            cmd.Parameters.AddWithValue("cid", body.ProjectCategoryId is null ? (object?)body.CategoryId ?? DBNull.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("pcid", (object?)body.ProjectCategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("new", newDir);
            cmd.Parameters.AddWithValue("old", oldDir);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("bid", bundleId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        b.MapPut("/stage", async (long projectId, Guid bundleId, FileStageUpdate body, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (body.StageId is not null && !await db.Codes.AnyAsync(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive, ct))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });
            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set stage_id = @sid where project_id = @pid and bundle_id = @bid and is_active", conn);
            cmd.Parameters.AddWithValue("sid", (object?)body.StageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("bid", bundleId);
            return await cmd.ExecuteNonQueryAsync(ct) > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // 공유 범위 — 올린 사람만
        b.MapPut("/share", async (long projectId, Guid bundleId, FileShareUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            if (!Share.Valid(body.Share)) return Results.BadRequest(new { message = "올바르지 않은 공유 범위입니다." });
            var v = await Share.ViewerAsync(principal, db, ct);
            var owner = await db.ProjectFiles.Where(x => x.ProjectId == projectId && x.BundleId == bundleId && x.IsActive).Select(x => x.UploadedBy).FirstOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            if (v.EmpId is null || owner != v.EmpId) return Results.Json(new { message = "공유 범위는 올린 사람만 바꿀 수 있습니다." }, statusCode: 403);
            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set share = @sh where project_id = @pid and bundle_id = @bid and is_active", conn);
            cmd.Parameters.AddWithValue("sh", body.Share);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("bid", bundleId);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // 묶음 삭제(소프트 삭제, 낱개 삭제와 같은 권한)
        b.MapDelete("", async (long projectId, Guid bundleId, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand("update project.project_file set is_active = false where project_id = @pid and bundle_id = @bid and is_active", conn);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("bid", bundleId);
            return await cmd.ExecuteNonQueryAsync(ct) > 0 ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        // 프로젝트별 직접 입력 종류(그 프로젝트 안에서만 보임)
        var pc = app.MapGroup("/api/projects/{projectId:long}/categories").WithTags("ProjectFiles");
        pc.MapGet("", async (long projectId, DyOneDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ProjectCategories.Where(x => x.ProjectId == projectId).OrderBy(x => x.Id)
                .Select(x => new ProjectCategoryDto(x.Id, x.Name)).ToListAsync(ct))).PartnerAllowed();
        // 같은 이름이 있으면 그것을 돌려준다
        pc.MapPost("", async (long projectId, ProjectCategoryCreate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 50) return Results.BadRequest(new { message = "종류 이름은 1~50자로 넣어 주세요." });
            if (!await db.Projects.AnyAsync(x => x.Id == projectId, ct)) return Results.NotFound();
            long? emp = long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;
            var writeConn = new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"insert into project.project_category (project_id, name, created_by) values (@pid, @n, @by)
                  on conflict (project_id, name) do update set name = excluded.name returning id", conn);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("by", (object?)emp ?? DBNull.Value);
            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return Results.Ok(new ProjectCategoryDto(id, name));
        });
    }
}
