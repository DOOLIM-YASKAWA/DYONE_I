using System.Security.Claims;
using DyOne.Data;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

public record JournalDto(
    long Id, long ProjectId, string ProjectCode, string ProjectName, long? StageId, string? Stage,
    long AuthorId, string? AuthorName, long? DepartmentId, string? Department,
    string Share, string Status, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool Mine, bool CanStatus);
public record JournalCreate(string Body, long? StageId, string? Share);
public record JournalUpdate(string? Body, long? StageId, string? Share, string? Status);

/// <summary>
/// 프로젝트 진행 일지(project.project_journal). 직원 전용(협력사 차단은 기본 정책).
/// 보기: 본인 것 + 같은 부서의 dept 공유 + 모든 project 공유. project 공유만 '진행 단계' 탭에 단계별로 보인다.
/// 상태(진행·완료) = 작성자 또는 같은 부서 · 본문·단계·공유 범위·삭제 = 작성자만.
/// '같은 부서' = 작성 당시 부서(department_id 스냅샷)와 보는 사람의 지금 부서가 같음.
/// </summary>
public static class ProjectJournalModule
{
    const string Select = @"
        select j.id, j.project_id, p.code, p.name, j.stage_id, s.name, j.author_id, e.name, j.department_id, d.name,
               j.share, j.status, j.body, j.created_at, j.updated_at
        from project.project_journal j
        join project.project p on p.id = j.project_id
        left join code.code s on s.id = j.stage_id
        left join org.employee e on e.id = j.author_id
        left join org.department d on d.id = j.department_id
        where j.is_active
          and (j.share = 'project' or j.author_id = @me or (j.share = 'dept' and j.department_id is not null and j.department_id = @dept))";

    public static void MapProjectJournal(this IEndpointRouteBuilder app)
    {
        // 한 프로젝트의 일지(프로젝트 안 '프로젝트 진행 일지' 탭 · 진행 단계 탭)
        app.MapGet("/api/projects/{projectId:long}/journal", async (long projectId, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            return Results.Ok(await ReadAsync(config, v, Select + " and j.project_id = @pid order by j.updated_at desc limit 500", ct, ("pid", projectId)));
        }).WithTags("ProjectJournal");

        // 여러 프로젝트 일지(사이드바 '프로젝트 진행 일지'): 내가 인원이거나 일지를 쓴 프로젝트
        app.MapGet("/api/journal", async (ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            const string mine = @" and (exists (select 1 from project.project_member m where m.project_id = j.project_id and m.employee_id = @me and m.is_active)
                                        or exists (select 1 from project.project_journal j2 where j2.project_id = j.project_id and j2.author_id = @me and j2.is_active))
                                   order by j.updated_at desc limit 500";
            return Results.Ok(await ReadAsync(config, v, Select + mine, ct));
        }).WithTags("ProjectJournal");

