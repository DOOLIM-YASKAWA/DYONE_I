using System.Security.Claims;
using System.Text.Json;
using DyOne.Api.Modules.Notifications;
using DyOne.Api.Platform;
using DyOne.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DyOne.Api.Modules.Projects;

/// <summary>메인 화면 한 줄(내 관심·회사 주요 공통). ProjectId가 없으면 ERP에만 있는 코드(상세 화면 없음).</summary>
public record MainRowDto(
    string Code, string CodeLabel, long? ProjectId, string Name, string? PartnerName, string? ErpStatus, string? Stage, string? PmName,
    decimal? AmountEok, string? Next, DateTimeOffset? LastActivityAt, string? LastActivityLabel,
    string? Category = null, int? Rank = null, int? Score = null, string? Reason = null, JsonElement? Parts = null, JsonElement? Signals = null,
    string? Decision = null, bool Favorite = false);

public record BriefDto(string Code, string CodeLabel, long? ProjectId, string Name, string Sentence, string Source, int ChangedYesterday, JsonElement? Evidence, DateTimeOffset MadeAt);

/// <param name="Selected">회사 주요: 선정된 건수(Items 앞쪽). 나머지 Items는 '추천안'(선정 전 후보, Decision 없음)</param>
public record MainListDto(int Total, IReadOnlyList<MainRowDto> Items, string? Mode = null, int Selected = 0);

public record MainDto(
    int ErpOrdered, int ErpSales, MainListDto Favorites, MainListDto Featured,
    IReadOnlyList<BriefDto> FavBriefs, IReadOnlyList<BriefDto> FeatBriefs, DateTimeOffset? BriefAt, DateOnly? RunDate, int Candidates, bool CanSelect,
    IReadOnlyList<ScheduleDto>? Schedule = null, int ScheduleTotal = 0);

/// <summary>일정 주의 한 줄. Kind: late(지연 의심) · soon(일정 임박) · after_sop(양산 후 작업 중) · erp(ERP 기간 갱신 필요, 관리자만).
/// Days = 오늘 − 기준일(+면 지남, −면 남음). Scope: fav(내 관심) · feat(회사 주요) · other</summary>
public record ScheduleDto(string Code, string CodeLabel, long? ProjectId, string Name, string Kind, string Stage, string StageName,
    DateOnly DueDate, int Days, string Reason, JsonElement? Evidence, string Scope, DateOnly FirstSeen);

/// <param name="All">true = 회사 전체에서 숨기기(관리자만), false = 나만</param>
public record ScheduleHide(string Code, string Kind, bool All = false, string? Note = null);

/// <param name="Action">select(선정) · exclude(후보에서 제외) · clear(선정·제외 해제)</param>
public record FeaturedDecision(string Code, string Action, int? Rank = null, string? Note = null);

/// <summary>
/// 프로젝트 메인(첫 화면): 오늘의 브리핑 · 내 관심(★) · 회사 주요(영업 부서 선정, 선정 전엔 시스템 추천) · 새 후보 검토.
/// 숫자·문장은 Worker(project-main 잡)가 매일 만든 표를 읽기만 한다. API가 쓰는 것은 ★와 선정뿐.
/// 선정 권한 = access 'project.featured'(부서·사람) + 관리자('*'). 설계 docs/roadmap/project-main-design.md
/// </summary>
public static class ProjectMainModule
{
    public const string FeaturedApp = "project.featured";
    /// <summary>첫 화면 카드·브리핑에 보내는 줄 수. 화면은 3줄씩 넘겨 보여 준다(자동 넘김, 11-04).</summary>
    private const int ShowFav = 9, ShowFeat = 12;

