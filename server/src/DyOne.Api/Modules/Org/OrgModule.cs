using System.Security.Claims;
using DyOne.Api.Platform;
using Npgsql;

namespace DyOne.Api.Modules.Org;

public record OrgDepartmentDto(long Id, long? ParentId, string Name, int SortOrder);
/// <param name="PmCount">PM을 맡은 프로젝트 수</param>
/// <param name="MemberCount">투입 인원으로 들어간 프로젝트 수(PM 제외)</param>
public record OrgEmployeeDto(long Id, string Name, long? DepartmentId, string? Position, int PositionRank, string? Duty, int? DutyOrder,
    string? Email, string? LoginId, int PmCount, int MemberCount, string? Mobile = null, string? DirectTel = null);
public record OrgEmployeeProjectDto(long ProjectId, string Code, string Name, string Role, string? Task, DateTimeOffset AssignedAt);

/// <summary>프로젝트 인원 한 명. Role: pm · member</summary>
public record ProjectMemberDto(long Id, long EmployeeId, string Name, string? Department, string? Position, string? Duty, string? Email,
    string Role, string? Task, DateTimeOffset AssignedAt, string? AssignedByName, string? Mobile = null, string? DirectTel = null);
/// <param name="Role">pm이면 PM으로 지정(기존 PM은 투입 인원으로 남김), member면 투입 인원 추가</param>
public record ProjectMemberAdd(long EmployeeId, string Role, string? Task = null);
/// <summary>투입 인원 여러 명 한 번에 추가. 이미 들어가 있는 사람은 건너뛴다. Task는 모두에게 같이 들어간다.</summary>
public record ProjectMembersAdd(List<long> EmployeeIds, string? Task = null);
public record ProjectMemberUpdate(string? Task);

