using System.Security.Claims;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

public record ProjectFileDto(
    long Id, long ProjectId, long? CategoryId, string? Category, string? CategoryCode,
    long? StageId, string? Stage, string FileName, string? ContentType, long SizeBytes,
    string? Description, string? UploadedByName, long? DepartmentId, string? Department, DateTimeOffset UploadedAt);

public record FileCategoryUpdate(long? CategoryId);

/// <summary>
/// 프로젝트별 업로드 문서. 메타데이터는 project.project_file(dyone 자체 데이터), 실물은 FileStorage(NAS 등).
/// 그룹웨어/ERP와 무관하게 DY ONE에서 직접 쓴다(쓰기 연결은 단계 변경과 동일 패턴).
/// </summary>
public static class ProjectFilesModule
{
    public static void MapProjectFiles(this IEndpointRouteBuilder app)
    {
        // 그룹 수준 권한은 걸지 않는다: 기본(Fallback)이 직원 전용이고, 목록·내려받기만 협력사 허용(.PartnerAllowed, 배정 프로젝트만)
        var g = app.MapGroup("/api/projects/{projectId:long}/files").WithTags("ProjectFiles");

        // 목록(활성 문서만, 최신 업로드 순). 분류·단계·업로더 이름을 함께 내려준다.
        g.MapGet("", async (long projectId, DyOneDbContext db, CancellationToken ct) =>
        {
            var rows = await (from f in db.ProjectFiles.Where(x => x.ProjectId == projectId && x.IsActive)
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
                                  f.Id, f.ProjectId, f.CategoryId, c != null ? c.Name : null, c != null ? c.Code : null,
                                  f.StageId, s != null ? s.Name : null, f.FileName, f.ContentType, f.SizeBytes,
                                  f.Description, e != null ? e.Name : null, f.DepartmentId, d != null ? d.Name : null, f.UploadedAt))
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
            string? categoryName = null;
            if (categoryId is not null)
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

            var saved = new List<object>();
            foreach (var file in form.Files)
            {
                if (file.Length == 0) continue;
                string storedRel;
                await using (var stream = file.OpenReadStream())
                    storedRel = await storage.SaveAsync(projectFolder, categoryName, file.FileName, stream, ct);

                await using var cmd = new NpgsqlCommand(
                    @"insert into project.project_file
                        (project_id, category_id, stage_id, file_name, stored_name, content_type, size_bytes, description, uploaded_by, department_id, uploaded_at, is_active)
                      values (@pid, @cid, @sid, @fn, @sn, @ctype, @sz, @ds, @uid, @dept, now(), true) returning id", conn);
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
                var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                saved.Add(new { id, fileName = file.FileName });
            }
            return Results.Ok(new { ok = true, count = saved.Count, files = saved });
        }).DisableAntiforgery();

        // 다운로드(원본 파일명으로 내려받기)
        g.MapGet("/{fileId:long}/download", async (long projectId, long fileId, FileStorage storage, DyOneDbContext db, CancellationToken ct) =>
        {
            var f = await db.ProjectFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.ProjectId == projectId && x.IsActive, ct);
            if (f is null) return Results.NotFound();
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
            if (body.CategoryId is not null)
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
            await using var cmd = new NpgsqlCommand("update project.project_file set category_id = @cid, stored_name = @sn where id = @id and project_id = @pid", conn);
            cmd.Parameters.AddWithValue("cid", (object?)body.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sn", newRel);
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
    }
}