    public static void MapProjectMain(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").WithTags("ProjectMain");

        g.MapGet("/main", async (ClaimsPrincipal user, IConfiguration config, AppAudience audience, DyOneDbContext db, CancellationToken ct) =>
        {
            var me = long.Parse(user.Current().Id);
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var (ordered, sales) = await ErpCountsAsync(conn, ct);
            var favs = await FavoritesAsync(conn, db, me, ct);
            var (featured, mode, runDate) = await FeaturedAsync(conn, db, me, ct);
            var selectedCount = featured.Count(r => r.Decision == "selected");
            var favBriefs = await BriefsAsync(conn, favs.Take(ShowFav).ToList(), ct);
            var featBriefs = await BriefsAsync(conn, featured.Take(ShowFeat).ToList(), ct);
            var candidates = runDate is null ? 0 : await ScalarAsync(conn, """
                select count(*)::int from project.featured_suggestion s
                 where s.run_date = @d and s.rank <= 10
                   and not exists (select 1 from project.featured f where f.code = s.code and f.ended_at is null)
                """, ("d", runDate.Value), ct);
            var briefAt = favBriefs.Concat(featBriefs).Select(b => (DateTimeOffset?)b.MadeAt).Max();
            var schedule = await ScheduleAsync(conn, config, me, false, favs, featured, mineOnly: true, ct);
            return new MainDto(ordered, sales,
                new MainListDto(favs.Count, favs.Take(ShowFav).ToList()),
                new MainListDto(featured.Count, featured.Take(ShowFeat).ToList(), mode, selectedCount),
                favBriefs, featBriefs, briefAt, runDate, candidates, await CanSelectAsync(audience, me, ct),
                schedule.Take(ShowFav).ToList(), schedule.Count);
        });

        g.MapGet("/favorites", async (ClaimsPrincipal user, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var rows = await FavoritesAsync(conn, db, long.Parse(user.Current().Id), ct);
            return new MainListDto(rows.Count, rows);
        });

        // 목록 ★ 표시용: 내가 ★한 프로젝트 id
        g.MapGet("/favorites/ids", async (ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var ids = new List<long>();
            await using var cmd = new NpgsqlCommand("select project_id from project.favorite where employee_id = @me", conn);
            cmd.Parameters.AddWithValue("me", long.Parse(user.Current().Id));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
            return ids;
        });

        g.MapPut("/{id:long}/favorite", async (long id, ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(WriteConn(config));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                insert into project.favorite (employee_id, project_id)
                select @me, p.id from project.project p where p.id = @id and p.is_active
                on conflict do nothing
                """, conn);
            cmd.Parameters.AddWithValue("me", long.Parse(user.Current().Id));
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:long}/favorite", async (long id, ClaimsPrincipal user, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(WriteConn(config));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("delete from project.favorite where employee_id = @me and project_id = @id", conn);
            cmd.Parameters.AddWithValue("me", long.Parse(user.Current().Id));
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/featured", async (ClaimsPrincipal user, IConfiguration config, DyOneDbContext db, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var (rows, mode, _) = await FeaturedAsync(conn, db, long.Parse(user.Current().Id), ct);
            return new MainListDto(rows.Count, rows, mode, rows.Count(r => r.Decision == "selected"));
        });

        // 새 후보 검토(선정 담당·관리자): 오늘 후보 상위 10건 + 선정·제외 상태
        g.MapGet("/featured/candidates", async (ClaimsPrincipal user, IConfiguration config, AppAudience audience, DyOneDbContext db, CancellationToken ct) =>
        {
            var me = long.Parse(user.Current().Id);
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var rows = await SuggestionsAsync(conn, db, me, 10, ct);
            return new { canSelect = await CanSelectAsync(audience, me, ct), items = rows };
        });

        // 일정 주의 전체(서랍): scope=mine(관심·주요) · all(회사 전체). 관리자는 ERP 기간 갱신 필요도 본다
        g.MapGet("/schedule-alerts", async (string? scope, ClaimsPrincipal user, IConfiguration config, AppAudience audience, DyOneDbContext db, CancellationToken ct) =>
        {
            var me = long.Parse(user.Current().Id);
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var favs = await FavoritesAsync(conn, db, me, ct);
            var (featured, _, _) = await FeaturedAsync(conn, db, me, ct);
            var admin = await audience.IsAdminAsync(me, ct);
            var items = await ScheduleAsync(conn, config, me, admin, favs, featured, mineOnly: scope != "all", ct);
            return new { items, isAdmin = admin };
        });

        g.MapPost("/schedule-alerts/hide", async (ScheduleHide body, ClaimsPrincipal user, IConfiguration config, AppAudience audience, CancellationToken ct) =>
        {
            var me = long.Parse(user.Current().Id);
            if (body.All && !await audience.IsAdminAsync(me, ct)) return Results.Json(new { message = "회사 전체 숨기기는 관리자만 할 수 있습니다." }, statusCode: 403);
            await using var conn = new NpgsqlConnection(WriteConn(config));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                insert into project.schedule_hide (code, kind, employee_id, note) values (@c, @k, @e, @n)
                on conflict (code, kind, coalesce(employee_id, 0)) do update set note = excluded.note, hidden_at = now()
                """, conn);
            cmd.Parameters.AddWithValue("c", body.Code);
            cmd.Parameters.AddWithValue("k", body.Kind);
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = body.All ? DBNull.Value : me });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)body.Note ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/featured", async (FeaturedDecision body, ClaimsPrincipal user, IConfiguration config, AppAudience audience, CancellationToken ct) =>
        {
            var me = long.Parse(user.Current().Id);
            if (!await CanSelectAsync(audience, me, ct)) return Results.Json(new { message = "회사 주요 프로젝트를 정할 권한이 없습니다(영업 부서·관리자)." }, statusCode: 403);
            if (body.Action is not ("select" or "exclude" or "clear")) return Results.BadRequest(new { message = "action은 select · exclude · clear 중 하나입니다." });
            await using var conn = new NpgsqlConnection(WriteConn(config));
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var end = new NpgsqlCommand("update project.featured set ended_at = now(), ended_by = @me where code = @c and ended_at is null", conn, tx))
            {
                end.Parameters.AddWithValue("me", me);
                end.Parameters.AddWithValue("c", body.Code);
                await end.ExecuteNonQueryAsync(ct);
            }
            if (body.Action != "clear")
            {
                await using var ins = new NpgsqlCommand("""
                    insert into project.featured (code, rank, status, note, decided_by)
                    values (@c, coalesce(@rk, (select coalesce(max(rank), 0) + 1 from project.featured where status = 'selected' and ended_at is null)), @s, @n, @me)
                    """, conn, tx);
                ins.Parameters.AddWithValue("c", body.Code);
                ins.Parameters.Add(new NpgsqlParameter("rk", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)body.Rank ?? DBNull.Value });
                ins.Parameters.AddWithValue("s", body.Action == "select" ? "selected" : "excluded");
                ins.Parameters.Add(new NpgsqlParameter("n", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)body.Note ?? DBNull.Value });
                ins.Parameters.AddWithValue("me", me);
                await ins.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private static string WriteConn(IConfiguration config) =>
        new NpgsqlConnectionStringBuilder(config.GetConnectionString("DyOne")) { Options = null }.ConnectionString;

    private static async Task<bool> CanSelectAsync(AppAudience audience, long me, CancellationToken ct) =>
        await audience.RecipientsAsync(FeaturedApp, ct) is { } ids && ids.Contains(me);

    private static async Task<(int, int)> ErpCountsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand("select count(*) filter (where status_code = '002')::int, count(*) filter (where status_code = '001')::int from sync.erp_project", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetInt32(0), r.GetInt32(1));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return (0, 0); }
    }

    private static async Task<int> ScalarAsync(NpgsqlConnection conn, string sql, (string, object) p, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(p.Item1, p.Item2);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    /// <summary>옛 코드(project_alias.old_code) 목록과, 코드 → 묶음 대표 코드(옛 코드면 새 코드 중 가장 작은 것).</summary>
    private static async Task<(HashSet<string> Old, Dictionary<string, string> Key)> AliasAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var old = new HashSet<string>();
        var key = new Dictionary<string, string>();
        await using var cmd = new NpgsqlCommand("select old_code, min(new_code) from project.project_alias group by old_code", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { old.Add(r.GetString(0)); key[r.GetString(0)] = r.GetString(1); }
        return (old, key);
    }

    private static string Label(string code, IReadOnlyCollection<string>? codes, HashSet<string> old)
    {
        var news = (codes ?? [code]).Where(c => !old.Contains(c)).Order().ToList();
        if (news.Count <= 1) return code;
        var last = news[^1];
        return $"{news[0]}~{last[(last.LastIndexOf('-') + 1)..]}";
    }

    /// <summary>ERP 미러·그룹웨어 미러·최근 활동·다음 일정(브리핑 사실)을 붙여 한 줄을 만든다.</summary>
    private static async Task<List<MainRowDto>> RowsAsync(NpgsqlConnection conn, DyOneDbContext db, IReadOnlyList<(string Code, string[] Codes)> items, long me, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var (old, _) = await AliasAsync(conn, ct);
        var all = items.SelectMany(i => i.Codes.Append(i.Code)).Distinct().ToArray();
        var erp = new Dictionary<string, (string? Name, string? Partner, string? Status, string? Pm, decimal Amount)>();
        await using (var cmd = new NpgsqlCommand("select cd_prjt, name, partner_name, status_name, pm_name, coalesce(contract_amount, 0) from sync.erp_project where cd_prjt = any(@c)", conn))
        {
            cmd.Parameters.AddWithValue("c", all);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                erp[r.GetString(0)] = (r.IsDBNull(1) ? null : r.GetString(1).Trim(), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5));
        }
        var next = new Dictionary<string, string>();
        await using (var cmd = new NpgsqlCommand("""
            select distinct on (code) code, facts->>'다음 일정' from project.status_brief where code = any(@c) order by code, made_at desc
            """, conn))
        {
            cmd.Parameters.AddWithValue("c", items.Select(i => i.Code).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (!r.IsDBNull(1)) next[r.GetString(0)] = r.GetString(1);
        }
        var favIds = new HashSet<long>();
        await using (var cmd = new NpgsqlCommand("select project_id from project.favorite where employee_id = @me", conn))
        {
            cmd.Parameters.AddWithValue("me", me);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) favIds.Add(r.GetInt64(0));
        }
        var projects = await db.Projects.Where(p => all.Contains(p.Code) && p.IsActive).ToListAsync(ct);
        var dtos = (await ProjectsModule.ToDtosAsync(db, projects, ct)).ToDictionary(d => d.Code);

        return items.Select(i =>
        {
            // 상세로 열 프로젝트: 대표 코드 → 묶음 안 그룹웨어에 있는 첫 코드
            var dto = dtos.GetValueOrDefault(i.Code) ?? i.Codes.Order().Select(c => dtos.GetValueOrDefault(c)).FirstOrDefault(d => d is not null);
            var e = erp.GetValueOrDefault(i.Code);
            return new MainRowDto(i.Code, Label(i.Code, i.Codes, old), dto?.Id, e.Name ?? dto?.Name ?? i.Code, e.Partner ?? dto?.PartnerName, e.Status,
                dto?.EffectiveStage, dto?.PmName ?? e.Pm, null, next.GetValueOrDefault(i.Code), // 계약 금액은 전 직원 화면에 보내지 않는다(11-04 사용자). 규모는 근거 보기의 쉬운 말로
                dto?.LastChangedAt, dto?.LastChangedSource, Favorite: dto is not null && favIds.Contains(dto.Id));
        }).ToList();
    }

    /// <summary>내 관심(★) — 최근 활동 순.</summary>
    private static async Task<List<MainRowDto>> FavoritesAsync(NpgsqlConnection conn, DyOneDbContext db, long me, CancellationToken ct)
    {
        var codes = new List<string>();
        await using (var cmd = new NpgsqlCommand("select p.code from project.favorite f join project.project p on p.id = f.project_id where f.employee_id = @me and p.is_active", conn))
        {
            cmd.Parameters.AddWithValue("me", me);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        }
        // ★는 내가 누른 코드 그대로 보여 준다(옛 코드면 브리핑만 새 코드 묶음에서 가져온다)
        var rows = await RowsAsync(conn, db, codes.Select(c => (c, new[] { c })).ToList(), me, ct);
        return rows.Select(r => r with { Favorite = true })
            .OrderBy(r => r.LastActivityAt is null).ThenByDescending(r => r.LastActivityAt).ToList();
    }

    /// <summary>회사 주요 — 선정된 것(순위 순). 하나도 없으면 오늘 시스템 추천(후보 순위, 제외한 것 빼고).</summary>
    private static async Task<(List<MainRowDto> Rows, string Mode, DateOnly? RunDate)> FeaturedAsync(NpgsqlConnection conn, DyOneDbContext db, long me, CancellationToken ct)
    {
        var runDate = await LatestRunAsync(conn, ct);
        var sel = new List<(string Code, int Rank)>();
        await using (var cmd = new NpgsqlCommand("select code, coalesce(rank, 999) from project.featured where status = 'selected' and ended_at is null order by rank nulls last, decided_at", conn))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sel.Add((r.GetString(0), r.GetInt32(1)));
        }
        if (sel.Count == 0) return (await SuggestionsAsync(conn, db, me, 10, ct, skipExcluded: true), "recommended", runDate);
        var sugg = (await SuggestionsAsync(conn, db, me, 100, ct)).ToDictionary(s => s.Code);
        var codesOf = new Dictionary<string, string[]>();
        if (runDate is not null)
        {
            await using var cmd = new NpgsqlCommand("select code, codes from project.featured_suggestion where run_date = @d and code = any(@c)", conn);
            cmd.Parameters.AddWithValue("d", runDate.Value);
            cmd.Parameters.AddWithValue("c", sel.Select(s => s.Code).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) codesOf[r.GetString(0)] = r.GetFieldValue<string[]>(1);
        }
        var rows = await RowsAsync(conn, db, sel.Select(s => (s.Code, codesOf.GetValueOrDefault(s.Code) ?? [s.Code])).ToList(), me, ct);
        var selected = rows.Select((r, i) =>
        {
            var s = sugg.GetValueOrDefault(r.Code);
            return r with { Name = s?.Name ?? r.Name, Rank = i + 1, Category = s?.Category, Score = s?.Score, Reason = s?.Reason, Parts = s?.Parts, Signals = s?.Signals, Decision = "selected" };
        }).ToList();
        // 선정된 것 뒤에 '추천안'(오늘 후보 중 선정·제외하지 않은 것) — 11-04 사용자: 선정 위주로 보이되 나머지도 추천안으로 계속 표시
        var picked = selected.Select(r => r.Code).ToHashSet();
        var recommended = (await SuggestionsAsync(conn, db, me, 10 + picked.Count, ct, skipExcluded: true))
            .Where(r => !picked.Contains(r.Code)).Take(10).Select((r, i) => r with { Rank = i + 1, Decision = null });
        return ([.. selected, .. recommended], "selected", runDate);
    }

    private static async Task<DateOnly?> LatestRunAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select max(run_date) from project.featured_suggestion", conn);
        return await cmd.ExecuteScalarAsync(ct) switch { DateOnly d => d, DateTime t => DateOnly.FromDateTime(t), _ => null };
    }

    /// <summary>오늘(가장 최근 계산일) 후보 상위 n건. skipExcluded면 '제외'한 것은 빼고 순위를 다시 붙인다.</summary>
    private static async Task<List<MainRowDto>> SuggestionsAsync(NpgsqlConnection conn, DyOneDbContext db, long me, int n, CancellationToken ct, bool skipExcluded = false)
    {
        var runDate = await LatestRunAsync(conn, ct);
        if (runDate is null) return [];
        var list = new List<(string Code, string[] Codes, string Name, int Rank, int Score, string Cat, string? Reason, string Parts, string? Signals, string? Decision)>();
        await using (var cmd = new NpgsqlCommand("""
            select s.code, s.codes, s.name, s.rank, s.score, s.category, s.reason, s.parts::text, s.signals::text, f.status
              from project.featured_suggestion s
              left join project.featured f on f.code = s.code and f.ended_at is null
             where s.run_date = @d and (not @skip or f.status is distinct from 'excluded')
             order by s.rank limit @n
            """, conn))
        {
            cmd.Parameters.AddWithValue("d", runDate.Value);
            cmd.Parameters.AddWithValue("skip", skipExcluded);
            cmd.Parameters.AddWithValue("n", n);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add((r.GetString(0), r.GetFieldValue<string[]>(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9)));
        }
        var rows = (await RowsAsync(conn, db, list.Select(x => (x.Code, x.Codes)).ToList(), me, ct)).ToDictionary(r => r.Code);
        return list.Select((x, i) => rows[x.Code] with
        {
            Name = x.Name, Rank = skipExcluded ? i + 1 : x.Rank, Score = x.Score, Category = x.Cat, Reason = x.Reason,
            Parts = JsonDocument.Parse(x.Parts).RootElement, Signals = x.Signals is null ? null : JsonDocument.Parse(x.Signals).RootElement, Decision = x.Decision,
        }).ToList();
    }

    /// <summary>줄마다 최신 '지금 상태' 한 줄. 옛 코드(★)는 새 코드 묶음의 브리핑을 쓴다.</summary>
    private static async Task<List<BriefDto>> BriefsAsync(NpgsqlConnection conn, List<MainRowDto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var (_, key) = await AliasAsync(conn, ct);
        var want = rows.ToDictionary(r => r.Code, r => key.GetValueOrDefault(r.Code, r.Code));
        var map = new Dictionary<string, (string S, string Src, int Cy, string? Ev, DateTime At)>();
        await using (var cmd = new NpgsqlCommand("""
            select distinct on (code) code, sentence, source, changed_yesterday, evidence::text, made_at
              from project.status_brief where code = any(@c) order by code, made_at desc
            """, conn))
        {
            cmd.Parameters.AddWithValue("c", want.Values.Distinct().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                map[r.GetString(0)] = (r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetDateTime(5));
        }
        return rows.Where(r => map.ContainsKey(want[r.Code])).Select(r =>
        {
            var b = map[want[r.Code]];
            return new BriefDto(r.Code, r.CodeLabel, r.ProjectId, r.Name, b.S, b.Src, b.Cy, b.Ev is null ? null : JsonDocument.Parse(b.Ev).RootElement, new DateTimeOffset(b.At, TimeSpan.Zero));
        }).ToList();
    }

    private static readonly Dictionary<string, string> StageNames = new()
    {
        ["olt"] = "OLT", ["ship"] = "선적", ["install"] = "현장 설치", ["commission"] = "시운전", ["pre"] = "시생산", ["sop"] = "양산",
    };

    /// <summary>
    /// 일정 주의(Worker가 매일 판정한 project.schedule_alert). 처음 나온 날부터 유예(ProjectMain:ScheduleGraceDays, 기본 14일) 뒤에 보인다.
    /// 일정 임박은 유예 없이 바로. 숨긴 것(나만·회사 전체)은 뺀다. ERP 기간 갱신 필요는 관리자(includeErp)만.
    /// 순서: 지연 의심(오래 지난 순) → 일정 임박(가까운 순) → 양산 후 작업 중.
    /// </summary>
    private static async Task<List<ScheduleDto>> ScheduleAsync(NpgsqlConnection conn, IConfiguration config, long me, bool includeErp,
        List<MainRowDto> favs, List<MainRowDto> featured, bool mineOnly, CancellationToken ct)
    {
        var grace = int.TryParse(config["ProjectMain:ScheduleGraceDays"], out var gd) ? gd : 14;
        var (old, key) = await AliasAsync(conn, ct);
        var favKeys = favs.Select(r => key.GetValueOrDefault(r.Code, r.Code)).ToHashSet();
        var featKeys = featured.Select(r => r.Code).ToHashSet();
        var rows = new List<(string Code, string Kind, string Stage, DateOnly Due, int Days, string Reason, string? Ev, DateOnly First)>();
        try
        {
            await using var cmd = new NpgsqlCommand("""
                select a.code, a.kind, a.stage, a.due_date, (current_date - a.due_date)::int, a.reason, a.evidence::text, a.first_seen
                  from project.schedule_alert a
                 where (a.kind <> 'erp' or @erp)
                   and (a.kind = 'soon' or a.first_seen + @grace <= current_date)
                   and not exists (select 1 from project.schedule_hide h where h.code = a.code and h.kind = a.kind and (h.employee_id is null or h.employee_id = @me))
                """, conn);
            cmd.Parameters.AddWithValue("erp", includeErp);
            cmd.Parameters.AddWithValue("grace", grace);
            cmd.Parameters.AddWithValue("me", me);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetInt32(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), DateOnly.FromDateTime(r.GetDateTime(7))));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { return []; }
        if (mineOnly) rows = rows.Where(x => favKeys.Contains(x.Code) || featKeys.Contains(x.Code)).ToList();
        if (rows.Count == 0) return [];

        // 이름·코드·상세 id: 회사 주요 줄 › 오늘 후보 › ERP 이름
        var known = favs.Concat(featured).GroupBy(r => key.GetValueOrDefault(r.Code, r.Code)).ToDictionary(g => g.Key, g => g.First());
        var names = new Dictionary<string, (string Name, string[] Codes, long? Pid)>();
        await using (var cmd = new NpgsqlCommand("""
            select distinct on (c.code) c.code, coalesce(s.name, e.name, p.name, c.code), coalesce(s.codes, array[c.code]), coalesce(s.project_id, p.id)
              from unnest(@c) c(code)
              left join project.featured_suggestion s on s.code = c.code and s.run_date = (select max(run_date) from project.featured_suggestion)
              left join sync.erp_project e on e.cd_prjt = c.code
              left join project.project p on p.code = c.code and p.is_active
            """, conn))
        {
            cmd.Parameters.AddWithValue("c", rows.Select(x => x.Code).Distinct().ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                names[r.GetString(0)] = (r.GetString(1).Trim(), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetInt64(3));
        }
        int Order(string k) => k switch { "late" => 0, "soon" => 1, "after_sop" => 2, _ => 3 };
        return rows
            .OrderBy(x => Order(x.Kind)).ThenByDescending(x => x.Days) // 지연은 오래 지난 순, 임박은 가까운 순(Days가 0에 가까울수록 큼)
            .Select(x =>
            {
                known.TryGetValue(x.Code, out var k);
                var n = names.GetValueOrDefault(x.Code);
                var scope = favKeys.Contains(x.Code) ? "fav" : featKeys.Contains(x.Code) ? "feat" : "other";
                return new ScheduleDto(x.Code, k?.CodeLabel ?? Label(x.Code, n.Codes, old), k?.ProjectId ?? n.Pid, k?.Name ?? n.Name ?? x.Code, x.Kind, x.Stage,
                    StageNames.GetValueOrDefault(x.Stage, x.Stage), x.Due, x.Days, x.Reason, x.Ev is null ? null : JsonDocument.Parse(x.Ev).RootElement, scope, x.First);
            }).ToList();
    }
}