/// <summary>
/// 조직도(org.*, 그룹웨어 조직도 미러 — 읽기 전용)와 프로젝트 인원(project.project_member — DY ONE에서 편집).
/// 프로젝트 인원 편집은 지금은 로그인한 전원 가능(진행 상황 변경과 같은 기준).
/// </summary>
public static class OrgModule
{
    public static void MapOrg(this IEndpointRouteBuilder app)
    {
        // 그룹 수준 권한은 걸지 않는다: 기본(Fallback)이 직원 전용이고, 프로젝트 인원 보기만 협력사 허용(.PartnerAllowed, 배정 프로젝트만)
        var g = app.MapGroup("/api").WithTags("Org");

        // 조직도: 재직 중인 부서·직원 전체(부서 62 · 직원 150 수준이라 한 번에 준다). 화면이 부서 트리를 만든다.
        g.MapGet("/org", async (IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var depts = new List<OrgDepartmentDto>();
            await using (var cmd = new NpgsqlCommand("select id, parent_id, name, sort_order from org.department where is_active order by sort_order, id", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    depts.Add(new OrgDepartmentDto(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetInt32(3)));

            var emps = new List<OrgEmployeeDto>();
            await using (var cmd = new NpgsqlCommand(@"
                select e.id, e.name, e.department_id, p.name, coalesce(p.rank, 0), du.name, du.sort_order, e.email, e.login_id,
                       coalesce(m.pm, 0), coalesce(m.member, 0), c.mobile, c.direct_tel
                from org.employee e
                left join org.employee_contact c on c.employee_id = e.id
                left join org.position p on p.id = e.position_id
                left join org.duty du on du.id = e.duty_id
                left join (select employee_id, count(*) filter (where role = 'pm')::int pm, count(*) filter (where role = 'member')::int member
                           from project.project_member where is_active group by employee_id) m on m.employee_id = e.id
                where e.is_active
                order by du.sort_order nulls last, p.rank, e.name", conn))
            {
                try
                {
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    string? S(NpgsqlDataReader x, int i) => x.IsDBNull(i) ? null : x.GetString(i);
                    while (await r.ReadAsync(ct))
                        emps.Add(new OrgEmployeeDto(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), S(r, 3), r.GetInt32(4),
                            S(r, 5), r.IsDBNull(6) ? null : r.GetInt32(6), S(r, 7), S(r, 8), r.GetInt32(9), r.GetInt32(10), S(r, 11), S(r, 12)));
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
                {
                    return Results.Problem("조직도용 테이블이 없습니다(server/db/project_member.sql · org_contact.sql 적용 필요).");
                }
            }
            return Results.Ok(new { departments = depts, employees = emps });
        });

        // 한 사람이 맡은 프로젝트(PM · 투입 인원)
        g.MapGet("/org/employees/{id:long}/projects", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                select p.id, p.code, p.name, m.role, m.task, m.assigned_at
                from project.project_member m join project.project p on p.id = m.project_id
                where m.employee_id = @eid and m.is_active
                order by (m.role = 'pm') desc, m.assigned_at desc", conn);
            cmd.Parameters.AddWithValue("eid", id);
            var list = new List<OrgEmployeeProjectDto>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new OrgEmployeeProjectDto(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetFieldValue<DateTimeOffset>(5)));
            return Results.Ok(list);
        });

        // 프로젝트 인원(PM 먼저, 그다음 넣은 순서)
        g.MapGet("/projects/{id:long}/members", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            return Results.Ok(await MembersAsync(conn, null, id, ct));
        }).PartnerAllowed();

        g.MapPost("/projects/{id:long}/members", async (long id, ProjectMemberAdd body, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            if (body.Role is not ("pm" or "member")) return Results.BadRequest(new { message = "역할은 PM 또는 투입 인원만 쓸 수 있습니다." });
            var task = Clean(body.Task);
            if (task is { Length: > 100 }) return Results.BadRequest(new { message = "맡은 일은 100자까지 쓸 수 있습니다." });
            var by = Me(principal);

            await using var conn = await OpenWriteAsync(config, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await ExistsAsync(conn, tx, "select 1 from project.project where id = @id", id, ct)) return Results.NotFound();
            if (!await ExistsAsync(conn, tx, "select 1 from org.employee where id = @id and is_active", body.EmployeeId, ct))
                return Results.BadRequest(new { message = "조직도에 없는(퇴직) 직원입니다." });

            // 이 프로젝트의 현재 인원을 잠그고 읽는다(동시에 PM을 둘 지정하는 것을 막는다)
            var current = new Dictionary<long, (long Id, string Role)>();
            await using (var cur = new NpgsqlCommand("select id, employee_id, role from project.project_member where project_id = @pid and is_active and employee_id is not null for update", conn, tx))
            {
                cur.Parameters.AddWithValue("pid", id);
                await using var r = await cur.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) current[r.GetInt64(1)] = (r.GetInt64(0), r.GetString(2));
            }
            var mine = current.TryGetValue(body.EmployeeId, out var m) ? m : ((long Id, string Role)?)null;

            if (body.Role == "member")
            {
                if (mine is not null) return Results.Conflict(new { message = mine.Value.Role == "pm" ? "이미 이 프로젝트의 PM입니다." : "이미 투입된 인원입니다." });
                await ExecAsync(conn, tx, @"insert into project.project_member (project_id, employee_id, role, task, assigned_by) values (@pid, @eid, 'member', @task, @by)",
                    ct, ("pid", id), ("eid", body.EmployeeId), ("task", task), ("by", by));
            }
            else
            {
                if (mine?.Role == "pm") return Results.Ok(new { ok = true, changed = false });
                // 기존 PM은 투입 인원으로 남긴다
                await ExecAsync(conn, tx, "update project.project_member set role = 'member', updated_at = now() where project_id = @pid and is_active and role = 'pm'",
                    ct, ("pid", id));
                if (mine is not null)
                    await ExecAsync(conn, tx, "update project.project_member set role = 'pm', task = coalesce(@task, task), updated_at = now() where id = @mid",
                        ct, ("mid", mine.Value.Id), ("task", task));
                else
                    await ExecAsync(conn, tx, @"insert into project.project_member (project_id, employee_id, role, task, assigned_by) values (@pid, @eid, 'pm', @task, @by)",
                        ct, ("pid", id), ("eid", body.EmployeeId), ("task", task), ("by", by));
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { ok = true, changed = true });
        });

        // 투입 인원 여러 명 한 번에 추가(이미 들어간 사람·퇴직자는 건너뛰고, 건너뛴 수를 알려 준다)
        g.MapPost("/projects/{id:long}/members/bulk", async (long id, ProjectMembersAdd body, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            var ids = (body.EmployeeIds ?? []).Distinct().ToArray();
            if (ids.Length == 0) return Results.BadRequest(new { message = "추가할 인원을 고르세요." });
            if (ids.Length > 200) return Results.BadRequest(new { message = "한 번에 200명까지 추가할 수 있습니다." });
            var task = Clean(body.Task);
            if (task is { Length: > 100 }) return Results.BadRequest(new { message = "맡은 일은 100자까지 쓸 수 있습니다." });

            await using var conn = await OpenWriteAsync(config, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await ExistsAsync(conn, tx, "select 1 from project.project where id = @id", id, ct)) return Results.NotFound();
            // 같은 프로젝트에 동시에 넣는 경우를 막으려고 현재 인원 행을 잠근다(중복은 고유 인덱스가 최종 방어)
            await using (var lk = new NpgsqlCommand("select 1 from project.project_member where project_id = @pid and is_active for update", conn, tx))
            {
                lk.Parameters.AddWithValue("pid", id);
                await lk.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = new NpgsqlCommand(@"
                insert into project.project_member (project_id, employee_id, role, task, assigned_by)
                select @pid, e.id, 'member', @task, @by
                from org.employee e
                where e.id = any(@ids) and e.is_active
                  and not exists (select 1 from project.project_member m where m.project_id = @pid and m.employee_id = e.id and m.is_active)", conn, tx);
            cmd.Parameters.AddWithValue("pid", id);
            cmd.Parameters.AddWithValue("ids", ids);
            cmd.Parameters.Add(new NpgsqlParameter("task", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)task ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)Me(principal) ?? DBNull.Value });
            var added = await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { ok = true, added, skipped = ids.Length - added });
        });

