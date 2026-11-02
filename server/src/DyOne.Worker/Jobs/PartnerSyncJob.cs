using Microsoft.Extensions.Options;
using Npgsql;

namespace DyOne.Worker.Jobs;

public class PartnerSyncOptions
{
    /// <summary>매일 실행 시각 (HH:mm). 기본 08:10(조직도 08:00 다음).</summary>
    public string DailyAt { get; set; } = "08:10";
    public string TimeZone { get; set; } = "Korea Standard Time";
    /// <summary>그룹웨어 회사 공용 주소록 id(go_contact_books). 두림야스카와 회사 주소록 = 15.</summary>
    public long BookId { get; set; } = 15;
    /// <summary>이 메일 도메인의 연락처는 협력사가 아니다(우리 직원).</summary>
    public string[] ExcludeDomains { get; set; } = ["doolim-yaskawa.com"];
    public bool RunOnStartup { get; set; }
    /// <summary>true면 조회·집계만 하고 DB에 쓰지 않는다(검증용).</summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// 그룹웨어(tims) 회사 공용 주소록 → dyone partner 스키마(server/db/partner.sql). 매일 지정 시각 실행. docs/partner-access 3-1.
/// 원천(조회 전용): go_contact_groups(루트 아래 그룹 = 협력사 회사) · go_contacts · go_contact_has_contact_group.
/// 대상(쓰기): partner.company / partner.person을 그룹웨어 id 기준 upsert. 주소록에서 빠지면 is_active=false(로그인 즉시 정지).
///   - 명단 칸(이름·이메일·소속·직위·회사)만 쓴다. 비밀번호 해시·로그인 중지는 API 관리자 화면 몫이라 건드리지 않는다.
///   - 이메일 없는 연락처, 우리 직원 메일(ExcludeDomains), 그룹웨어 계정 메일과 같은 연락처는 제외. 같은 이메일이 둘이면 id가 작은 쪽.
///   - partner 스키마가 없는 대상은 건너뛴다(스크립트 적용 전).
/// </summary>
public class PartnerSyncJob(IOptions<PartnerSyncOptions> options, IConfiguration config, ILogger<PartnerSyncJob> logger)
    : DailyJob(TimeOnly.Parse(options.Value.DailyAt), options.Value.TimeZone)
{
    private readonly PartnerSyncOptions _opt = options.Value;

    public override string Name => "partner-sync";
    public override bool RunOnStartup => _opt.RunOnStartup;

    private sealed record Company(long Id, string Name);
    private sealed record Person(long Id, long CompanyId, string Name, string Email, string? Department, string? Position);

    public override async Task RunAsync(CancellationToken ct)
    {
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");
        var (companies, people, skipped) = await ReadTimsAsync(gwConn, ct);
        logger.LogInformation("협력사 주소록 조회(tims, 주소록 {Book}): 회사 {C} · 사람 {P} (제외 {S})", _opt.BookId, companies.Count, people.Count, skipped);

        if (companies.Count == 0 || people.Count == 0)
        {
            logger.LogWarning("협력사 회사·사람 0건 조회 → 안전을 위해 적재를 건너뛴다(전원 정지 방지).");
            return;
        }
        if (_opt.DryRun)
        {
            logger.LogInformation("DryRun: 적재 생략. 회사별 {Sample}",
                string.Join(" / ", companies.Select(c => $"{c.Name} {people.Count(p => p.CompanyId == c.Id)}")));
            return;
        }

        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");
        Exception? firstError = null;
        foreach (var (name, conn) in targets)
        {
            try
            {
                var r = await UpsertAsync(conn, companies, people, ct);
                if (r is null) { logger.LogWarning("[{Target}] partner 스키마 없음(server/db/partner.sql 적용 전) — 건너뜀", name); continue; }
                logger.LogInformation("[{Target}] 협력사 동기화 완료: 회사 {C} · 사람 {P} 적재 / 정지 회사 -{CD} · 사람 -{PD}",
                    name, companies.Count, people.Count, r.Value.companyOff, r.Value.personOff);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
                logger.LogError(ex, "[{Target}] 협력사 적재 실패", name);
            }
        }
        if (firstError is not null) throw firstError;
    }

    private async Task<(List<Company>, List<Person>, int skipped)> ReadTimsAsync(string gwConn, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using (var ro = new NpgsqlCommand("SET default_transaction_read_only = on", conn))
            await ro.ExecuteNonQueryAsync(ct); // 소스 쓰기 차단(안전장치)

        var companies = new List<Company>();
        await using (var cmd = new NpgsqlCommand(
            "select id, name from go_contact_groups where book_id = @book and deleted_at is null and parent_id is not null order by id", conn))
        {
            cmd.Parameters.AddWithValue("book", _opt.BookId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) companies.Add(new Company(r.GetInt64(0), r.GetString(1).Trim()));
        }

        const string sql = @"
            select distinct on (ct.id) ct.id, h.contact_group_id,
                   coalesce(nullif(trim(ct.name), ''), trim(coalesce(ct.last_name, '') || coalesce(ct.first_name, ''))) as person_name,
                   lower(trim(ct.email)) as email, nullif(trim(ct.department_name), '') as dept, nullif(trim(ct.position_name), '') as pos,
                   exists (select 1 from go_users u
                             join mail_user mu on mu.mail_user_seq = u.mail_user_seq
                             join mail_domain d on d.mail_domain_seq = mu.mail_domain_seq
                            where u.deleted_at is null and lower(mu.mail_uid || '@' || d.mail_domain) = lower(trim(ct.email))) as is_gw_user
              from go_contacts ct
              join go_contact_has_contact_group h on h.contact_id = ct.id
              join go_contact_groups g on g.id = h.contact_group_id and g.deleted_at is null and g.parent_id is not null
             where ct.book_id = @book and ct.deleted_at is null
             order by ct.id, h.contact_group_id";
        var people = new List<Person>();
        var seen = new HashSet<string>();
        var skipped = 0;
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("book", _opt.BookId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var email = r.IsDBNull(3) ? "" : r.GetString(3);
                var domain = email.Contains('@') ? email[(email.IndexOf('@') + 1)..] : "";
                var name = r.IsDBNull(2) ? "" : r.GetString(2);
                if (domain.Length == 0 || name.Length == 0 || r.GetBoolean(6)
                    || _opt.ExcludeDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) || !seen.Add(email))
                {
                    skipped++;
                    continue;
                }
                people.Add(new Person(r.GetInt64(0), r.GetInt64(1), name, email,
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
            }
        }
        return (companies, people, skipped);
    }

    /// <returns>정지한 회사·사람 수. partner 스키마가 없으면 null.</returns>
    private static async Task<(int companyOff, int personOff)?> UpsertAsync(string writeConn, List<Company> companies, List<Person> people, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using (var chk = new NpgsqlCommand("select to_regclass('partner.person') is not null", conn))
            if (await chk.ExecuteScalarAsync(ct) is not true) return null;

        await using var tx = await conn.BeginTransactionAsync(ct);
        async Task<int> Exec(string sql, params (string, object)[] args)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var c in companies)
            await Exec(@"insert into partner.company (id, name) values (@id, @name)
                         on conflict (id) do update set name = excluded.name, is_active = true, synced_at = now(),
                           updated_at = case when partner.company.name is distinct from excluded.name or not partner.company.is_active then now() else partner.company.updated_at end",
                ("id", c.Id), ("name", c.Name));
        var companyOff = await Exec("update partner.company set is_active = false, updated_at = now() where is_active and not (id = any(@ids))",
            ("ids", companies.Select(c => c.Id).ToArray()));

        // 빠진 사람을 먼저 정지해야, 같은 이메일이 다른 연락처로 옮겨 왔을 때 활성 이메일 유일 인덱스에 걸리지 않는다.
        var personOff = await Exec("update partner.person set is_active = false, updated_at = now() where is_active and not (id = any(@ids))",
            ("ids", people.Select(p => p.Id).ToArray()));
        foreach (var p in people)
            await Exec(@"insert into partner.person (id, company_id, name, email, department_name, position_name)
                         values (@id, @cid, @name, @email, @dept, @pos)
                         on conflict (id) do update set company_id = excluded.company_id, name = excluded.name, email = excluded.email,
                           department_name = excluded.department_name, position_name = excluded.position_name, is_active = true, synced_at = now(),
                           updated_at = case when (partner.person.company_id, partner.person.name, partner.person.email, partner.person.department_name, partner.person.position_name, partner.person.is_active)
                                                  is distinct from (excluded.company_id, excluded.name, excluded.email, excluded.department_name, excluded.position_name, true)
                                             then now() else partner.person.updated_at end",
                ("id", p.Id), ("cid", p.CompanyId), ("name", p.Name), ("email", p.Email),
                ("dept", (object?)p.Department ?? DBNull.Value), ("pos", (object?)p.Position ?? DBNull.Value));

        await Exec(@"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', 'partner', to_char(now(), 'YYYYMMDD'), now())
                     on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()");
        await tx.CommitAsync(ct);
        return (companyOff, personOff);
    }
}
