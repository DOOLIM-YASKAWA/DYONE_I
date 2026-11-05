using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;

namespace DyOne.Worker.Jobs.ProjectMain;

public sealed record ErpProjectRow(
    string Code, string? StatusCode, string? Name, string? PartnerCode, string? PartnerName, string? Pm,
    DateOnly? From, DateOnly? To, decimal? Amount, DateOnly? End, DateOnly? Close, string Updated);

/// <param name="Po31_60">31~60일 전 발주 수</param><param name="Po61_90">61~90일 전 발주 수</param> — 발주 연속성(최근 3개월 중 발주가 있던 달 수) 판정용
public sealed record ErpPoRow(string Code, int Po30, int Po90, int PoTotal, DateOnly? LastPo, int Po31_60 = 0, int Po61_90 = 0, string? RecentJson = null);

/// <summary>
/// ERP(NEOE, MSSQL) 프로젝트 읽기 — <b>읽기 전용</b>.
/// 계정 dylab(조회 전용), 연결 자체를 ApplicationIntent=ReadOnly로 열고 READ UNCOMMITTED + WITH(NOLOCK) SELECT만 보낸다.
/// 연결 문자열 ConnectionStrings:Erp(비밀번호 없이) + 비밀번호 Erp:Password(user-secrets/환경변수).
/// </summary>
public static class ErpMirror
{
    public static readonly IReadOnlyDictionary<string, string> StatusNames = new Dictionary<string, string>
    {
        ["001"] = "영업중", ["002"] = "수주", ["003"] = "마감", ["004"] = "중단",
    };

    public static string? Conn(IConfiguration config)
    {
        var baseConn = config.GetConnectionString("Erp");
        if (string.IsNullOrWhiteSpace(baseConn)) return null;
        var b = new SqlConnectionStringBuilder(baseConn) { ApplicationIntent = ApplicationIntent.ReadOnly };
        if (config["Erp:Password"] is { Length: > 0 } pw) b.Password = pw;
        return b.ConnectionString;
    }