        // 맡은 일 고치기
        g.MapPatch("/projects/{id:long}/members/{memberId:long}", async (long id, long memberId, ProjectMemberUpdate body, IConfiguration config, CancellationToken ct) =>
        {
            var task = Clean(body.Task);
            if (task is { Length: > 100 }) return Results.BadRequest(new { message = "맡은 일은 100자까지 쓸 수 있습니다." });
            await using var conn = await OpenWriteAsync(config, ct);
            var n = await ExecAsync(conn, null, "update project.project_member set task = @task, updated_at = now() where id = @mid and project_id = @pid and is_active",
                ct, ("mid", memberId), ("pid", id), ("task", task));
            return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });

        // 인원 빼기(PM도 같은 방법으로 해제). 지우지 않고 이력으로 남긴다.
        g.MapDelete("/projects/{id:long}/members/{memberId:long}", async (long id, long memberId, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenWriteAsync(config, ct);
            var n = await ExecAsync(conn, null,
                "update project.project_member set is_active = false, removed_by = @by, removed_at = now(), updated_at = now() where id = @mid and project_id = @pid and is_active",
                ct, ("mid", memberId), ("pid", id), ("by", Me(principal)));
            return n == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });
    }

    /// <summary>프로젝트들의 PM 이름(목록·상세 표시용). 인원 테이블이 아직 없으면 빈 값.</summary>
    public static async Task<Dictionary<long, string>> PmNamesAsync(NpgsqlConnection conn, IReadOnlyCollection<long> projectIds, CancellationToken ct)
    {
        var map = new Dictionary<long, string>();
        if (projectIds.Count == 0) return map;
        await using var cmd = new NpgsqlCommand(@"select m.project_id, e.name from project.project_member m join org.employee e on e.id = m.employee_id
                                                 where m.is_active and m.role = 'pm' and m.project_id = any(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", projectIds.ToArray());
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) map[r.GetInt64(0)] = r.GetString(1);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { }
        return map;
    }

    private static async Task<List<ProjectMemberDto>> MembersAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long projectId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            select m.id, e.id, e.name, d.name, p.name, du.name, e.email, m.role, m.task, m.assigned_at, b.name, c.mobile, c.direct_tel
            from project.project_member m
            join org.employee e on e.id = m.employee_id
            left join org.employee_contact c on c.employee_id = e.id
            left join org.department d on d.id = e.department_id
            left join org.position p on p.id = e.position_id
            left join org.duty du on du.id = e.duty_id
            left join org.employee b on b.id = m.assigned_by
            where m.project_id = @pid and m.is_active
            order by (m.role = 'pm') desc, m.assigned_at, m.id", conn, tx);
        cmd.Parameters.AddWithValue("pid", projectId);
        var list = new List<ProjectMemberDto>();
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (await r.ReadAsync(ct))
                list.Add(new ProjectMemberDto(r.GetInt64(0), r.GetInt64(1), r.GetString(2), S(3), S(4), S(5), S(6), r.GetString(7), S(8),
                    r.GetFieldValue<DateTimeOffset>(9), S(10), S(11), S(12)));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // 인원 테이블 적용 전(server/db/project_member.sql): 빈 목록
        }
        return list;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static long? Me(ClaimsPrincipal principal) => long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;

    /// <summary>읽기 전용 연결(ConnectionStrings:DyOne)에서 read-only 옵션만 제거한 쓰기 연결(사용자 편집 데이터 전용)</summary>
    private static async Task<NpgsqlConnection> OpenWriteAsync(IConfiguration config, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
