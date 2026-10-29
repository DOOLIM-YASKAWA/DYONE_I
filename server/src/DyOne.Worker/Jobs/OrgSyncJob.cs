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
///
/// 소속·부서 이력(org.employee_dept_history · org.department_history, server/db/org_history.sql — 표가 없는 대상은 건너뜀):
///   - 덮어쓴 뒤 열린 이력 행과 비교해, 바뀐 사람·부서는 어제로 닫고 오늘부터 새 행을 연다(source=sync).
///   - 그룹웨어는 부서를 지우면 소속 행도 지우므로, 처음 한 번 결재 기안 부서(approval)와 go_departments 생성·삭제일(gw)로 그 이전을 채운다.
///   - 조직 개편 때 역할 ↔ 부서 연결 후보를 '사람이 옮겨 간 방향'으로 내는 근거(부서 자동 부여).
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
    private sealed record Emp(long Id, string LoginId, string? EmpNo, string? Name, long? PositionId, long? DepartmentId, long? DutyId, string? Email,
        string? Mobile = null, string? DirectTel = null);
    /// <summary>결재로 본 소속 구간(사람·월별 최다 기안 부서가 이어진 구간). LastDay = 그 구간 마지막 기안일.</summary>
    private sealed record ApprovalSpan(long EmployeeId, long DeptId, string? DeptName, DateOnly From, DateOnly LastDay);
    private sealed record DeptLife(long Id, long? ParentId, string? CodeStr, string? Name, DateOnly? Created, DateOnly? Deleted);
    /// <summary>결재로 본 부서 이름 구간(같은 부서 id의 월별 최다 기안 부서 이름이 이어진 구간).</summary>
    private sealed record DeptNameSpan(long DeptId, string Name, DateOnly From, DateOnly LastDay);
    private sealed record Backfill(List<ApprovalSpan> Spans, List<DeptLife> Depts, List<DeptNameSpan> DeptNames);

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
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(_opt.TimeZone)).DateTime);
        Backfill? backfill = null; // 이력을 처음 채우는 대상이 있을 때만 그룹웨어에서 한 번 읽는다
        async Task<Backfill> LoadBackfillAsync() => backfill ??= await ReadBackfillAsync(gwConn, ct);
        Exception? firstError = null;
        foreach (var (name, conn) in targets)
        {
            try
            {
                var (empDeact, deptDeact, history) = await UpsertAsync(conn, positions, duties, departments, employees, today, LoadBackfillAsync, ct);
                await RecordSyncStateAsync(conn, ct);
                logger.LogInformation("[{Target}] 조직도 동기화 완료: 직원 {E} · 부서 {Dept} 적재 / 소프트 비활성 직원 -{ED} · 부서 -{DD}",
                    name, employees.Count, departments.Count, empDeact, deptDeact);
                if (history is not null) logger.LogInformation("[{Target}] 소속·부서 이력: {History}", name, history);
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
                   (mu.mail_uid || '@' || d.mail_domain) as email,
                   nullif(trim(p.mobile_no), '') as mobile, nullif(trim(p.direct_tel), '') as direct_tel
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
                employees.Add(new Emp(r.GetInt64(0), r.GetString(1), S(r, 2), S(r, 3), N(r, 4), N(r, 5), N(r, 6), S(r, 7), S(r, 8), S(r, 9)));

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

    /// <summary>
    /// 이력 첫 채우기용(조회 전용): 결재 기안 부서를 사람·월별 최다 부서로 줄인 뒤 같은 부서가 이어진 구간으로 묶는다(겸직·공용 부서가 섞여 오락가락하는 것을 완화).
    /// 부서는 삭제된 것까지 생성·삭제일을 읽는다.
    /// </summary>
    private static async Task<Backfill> ReadBackfillAsync(string gwConn, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using (var ro = new NpgsqlCommand("SET default_transaction_read_only = on", conn))
            await ro.ExecuteNonQueryAsync(ct);

        var spans = new List<ApprovalSpan>();
        const string spanSql = @"
            with ids as (  -- 한 로그인에 사용자 id가 여럿(재입사 등)이면 조직도 동기화와 같은 id(재직 중 최신, 없으면 최신)로 합친다
              select u.id, coalesce(
                       (select max(o.id) from go_users o where o.login_id = u.login_id and o.deleted_at is null and o.status = 'ONLINE' and o.employee_number ~ '^[0-9]+$'),
                       (select max(o.id) from go_users o where o.login_id = u.login_id), u.id) as cid
                from go_users u
            ), d as (
              select ids.cid as uid, a.drafter_dept_id as did, a.drafter_dept_name as dn, a.drafted_at as ts
                from go_appr_documents a join ids on ids.id = a.drafter_id
               where a.drafter_dept_id is not null and a.drafted_at is not null
            ), m as (
              select distinct on (uid, mon) uid, mon, did, dn, first_ts, last_ts
                from (select uid, date_trunc('month', ts) as mon, did, max(dn) as dn, count(*) as n, min(ts) as first_ts, max(ts) as last_ts
                        from d group by 1, 2, 3) x
               order by uid, mon, n desc, first_ts
            ), g as (
              select *, case when did is distinct from lag(did) over (partition by uid order by mon) then 1 else 0 end as chg from m
            ), s as (
              select *, sum(chg) over (partition by uid order by mon) as grp from g
            )
            select uid, did, (array_agg(dn order by mon desc))[1] as dn, min(first_ts)::date as from_day, max(last_ts)::date as last_day
              from s group by uid, did, grp
             order by uid, from_day";
        await using (var cmd = new NpgsqlCommand(spanSql, conn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                spans.Add(new ApprovalSpan(r.GetInt64(0), r.GetInt64(1), S(r, 2), D(r, 3)!.Value, D(r, 4)!.Value));

        var depts = new List<DeptLife>();
        await using (var cmd = new NpgsqlCommand(
            "select id, parent_id, code, name, created_at::date, deleted_at::date from go_departments", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                depts.Add(new DeptLife(r.GetInt64(0), N(r, 1), S(r, 2), S(r, 3), D(r, 4), D(r, 5)));

        // 같은 부서 id의 이름 변경(그룹웨어는 개편 때 새 id보다 이름 변경이 더 많다)
        var names = new List<DeptNameSpan>();
        const string nameSql = @"
            with m as (
              select distinct on (did, mon) did, mon, dn, first_ts, last_ts
                from (select drafter_dept_id as did, date_trunc('month', drafted_at) as mon, drafter_dept_name as dn,
                             count(*) as n, min(drafted_at) as first_ts, max(drafted_at) as last_ts
                        from go_appr_documents
                       where drafter_dept_id is not null and drafted_at is not null and drafter_dept_name is not null
                       group by 1, 2, 3) x
               order by did, mon, n desc, first_ts
            ), g as (
              select *, case when dn is distinct from lag(dn) over (partition by did order by mon) then 1 else 0 end as chg from m
            ), s as (
              select *, sum(chg) over (partition by did order by mon) as grp from g
            )
            select did, dn, min(first_ts)::date, max(last_ts)::date from s group by did, dn, grp order by did, 3";
        await using (var cmd = new NpgsqlCommand(nameSql, conn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                names.Add(new DeptNameSpan(r.GetInt64(0), r.GetString(1), D(r, 2)!.Value, D(r, 3)!.Value));

        return new Backfill(spans, depts, names);
    }

    private static async Task<(int empDeact, int deptDeact, string? history)> UpsertAsync(
        string writeConn, List<Code> positions, List<Code> duties, List<Dept> departments, List<Emp> employees,
        DateOnly today, Func<Task<Backfill>> loadBackfill, CancellationToken ct)
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

        // 연락처(휴대폰·직통): org.employee_contact(server/db/org_contact.sql)가 있는 대상에만 적재
        await using (var chk = new NpgsqlCommand("select to_regclass('org.employee_contact') is not null", conn, tx))
            if (await chk.ExecuteScalarAsync(ct) is true)
                foreach (var e in employees)
                    await ExecAsync(conn, tx, ct,
                        @"insert into org.employee_contact (employee_id, mobile, direct_tel, updated_at) values (@id,@mobile,@direct, now())
                          on conflict (employee_id) do update set mobile=excluded.mobile, direct_tel=excluded.direct_tel, updated_at=now()
                          where org.employee_contact.mobile is distinct from excluded.mobile or org.employee_contact.direct_tel is distinct from excluded.direct_tel",
                        ("id", e.Id), ("mobile", (object?)e.Mobile), ("direct", (object?)e.DirectTel));

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

        string? history = null;
        await using (var chk = new NpgsqlCommand("select to_regclass('org.employee_dept_history') is not null and to_regclass('org.department_history') is not null", conn, tx))
            if (await chk.ExecuteScalarAsync(ct) is true)
                history = await RecordHistoryAsync(conn, tx, today, employees.Select(e => e.Id).ToHashSet(), loadBackfill, ct);

        await tx.CommitAsync(ct);
        return (empDeact, deptDeact, history);
    }

    /// <summary>
    /// 방금 덮어쓴 org.employee·org.department와 열린 이력(sync, valid_to null)을 비교한다.
    /// 달라졌거나 빠진 것은 어제로 닫고(오늘 연 행은 같은 날 재실행이므로 지운다) 오늘부터 새 행을 연다.
    /// sync 이력이 시작되기 전은 처음 한 번 결재(approval)·그룹웨어 부서(gw)로 채운다.
    /// </summary>
    private static async Task<string> RecordHistoryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateOnly today,
        HashSet<long> activeIds, Func<Task<Backfill>> loadBackfill, CancellationToken ct)
    {
        const string sameEmp = @"exists (select 1 from org.employee e where e.id = h.employee_id and e.is_active
                                          and e.department_id is not distinct from h.department_id and e.duty_id is not distinct from h.duty_id)";
        const string sameDept = @"exists (select 1 from org.department d where d.id = h.department_id and d.is_active
                                           and d.name = h.name and d.parent_id is not distinct from h.parent_id)";

        await CountAsync(conn, tx, ct, $"delete from org.employee_dept_history h where h.source = 'sync' and h.valid_to is null and h.valid_from = @today and not {sameEmp}", today);
        var empClosed = await CountAsync(conn, tx, ct, $"update org.employee_dept_history h set valid_to = @today - 1 where h.source = 'sync' and h.valid_to is null and not {sameEmp}", today);
        var empOpened = await CountAsync(conn, tx, ct, @"
            insert into org.employee_dept_history (employee_id, department_id, department_name, duty_id, valid_from, source)
            select e.id, e.department_id, d.name, e.duty_id, @today, 'sync'
              from org.employee e left join org.department d on d.id = e.department_id
             where e.is_active and not exists (select 1 from org.employee_dept_history h where h.employee_id = e.id and h.source = 'sync' and h.valid_to is null)", today);

        await CountAsync(conn, tx, ct, $"delete from org.department_history h where h.source = 'sync' and h.valid_to is null and h.valid_from = @today and not {sameDept}", today);
        var deptClosed = await CountAsync(conn, tx, ct, $"update org.department_history h set valid_to = @today - 1 where h.source = 'sync' and h.valid_to is null and not {sameDept}", today);
        var deptOpened = await CountAsync(conn, tx, ct, @"
            insert into org.department_history (department_id, name, parent_id, code, valid_from, source)
            select d.id, d.name, d.parent_id, d.code, @today, 'sync'
              from org.department d
             where d.is_active and not exists (select 1 from org.department_history h where h.department_id = d.id and h.source = 'sync' and h.valid_to is null)", today);

        var summary = $"직원 닫음 {empClosed} · 새로 엶 {empOpened} / 부서 닫음 {deptClosed} · 새로 엶 {deptOpened}";

        // 처음 한 번: sync 이력 시작일(baseline) 이전을 채운다. 겹치지 않게 baseline 전날까지만.
        await using (var chk = new NpgsqlCommand(
            "select (select min(valid_from) from org.employee_dept_history where source = 'sync'), " +
            "exists (select 1 from org.employee_dept_history where source = 'approval'), exists (select 1 from org.department_history where source = 'gw'), " +
            "exists (select 1 from org.department_history where source = 'approval')", conn, tx))
        await using (var r = await chk.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            var baseline = r.IsDBNull(0) ? today : r.GetFieldValue<DateOnly>(0);
            var hasApproval = r.GetBoolean(1);
            var hasGw = r.GetBoolean(2);
            var hasDeptNames = r.GetBoolean(3);
            await r.CloseAsync();
            if (hasApproval && hasGw && hasDeptNames) return summary;

            var b = await loadBackfill();
            if (!hasApproval) summary += $" / 결재 근사 소속 이력 {await InsertApprovalSpansAsync(conn, tx, b.Spans, baseline, activeIds, ct)}건";
            if (!hasGw) summary += $" / 그룹웨어 부서 생애 {await InsertDeptLivesAsync(conn, tx, b.Depts, baseline, ct)}건";
            if (!hasDeptNames) summary += $" / 결재 근사 부서 이름 이력 {await InsertDeptNamesAsync(conn, tx, b.DeptNames, b.Depts, baseline, ct)}건";
        }
        return summary;
    }

    private static async Task<int> InsertDeptNamesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<DeptNameSpan> spans, List<DeptLife> depts,
        DateOnly baseline, CancellationToken ct)
    {
        var last = baseline.AddDays(-1);
        var deleted = depts.Where(d => d.Deleted is not null).ToDictionary(d => d.Id, d => d.Deleted!.Value);
        var rows = new List<(long id, string name, DateOnly from, DateOnly to)>();
        foreach (var g in spans.GroupBy(s => s.DeptId))
        {
            var list = g.OrderBy(s => s.From).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                // 다음 이름 시작 전날까지. 마지막은 삭제된 부서면 삭제 전날, 살아 있으면 sync 시작 전날까지.
                var to = i + 1 < list.Count ? list[i + 1].From.AddDays(-1) : deleted.TryGetValue(s.DeptId, out var del) ? del.AddDays(-1) : last;
                if (to > last) to = last;
                if (s.From > to) continue;
                rows.Add((s.DeptId, s.Name, s.From, to));
            }
        }
        if (rows.Count == 0) return 0;
        await using var cmd = new NpgsqlCommand(@"
            insert into org.department_history (department_id, name, valid_from, valid_to, source)
            select *, 'approval' from unnest(@id, @name, @from, @to)", conn, tx);
        cmd.Parameters.AddWithValue("id", rows.Select(x => x.id).ToArray());
        cmd.Parameters.AddWithValue("name", rows.Select(x => x.name).ToArray());
        cmd.Parameters.AddWithValue("from", rows.Select(x => x.from).ToArray());
        cmd.Parameters.AddWithValue("to", rows.Select(x => x.to).ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertApprovalSpansAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<ApprovalSpan> spans,
        DateOnly baseline, HashSet<long> activeIds, CancellationToken ct)
    {
        var last = baseline.AddDays(-1);
        var rows = new List<(long emp, long dept, string? name, DateOnly from, DateOnly to)>();
        foreach (var g in spans.GroupBy(s => s.EmployeeId))
        {
            var list = g.OrderBy(s => s.From).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                // 다음 구간 시작 전날까지. 마지막 구간은 재직자면 sync 시작 전날까지, 퇴직자면 마지막 기안일까지.
                var to = i + 1 < list.Count ? list[i + 1].From.AddDays(-1) : activeIds.Contains(s.EmployeeId) ? last : s.LastDay;
                if (to > last) to = last;
                if (s.From > to) continue;
                rows.Add((s.EmployeeId, s.DeptId, s.DeptName, s.From, to));
            }
        }
        if (rows.Count == 0) return 0;
        await using var cmd = new NpgsqlCommand(@"
            insert into org.employee_dept_history (employee_id, department_id, department_name, valid_from, valid_to, source)
            select * , 'approval' from unnest(@emp, @dept, @name, @from, @to)", conn, tx);
        cmd.Parameters.AddWithValue("emp", rows.Select(x => x.emp).ToArray());
        cmd.Parameters.AddWithValue("dept", rows.Select(x => x.dept).ToArray());
        cmd.Parameters.AddWithValue("name", rows.Select(x => x.name).ToArray());
        cmd.Parameters.AddWithValue("from", rows.Select(x => x.from).ToArray());
        cmd.Parameters.AddWithValue("to", rows.Select(x => x.to).ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertDeptLivesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<DeptLife> depts, DateOnly baseline, CancellationToken ct)
    {
        var last = baseline.AddDays(-1);
        var rows = new List<(long id, string name, long? parent, string? code, DateOnly from, DateOnly to)>();
        foreach (var d in depts)
        {
            if (d.Name is null || d.Created is not { } from) continue;
            // 삭제된 부서는 삭제 전날까지, 살아 있는 부서는 sync 시작 전날까지(그 뒤는 sync 행)
            var to = d.Deleted is { } del ? del.AddDays(-1) : last;
            if (to > last) to = last;
            if (from > to) continue;
            rows.Add((d.Id, d.Name, d.ParentId, d.CodeStr, from, to));
        }
        if (rows.Count == 0) return 0;
        await using var cmd = new NpgsqlCommand(@"
            insert into org.department_history (department_id, name, parent_id, code, valid_from, valid_to, source)
            select *, 'gw' from unnest(@id, @name, @parent, @code, @from, @to)", conn, tx);
        cmd.Parameters.AddWithValue("id", rows.Select(x => x.id).ToArray());
        cmd.Parameters.AddWithValue("name", rows.Select(x => x.name).ToArray());
        cmd.Parameters.AddWithValue("parent", rows.Select(x => x.parent).ToArray());
        cmd.Parameters.AddWithValue("code", rows.Select(x => x.code).ToArray());
        cmd.Parameters.AddWithValue("from", rows.Select(x => x.from).ToArray());
        cmd.Parameters.AddWithValue("to", rows.Select(x => x.to).ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> CountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql, DateOnly today)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("today", today);
        return await cmd.ExecuteNonQueryAsync(ct);
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
    // 그룹웨어 DB는 ::date 도 timestamp 로 돌려주므로 DateTime 으로 읽어 날짜만 쓴다(PG date 도 그대로 읽힘).
    private static DateOnly? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
}