    /// <summary>바뀐 프로젝트만(since = DTS_UPDATE·DTS_INSERT 기준, null이면 전체). 영업 관리 최신 1줄 + 프로젝트 헤더 + 거래처 이름.</summary>
    public static async Task<List<ErpProjectRow>> ReadProjectsAsync(string conn, string? since, CancellationToken ct)
    {
        const string sql = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            WITH CH AS (
              SELECT CD_PRJT FROM neoe.WS_B_BSNSS_MAIN WITH(NOLOCK)
               WHERE CD_PRJT LIKE 'DLP-%' AND (@since IS NULL OR DTS_UPDATE > @since OR DTS_INSERT > @since)
              UNION
              SELECT NO_PROJECT FROM neoe.SA_PROJECTH WITH(NOLOCK)
               WHERE NO_PROJECT LIKE 'DLP-%' AND (@since IS NULL OR DTS_UPDATE > @since OR DTS_INSERT > @since)),
            L AS (
              SELECT B.CD_COMPANY, B.CD_PRJT, B.WS_CM00008, B.NM_EMP_PM, B.DT_FM_CNST, B.DT_TO_CNST, B.DTS_UPDATE,
                     ROW_NUMBER() OVER (PARTITION BY B.CD_PRJT ORDER BY B.DTS_UPDATE DESC) rn
                FROM neoe.WS_B_BSNSS_MAIN B WITH(NOLOCK) JOIN CH ON CH.CD_PRJT = B.CD_PRJT),
            H AS (
              SELECT H.CD_COMPANY, H.NO_PROJECT, MAX(H.NM_PROJECT) nm, MAX(H.CD_PARTNER) partner, MAX(H.AM_WONAMT) amt,
                     MAX(H.ED_PROJECT) ed, MAX(H.DT_CLOSE) dtc, MAX(H.DTS_UPDATE) hupd
                FROM neoe.SA_PROJECTH H WITH(NOLOCK) JOIN CH ON CH.CD_PRJT = H.NO_PROJECT
               GROUP BY H.CD_COMPANY, H.NO_PROJECT)
            SELECT L.CD_PRJT, L.WS_CM00008, H.nm, H.partner, P.LN_PARTNER, L.NM_EMP_PM, L.DT_FM_CNST, L.DT_TO_CNST,
                   H.amt, H.ed, H.dtc, L.DTS_UPDATE, H.hupd
              FROM L LEFT JOIN H ON H.CD_COMPANY = L.CD_COMPANY AND H.NO_PROJECT = L.CD_PRJT
              LEFT JOIN neoe.MA_PARTNER P WITH(NOLOCK) ON P.CD_COMPANY = L.CD_COMPANY AND P.CD_PARTNER = H.partner
             WHERE L.rn = 1
            """;
        var list = new List<ErpProjectRow>();
        await using var c = new SqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        cmd.Parameters.Add(new SqlParameter("@since", System.Data.SqlDbType.NVarChar, 20) { Value = (object?)since ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } s ? s : null;
            var u1 = S(11) ?? "";
            var u2 = S(12) ?? "";
            list.Add(new ErpProjectRow(
                S(0)!, S(1), S(2), S(3), S(4), S(5), D8(S(6)), D8(S(7)),
                r.IsDBNull(8) ? null : Convert.ToDecimal(r.GetValue(8), CultureInfo.InvariantCulture),
                D8(S(9)), D8(S(10)), string.CompareOrdinal(u1, u2) >= 0 ? u1 : u2));
        }
        return list;
    }

    /// <summary>프로젝트별 발주 집계(PU_POH 번호 기준). 오늘 이후 발주일(입력 오류, 예: 2123-11-21)은 '마지막 발주'에서 뺀다.</summary>
    public static async Task<List<ErpPoRow>> ReadPoAsync(string conn, DateOnly today, CancellationToken ct)
    {
        const string sql = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT L.CD_PJT,
                   COUNT(DISTINCT CASE WHEN H.DT_PO >= @d30 AND H.DT_PO <= @today THEN H.NO_PO END),
                   COUNT(DISTINCT CASE WHEN H.DT_PO >= @d90 AND H.DT_PO <= @today THEN H.NO_PO END),
                   COUNT(DISTINCT H.NO_PO),
                   MAX(CASE WHEN H.DT_PO <= @today THEN H.DT_PO END),
                   COUNT(DISTINCT CASE WHEN H.DT_PO >= @d60 AND H.DT_PO < @d30 THEN H.NO_PO END),
                   COUNT(DISTINCT CASE WHEN H.DT_PO >= @d90 AND H.DT_PO < @d60 THEN H.NO_PO END)
              FROM neoe.PU_POL L WITH(NOLOCK)
              JOIN neoe.PU_POH H WITH(NOLOCK) ON H.CD_COMPANY = L.CD_COMPANY AND H.NO_PO = L.NO_PO
             WHERE L.CD_PJT LIKE 'DLP-%'
             GROUP BY L.CD_PJT
            """;
        var list = new List<ErpPoRow>();
        await using var c = new SqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@today", today.ToString("yyyyMMdd"));
        cmd.Parameters.AddWithValue("@d30", today.AddDays(-30).ToString("yyyyMMdd"));
        cmd.Parameters.AddWithValue("@d60", today.AddDays(-60).ToString("yyyyMMdd"));
        cmd.Parameters.AddWithValue("@d90", today.AddDays(-90).ToString("yyyyMMdd"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var code = Convert.ToString(r.GetValue(0))?.Trim();
            if (string.IsNullOrEmpty(code)) continue;
            list.Add(new ErpPoRow(code, Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(2)), Convert.ToInt32(r.GetValue(3)),
                r.IsDBNull(4) ? null : D8(Convert.ToString(r.GetValue(4))), Convert.ToInt32(r.GetValue(5)), Convert.ToInt32(r.GetValue(6))));
        }
        return list;
    }

