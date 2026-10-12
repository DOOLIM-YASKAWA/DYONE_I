using Microsoft.Extensions.Options;
using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어(tims) 조직도 → dyone org 스키마 동기화. 매일 지정 시각(기본 08:00) 실행.
/// (기존 etl/org-sync.js 를 Worker로 포팅. 재직자만 upsert, 사라지면 soft-delete)
///
/// 원천(GW, 조회 전용): go_domain_codes(POSITION/DUTY), go_departments, go_users(+profile/dept_members/mail).
/// 대상(dyone, 쓰기): org.position / org.duty / org.department / org.employee 를 id(그룹웨어 id) 기준 upsert.
///   - 재직자 = status='ONLINE' + 숫자 사번(employee_number). login_id별 최신 id 1건으로 dedup.
///   - 소스에서 빠진 직원/부서는 is_active=false(soft). FK 정합성: 유효 집합에 없는 참조는 null.
///
/// 이 Job으로 사번(emp_no)·이름·부서·직책이 매일 최신화되어 SRS/SmartLab 등과 사번 매칭이 유지된다.
/// 연결 문자열: ConnectionStrings:Gw (tims 읽기), ConnectionStrings:DyOneWrite (dyone 쓰기, 개발=dev 복사본).
/// </summary>
public class OrgSyncJob(IOptions<OrgSyncOptions> options, IConfiguration config, ILogger<OrgSyncJob> logger)
    : DailyJob(TimeOnly.Parse(options.Value.DailyAt), options.Value.TimeZone)
{
    private readonly OrgSyncOptions _opt = options.Value;

    public override string Name => "org-sync";
    public override bool RunOnStartup => _opt.RunOnStartup;

    private sealed record Code(long Id, string? CodeStr, string? Name, int? SortOrder);
    private sealed record Dept(long Id, long? ParentId, string? CodeStr, string? Name, int? SortOrder, string? Path);
    private sealed record Emp(long Id, string LoginId, string? EmpNo, string? Name, long? PositionId, long? DepartmentId, long? DutyId, string? Email);

    public override async Task RunAsync(CancellationToken ct)
    {
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");

        var (positions, duties, departments, employees) = await ReadTimsAsync(gwConn, ct);
        logger.LogInformation("조직도 조회(tims): position {P} · duty {D} · department {Dept} · employee(ONLINE) {E}",
            positions.Count, duties.Count, departments.Count, employees.Count);

        if (employees.Count == 0)
        {
            logger.LogWarning("재직자 0명 조회 → 안전을 위해 적재를 건너뛴다(전원 비활성 방지).");
            return;
        }

        if (_opt.DryRun)
        {
            logger.LogInformation("DryRun: 적재 생략. 직원 샘플 {Sample}",
                string.Join(" / ", employees.Take(5).Select(e => $"{e.EmpNo} {e.Name}({e.LoginId})")));
            return;
        }

        // 쓰기 대상(로컬 dev + 운영 등) 전체에 동일 조직도를 적재한다. 대상 하나가 실패해도 나머지는 진행.
        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");
        Exception? firstError = null;
        foreach (var (name, conn) in targets)
        {
            try
            {
                var (empDeact, deptDeact) = await UpsertAsync(conn, positions, duties, departments, employees, ct);
                await RecordSyncStateAsync(conn, ct);
                logger.LogInformation("[{Target}] 조직도 동기화 완료: 직원 {E} · 부서 {Dept} 적재 / 소프트 비활성 직원 -{ED} · 부서 -{DD}",
                    name, employees.Count, departments.Count, empDeact, deptDeact);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
                logger.LogError(ex, "[{Target}] 조직도 적재 실패", name);
                try { await TryRecordErrorAsync(conn, ex.Message, ct); } catch { /* 오류 기록 실패는 무시 */ }
            }
        }
        if (firstError is not null) throw firstError;
    }

    private static async Task<(List<Code> positions, List<Code> duties, List<Dept> departments, List<Emp> employees)>
        ReadTimsAsync(string gwConn, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using (var ro = new NpgsqlCommand("SET default_transaction_read_only = on", conn))
            await ro.ExecuteNonQueryAsync(ct); // 소스 쓰기 차단(안전장치)

        var positions = await ReadCodesAsync(conn, "POSITION", ct);
        var duties = await ReadCodesAsync(conn, "DUTY", ct);

        var departments = new List<Dept>();
        await using (var cmd = new NpgsqlCommand(
            "select id, parent_id, code, name, sort_order, path from go_departments where deleted_at is null", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                departments.Add(new Dept(r.GetInt64(0), N(r, 1), S(r, 2), S(r, 3), I(r, 4), S(r, 5)));

        var employees = new List<Emp>();
        const string empSql = @"
            with prim as (
              select distinct on (dm.user_id) dm.user_id, dm.department_id, dm.duty_id
                from go_dept_members dm where dm.deleted_at is null
                order by dm.user_id, dm.sort_order nulls last, dm.id
            )
            select distinct on (u.login_id)
                   u.id, u.login_id, u.employee_number as emp_no, u.name,
                   p.position_id, prim.department_id, prim.duty_id,
                   (mu.mail_uid || '@' || d.mail_domain) as email
              from go_users u
              left join go_user_profiles p on p.id = u.user_profile_id
              left join prim on prim.user_id = u.id
              left join mail_user mu on mu.mail_user_seq = u.mail_user_seq
              left join mail_domain d on d.mail_domain_seq = mu.mail_domain_seq
             where u.deleted_at is null and u.status = 'ONLINE'
               and u.employee_number ~ '^[0-9]+$'
             order by u.login_id, u.id desc";
        await using (var cmd = new NpgsqlCommand(empSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                employees.Add(new Emp(r.GetInt64(0), r.GetString(1), S(r, 2), S(r, 3), N(r, 4), N(r, 5), N(r, 6), S(r, 7)));

        return (positions, duties, departments, employees);
    }

    private static async Task<List<Code>> ReadCodesAsync(NpgsqlConnection conn, string codeType, CancellationToken ct)
    {
        var list = new List<Code>();
        await using var cmd = new NpgsqlCommand(
            "select id, code, ko_name as name, sort_order from go_domain_codes where code_type = @t", conn);
        cmd.Parameters.AddWithValue("t", codeType);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Code(r.GetInt64(0), S(r, 1), S(r, 2), I(r, 3)));
        return list;
    }

    private static async Task<(int empDeact, int deptDeact)> UpsertAsync(
        string writeConn, List<Code> positions, List<Code> duties, List<Dept> departments, List<Emp> employees, CancellationToken ct)
    {
        var deptIds = departments.Select(d => d.Id).ToHashSet();
        var posIds = positions.Select(p => p.Id).ToHashSet();
        var dutyIds = duties.Select(d => d.Id).ToHashSet();

        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var p in positions)
            await ExecAsync(conn, tx, ct,
                @"insert into org.position (id, code, name, rank, is_active)
                  overriding system value values (@id,@code,@name,@rank,true)
                  on conflict (id) do update set code=excluded.code, name=excluded.name, rank=excluded.rank, is_active=true",
                ("id", p.Id), ("code", (object?)p.CodeStr), ("name", (object?)p.Name), ("rank", (object?)p.SortOrder));

        foreach (var d in duties)
            await ExecAsync(conn, tx, ct,
                @"insert into org.duty (id, code, name, sort_order, is_active)
                  values (@id,@code,@name,@sort,true)
                  on conflict (id) do update set code=excluded.code, name=excluded.name, sort_order=excluded.sort_order, is_active=true",
                ("id", d.Id), ("code", (object?)d.CodeStr), ("name", (object?)d.Name), ("sort", (object?)d.SortOrder));

        // 부서: self-FK라 depth(path 세그먼트 수) 오름차순으로 부모 먼저. 부모가 집합에 없으면 null.
        foreach (var d in departments.OrderBy(x => Depth(x.Path)))
        {
            long? parent = d.ParentId is { } pid && deptIds.Contains(pid) ? pid : null;
            await ExecAsync(conn, tx, ct,
                @"insert into org.department (id, parent_id, code, name, sort_order, is_active, updated_at)
                  overriding system value values (@id,@parent,@code,@name,@sort,true, now())
                  on conflict (id) do update set parent_id=excluded.parent_id, code=excluded.code,
                     name=excluded.name, sort_order=excluded.sort_order, is_active=true, updated_at=now()",
                ("id", d.Id), ("parent", (object?)parent), ("code", (object?)d.CodeStr), ("name", (object?)d.Name), ("sort", (object?)d.SortOrder));
        }

        foreach (var e in employees)
        {
            long? deptId = e.DepartmentId is { } di && deptIds.Contains(di) ? di : null;
            long? posId = e.PositionId is { } pi && posIds.Contains(pi) ? pi : null;
            long? dutyId = e.DutyId is { } du && dutyIds.Contains(du) ? du : null;
            await ExecAsync(conn, tx, ct,
                @"insert into org.employee (id, emp_no, name, email, login_id, department_id, position_id, duty_id, is_active, updated_at)
                  overriding system value values (@id,@emp,@name,@email,@login,@dept,@pos,@duty,true, now())
                  on conflict (id) do update set emp_no=excluded.emp_no, name=excluded.name, email=excluded.email,
                     login_id=excluded.login_id, department_id=excluded.department_id, position_id=excluded.position_id,
                     duty_id=excluded.duty_id, is_active=true, updated_at=now()",
                ("id", e.Id), ("emp", (object?)e.EmpNo), ("name", (object?)e.Name), ("email", (object?)e.Email),
                ("login", (object?)e.LoginId), ("dept", (object?)deptId), ("pos", (object?)posId), ("duty", (object?)dutyId));
        }

        // 소프트 비활성화: 이번 조회에 없는 재직자/부서는 is_active=false
        int empDeact;
        await using (var cmd = new NpgsqlCommand(
            "update org.employee set is_active=false, updated_at=now() where is_active=true and not (id = any(@ids))", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", employees.Select(e => e.Id).ToArray());
            empDeact = await cmd.ExecuteNonQueryAsync(ct);
        }
        int deptDeact;
        await using (var cmd = new NpgsqlCommand(
            "update org.department set is_active=false, updated_at=now() where is_active=true and not (id = any(@ids))", conn, tx))
        {
            cmd.Parameters.AddWithValue("ids", deptIds.ToArray());
            deptDeact = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (empDeact, deptDeact);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql,
        params (string name, object? value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordSyncStateAsync(string writeConn, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            @"insert into sync.state (source, entity, watermark, updated_at) values ('groupware','org', to_char(now(),'YYYYMMDD'), now())
              on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TryRecordErrorAsync(string writeConn, string message, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into sync.error (source, entity, source_pk, message, created_at) values ('groupware','org', null, @m, now())", conn);
            cmd.Parameters.AddWithValue("m", message.Length > 2000 ? message[..2000] : message);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { logger.LogError(ex, "sync.error 기록 실패"); }
    }

    private static int Depth(string? path) => string.IsNullOrEmpty(path) ? 0 : path.Split(':').Length;
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static int? I(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
}