        // 내가 인원(PM 포함)인 프로젝트 — 사이드바에서 일지를 쓸 프로젝트 고르기
        app.MapGet("/api/journal/projects", async (ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                @"select distinct p.id, p.code, p.name from project.project_member m join project.project p on p.id = m.project_id
                  where m.employee_id = @me and m.is_active order by p.code desc", conn);
            cmd.Parameters.AddWithValue("me", (object?)v.EmpId ?? -1L);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new { id = r.GetInt64(0), code = r.GetString(1), name = r.GetString(2) });
            return Results.Ok(list);
        }).WithTags("ProjectJournal");

        app.MapPost("/api/projects/{projectId:long}/journal", async (long projectId, JournalCreate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            if (v.EmpId is null) return Results.Forbid();
            var text = body.Body?.Trim() ?? "";
            if (text.Length is 0 or > 20000) return Results.BadRequest(new { message = "내용을 넣어 주세요(2만 자까지)." });
            var share = body.Share ?? "me";
            if (!Share.Valid(share)) return Results.BadRequest(new { message = "올바르지 않은 공유 범위입니다." });
            if (body.StageId is not null && !db.Codes.Any(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });
            if (!db.Projects.Any(x => x.Id == projectId)) return Results.NotFound();
            await using var conn = await WriteAsync(config, ct);
            // 프로젝트 인원(PM 포함)으로 배정된 사람만 쓴다
            await using (var chk = new NpgsqlCommand("select exists (select 1 from project.project_member where project_id = @pid and employee_id = @me and is_active)", conn))
            {
                chk.Parameters.AddWithValue("pid", projectId);
                chk.Parameters.AddWithValue("me", v.EmpId);
                if (!(bool)(await chk.ExecuteScalarAsync(ct))!)
                    return Results.Json(new { message = "프로젝트 인원으로 배정된 사람만 일지를 쓸 수 있습니다. PM에게 인원 추가를 요청하세요." }, statusCode: 403);
            }
            await using var cmd = new NpgsqlCommand(
                @"insert into project.project_journal (project_id, stage_id, author_id, department_id, share, status, body, updated_by)
                  values (@pid, @sid, @me, @dept, @share, 'run', @body, @me) returning id", conn);
            cmd.Parameters.AddWithValue("pid", projectId);
            cmd.Parameters.AddWithValue("sid", (object?)body.StageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("me", v.EmpId);
            cmd.Parameters.AddWithValue("dept", (object?)v.DeptId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("share", share);
            cmd.Parameters.AddWithValue("body", text);
            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            return Results.Ok(new { id });
        }).WithTags("ProjectJournal");

        app.MapPut("/api/journal/{id:long}", async (long id, JournalUpdate body, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            var j = db.ProjectJournals.Where(x => x.Id == id && x.IsActive).Select(x => new { x.AuthorId, x.DepartmentId }).FirstOrDefault();
            if (j is null || v.EmpId is null) return Results.NotFound();
            var author = j.AuthorId == v.EmpId;
            var sameDept = j.DepartmentId is not null && j.DepartmentId == v.DeptId;
            // 작성자만 바꾸는 칸: 본문·단계·공유 범위
            if ((body.Body is not null || body.StageId is not null || body.Share is not null) && !author)
                return Results.Json(new { message = "내용·단계·공유 범위는 작성자만 바꿀 수 있습니다." }, statusCode: 403);
            if (body.Status is not null && !author && !sameDept)
                return Results.Json(new { message = "상태는 작성자나 같은 부서 사람만 바꿀 수 있습니다." }, statusCode: 403);
            if (body.Status is not null and not ("run" or "done")) return Results.BadRequest(new { message = "올바르지 않은 상태입니다." });
            if (body.Share is not null && !Share.Valid(body.Share)) return Results.BadRequest(new { message = "올바르지 않은 공유 범위입니다." });
            if (body.Body is not null && body.Body.Trim().Length is 0 or > 20000) return Results.BadRequest(new { message = "내용을 넣어 주세요(2만 자까지)." });
            if (body.StageId is not null && !db.Codes.Any(c => c.Id == body.StageId && c.GroupKey == "project_stage" && c.IsActive))
                return Results.BadRequest(new { message = "올바르지 않은 단계입니다." });

            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand(
                @"update project.project_journal set
                    body = coalesce(@body, body), stage_id = coalesce(@sid, stage_id), share = coalesce(@share, share), status = coalesce(@st, status),
                    updated_at = now(), updated_by = @me
                  where id = @id", conn);
            cmd.Parameters.AddWithValue("body", (object?)body.Body?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sid", (object?)body.StageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("share", (object?)body.Share ?? DBNull.Value);
            cmd.Parameters.AddWithValue("st", (object?)body.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("me", v.EmpId);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        }).WithTags("ProjectJournal");

        app.MapDelete("/api/journal/{id:long}", async (long id, ClaimsPrincipal principal, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            var v = await Share.ViewerAsync(principal, db, ct);
            var authorId = db.ProjectJournals.Where(x => x.Id == id && x.IsActive).Select(x => (long?)x.AuthorId).FirstOrDefault();
            if (authorId is null) return Results.NotFound();
            if (authorId != v.EmpId) return Results.Json(new { message = "작성자만 지울 수 있습니다." }, statusCode: 403);
            await using var conn = await WriteAsync(config, ct);
            await using var cmd = new NpgsqlCommand("update project.project_journal set is_active = false, updated_at = now(), updated_by = @me where id = @id", conn);
            cmd.Parameters.AddWithValue("me", v.EmpId!);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { ok = true });
        }).WithTags("ProjectJournal");
    }

    static async Task<List<JournalDto>> ReadAsync(IConfiguration config, Share.Viewer v, string sql, CancellationToken ct, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("me", (object?)v.EmpId ?? -1L);
        cmd.Parameters.AddWithValue("dept", (object?)v.DeptId ?? -1L);
        foreach (var (k, val) in args) cmd.Parameters.AddWithValue(k, val);
        var list = new List<JournalDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            long? deptId = r.IsDBNull(8) ? null : r.GetInt64(8);
            var authorId = r.GetInt64(6);
            list.Add(new JournalDto(
                r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5),
                authorId, r.IsDBNull(7) ? null : r.GetString(7), deptId, r.IsDBNull(9) ? null : r.GetString(9),
                r.GetString(10), r.GetString(11), r.GetString(12), r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14),
                authorId == v.EmpId, authorId == v.EmpId || (deptId is not null && deptId == v.DeptId)));
        }
        return list;
    }

    static async Task<NpgsqlConnection> WriteAsync(IConfiguration config, CancellationToken ct)
    {
        // 읽기 전용 연결에서 read-only 옵션만 제거(사용자 편집 데이터 전용) — 자료·단계 변경과 같은 방식
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