    /// <summary>
    /// 프로젝트별 최근 90일 발주 내용(po_recent): 발주서마다 날짜·거래처·품목 이름(최대 5)·품목 줄 수. 금액은 읽지 않는다.
    /// 새 발주서부터 프로젝트당 최대 40건. 오늘 이후 발주일(입력 오류)은 뺀다. 근거 화면의 '발주 흐름' 글 재료.
    /// </summary>
    public static async Task<Dictionary<string, string>> ReadPoRecentAsync(string conn, DateOnly today, CancellationToken ct)
    {
        const string sql = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT L.CD_PJT, H.DT_PO, H.NO_PO, P.LN_PARTNER, I.NM_ITEM
              FROM neoe.PU_POL L WITH(NOLOCK)
              JOIN neoe.PU_POH H WITH(NOLOCK) ON H.CD_COMPANY = L.CD_COMPANY AND H.NO_PO = L.NO_PO
              LEFT JOIN neoe.MA_PITEM I WITH(NOLOCK) ON I.CD_COMPANY = L.CD_COMPANY AND I.CD_PLANT = L.CD_PLANT AND I.CD_ITEM = L.CD_ITEM
              LEFT JOIN neoe.MA_PARTNER P WITH(NOLOCK) ON P.CD_COMPANY = H.CD_COMPANY AND P.CD_PARTNER = H.CD_PARTNER
             WHERE L.CD_PJT LIKE 'DLP-%' AND H.DT_PO >= @d90 AND H.DT_PO <= @today
            """;
        var rows = new List<(string Code, string Day, string No, string Partner, string Item)>();
        await using (var c = new SqlConnection(conn))
        {
            await c.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@today", today.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@d90", today.AddDays(-90).ToString("yyyyMMdd"));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string T(int i) => r.IsDBNull(i) ? "" : (Convert.ToString(r.GetValue(i)) ?? "").Trim();
            while (await r.ReadAsync(ct))
                if (T(0) is { Length: > 0 } code) rows.Add((code, T(1), T(2), T(3), T(4)));
        }
        return rows.GroupBy(x => x.Code).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g
            .GroupBy(x => (x.Day, x.No))
            .OrderByDescending(po => po.Key.Day).ThenByDescending(po => po.Key.No).Take(40)
            .Select(po => new
            {
                d = D8(po.Key.Day)?.ToString("yyyy-MM-dd"),
                partner = po.First().Partner,
                items = po.Select(x => x.Item).Where(x => x.Length > 0).Distinct().Take(5).ToArray(),
                lines = po.Count(),
            })));
    }

    /// <summary>dyone sync.erp_project에 code 기준 upsert. 마지막 증분 기준(watermark)은 sync.state(erp/project).</summary>
    public static async Task<int> UpsertAsync(NpgsqlConnection db, List<ErpProjectRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var t = new NpgsqlCommand("create temp table _erp (like sync.erp_project including defaults) on commit drop", db, tx))
            await t.ExecuteNonQueryAsync(ct);
        await using (var w = await db.BeginBinaryImportAsync(
            "copy _erp (cd_prjt, status_code, status_name, name, partner_code, partner_name, pm_name, cnst_from, cnst_to, contract_amount, project_end, dt_close, erp_updated_at) from stdin (format binary)", ct))
        {
            foreach (var x in rows.GroupBy(r => r.Code).Select(g => g.First()))
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(x.Code, NpgsqlDbType.Text, ct);
                await W(w, x.StatusCode, ct);
                await W(w, x.StatusCode is { } s && StatusNames.TryGetValue(s, out var n) ? n : x.StatusCode, ct);
                await W(w, x.Name, ct);
                await W(w, x.PartnerCode, ct);
                await W(w, x.PartnerName, ct);
                await W(w, x.Pm, ct);
                await WD(w, x.From, ct);
                await WD(w, x.To, ct);
                if (x.Amount is { } a) await w.WriteAsync(a, NpgsqlDbType.Numeric, ct); else await w.WriteNullAsync(ct);
                await WD(w, x.End, ct);
                await WD(w, x.Close, ct);
                await W(w, x.Updated, ct);
            }
            await w.CompleteAsync(ct);
        }
        int n2;
        await using (var up = new NpgsqlCommand("""
            insert into sync.erp_project (cd_prjt, status_code, status_name, name, partner_code, partner_name, pm_name, cnst_from, cnst_to, contract_amount, project_end, dt_close, erp_updated_at, synced_at)
            select cd_prjt, status_code, status_name, name, partner_code, partner_name, pm_name, cnst_from, cnst_to, contract_amount, project_end, dt_close, erp_updated_at, now() from _erp
            on conflict (cd_prjt) do update set status_code = excluded.status_code, status_name = excluded.status_name, name = excluded.name,
              partner_code = excluded.partner_code, partner_name = excluded.partner_name, pm_name = excluded.pm_name, cnst_from = excluded.cnst_from,
              cnst_to = excluded.cnst_to, contract_amount = excluded.contract_amount, project_end = excluded.project_end, dt_close = excluded.dt_close,
              erp_updated_at = excluded.erp_updated_at, synced_at = now()
            """, db, tx))
            n2 = await up.ExecuteNonQueryAsync(ct);
        await using (var st = new NpgsqlCommand("""
            insert into sync.state (source, entity, watermark, updated_at) select 'erp', 'project', max(erp_updated_at), now() from sync.erp_project
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()
            """, db, tx))
            await st.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n2;
    }

    public static async Task<string?> WatermarkAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select watermark from sync.state where source = 'erp' and entity = 'project'", db);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static Task W(NpgsqlBinaryImporter w, string? v, CancellationToken ct) => v is null ? w.WriteNullAsync(ct) : w.WriteAsync(v, NpgsqlDbType.Text, ct);
    private static Task WD(NpgsqlBinaryImporter w, DateOnly? v, CancellationToken ct) => v is { } d ? w.WriteAsync(d, NpgsqlDbType.Date, ct) : w.WriteNullAsync(ct);

    public static DateOnly? D8(string? s) =>
        s is { Length: >= 8 } && DateOnly.TryParseExact(s[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
