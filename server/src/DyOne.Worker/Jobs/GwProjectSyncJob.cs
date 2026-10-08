using Microsoft.Extensions.Options;
using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 매일 지정 시각(기본 08:00)에 그룹웨어(tims) WORKS applet 285 "기초정보-프로젝트코드 관리"를 조회해
/// dyone DB project.project 미러를 갱신한다.
///
/// 원천(GW): 조회 전용. tims EAV(go_applet_docs/doc_values/vals)를 values_key로 피벗.
/// 대상(dyone): project.project 를 code 기준 upsert. 상태(0~3)는 project_status 코드의 sort_order로 매핑.
///             소스에서 사라진 DLP 코드는 soft-delete(is_active=false). DLP 접두어만 취급(DL/DLC 제외).
/// 결과: sync.state(groupware/project) 워터마크 기록, 실패 시 sync.error 기록.
///
/// 연결 문자열: ConnectionStrings:Gw (tims, 읽기), ConnectionStrings:DyOneWrite (dyone, 쓰기).
/// </summary>
public class GwProjectSyncJob(IOptions<GwProjectSyncOptions> options, IConfiguration config, ILogger<GwProjectSyncJob> logger)
    : DailyJob(TimeOnly.Parse(options.Value.DailyAt), options.Value.TimeZone)
{
    private readonly GwProjectSyncOptions _opt = options.Value;

    public override string Name => "gw-project-sync";
    public override bool RunOnStartup => _opt.RunOnStartup;

    private sealed record GwProject(string Code, string Name, string? Partner, int? StatusCode, string? ErpDate);

    // applet 285 필드키 (go_applet_field 정의 기준)
    private const string KeyCode = "_x3nxrb40v";
    private const string KeyName = "_17sdi9k41";
    private const string KeyPartner = "_yhrcz1wkz";
    private const string KeyErpDate = "_0e7fpe1fv";
    private const string KeyStatus = "_m9yeyxbmu";

    public override async Task RunAsync(CancellationToken ct)
    {
        var gwConn = ConnBuilder.Build(config, "Gw", "Gw:Password") ?? throw new InvalidOperationException("ConnectionStrings:Gw 없음(그룹웨어 tims 읽기 연결)");

        var rows = await ReadGroupwareAsync(gwConn, ct);

        // applet285에는 같은 프로젝트 코드가 중복 입력된 문서가 있을 수 있다.
        // 임시테이블 PK(code)와 MERGE(코드 기준)는 코드당 1행만 허용하므로 코드별로 1건만 남긴다(마지막 문서 우선).
        var distinct = rows.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        if (distinct.Count != rows.Count)
            logger.LogWarning("GW 중복 프로젝트 코드 {Dup}건 → 코드별 1건으로 정리", rows.Count - distinct.Count);
        rows = distinct;

        logger.LogInformation("GW applet285 조회: DLP 프로젝트 {Count}건", rows.Count);
        if (rows.Count == 0)
        {
            logger.LogWarning("GW 조회 결과 0건 → 안전을 위해 적재를 건너뛴다(soft-delete 방지).");
            return;
        }

        if (_opt.DryRun)
        {
            logger.LogInformation("DryRun: 적재 생략. 샘플 {Sample}", string.Join(" / ", rows.Take(3).Select(r => $"{r.Code} {r.Name}")));
            return;
        }

        // 쓰기 대상(로컬 dev + 운영 등) 전체에 동일 데이터를 적재한다. 대상 하나가 실패해도 나머지는 진행.
        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");
        Exception? firstError = null;
        foreach (var (name, conn) in targets)
        {
            try
            {
                var (inserted, updated, deactivated) = await UpsertAsync(conn, rows, ct);
                await RecordSyncStateAsync(conn, rows, ct);
                logger.LogInformation("[{Target}] GW 프로젝트 미러 갱신: 신규 {Ins} · 수정 {Upd} · 비활성 {Del}", name, inserted, updated, deactivated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
                logger.LogError(ex, "[{Target}] 적재 실패", name);
                try { await TryRecordErrorAsync(conn, ex.Message, ct); } catch { /* 오류 기록 실패는 무시 */ }
            }
        }
        if (firstError is not null) throw firstError;
    }

    private static async Task<List<GwProject>> ReadGroupwareAsync(string gwConn, CancellationToken ct)
    {
        const string sql = $@"
            select
              max(v.string_value) filter (where dv.values_key = '{KeyCode}')    as code,
              max(v.string_value) filter (where dv.values_key = '{KeyName}')    as name,
              max(v.string_value) filter (where dv.values_key = '{KeyPartner}') as partner,
              max(v.string_value) filter (where dv.values_key = '{KeyErpDate}') as erp_date,
              max(v.long_value)   filter (where dv.values_key = '{KeyStatus}')  as status_code
            from go_applet_docs d
            join go_applet_doc_values dv on dv.applet_doc_id = d.id
            join go_applet_vals v on v.id = dv.value_id
            where d.applet_id = 285
            group by d.id";

        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<GwProject>();
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(code) || !code.StartsWith("DLP", StringComparison.OrdinalIgnoreCase)) continue; // 진짜 ERP 프로젝트만
            list.Add(new GwProject(
                code.Trim(),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(4) ? null : (int)reader.GetInt64(4),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    private static async Task<(int inserted, int updated, int deactivated)> UpsertAsync(string writeConn, List<GwProject> rows, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var create = new NpgsqlCommand(
            "create temp table _gw_proj (code text primary key, name text, partner text, status_code int, erp_date text) on commit drop", conn, tx))
            await create.ExecuteNonQueryAsync(ct);

        await using (var import = conn.BeginBinaryImport("copy _gw_proj (code, name, partner, status_code, erp_date) from stdin (format binary)"))
        {
            foreach (var r in rows)
            {
                await import.StartRowAsync(ct);
                await import.WriteAsync(r.Code, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await import.WriteAsync(r.Name, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await WriteNullableAsync(import, r.Partner, ct);
                if (r.StatusCode is { } sc) await import.WriteAsync(sc, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                else await import.WriteNullAsync(ct);
                await WriteNullableAsync(import, r.ErpDate, ct);
            }
            await import.CompleteAsync(ct);
        }

        // 상태코드(0~3) → project_status 코드 id (sort_order로 매핑). ERP날짜 YYYYMMDD → date.
        const string merge = @"
            with src as (
              select g.code, g.name, g.partner,
                     (select c.id from code.code c where c.group_key = 'project_status' and c.sort_order = g.status_code) as status_id,
                     case when g.erp_date ~ '^\d{8}$' then to_date(g.erp_date, 'YYYYMMDD') end as erp_reg_date
              from _gw_proj g
            )
            merge into project.project p
            using src on p.code = src.code
            when matched then update set
              name = src.name, partner_name = src.partner, status_id = src.status_id,
              erp_reg_date = src.erp_reg_date, is_active = true, updated_at = now()
            when not matched then insert (code, name, partner_name, status_id, erp_reg_date, is_active, created_at, updated_at)
              values (src.code, src.name, src.partner, src.status_id, src.erp_reg_date, true, now(), now())";

        int affected;
        await using (var mergeCmd = new NpgsqlCommand(merge, conn, tx))
            affected = await mergeCmd.ExecuteNonQueryAsync(ct);

        // 소스에서 사라진 DLP 프로젝트는 soft-delete
        int deactivated;
        await using (var del = new NpgsqlCommand(
            "update project.project set is_active = false, updated_at = now() where code like 'DLP-%' and is_active = true and code not in (select code from _gw_proj)", conn, tx))
            deactivated = await del.ExecuteNonQueryAsync(ct);

        // MERGE는 전체 영향 행만 주므로 신규/수정 구분은 별도 집계
        int inserted;
        await using (var insCount = new NpgsqlCommand(
            "select count(*) from project.project p where p.code like 'DLP-%' and p.created_at >= now() - interval '1 minute'", conn, tx))
            inserted = Convert.ToInt32(await insCount.ExecuteScalarAsync(ct));

        await tx.CommitAsync(ct);
        return (inserted, Math.Max(0, affected - inserted), deactivated);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter import, string? value, CancellationToken ct)
    {
        if (value is null) await import.WriteNullAsync(ct);
        else await import.WriteAsync(value, NpgsqlTypes.NpgsqlDbType.Text, ct);
    }

    private static async Task RecordSyncStateAsync(string writeConn, List<GwProject> rows, CancellationToken ct)
    {
        var watermark = rows.Select(r => r.ErpDate).Where(d => !string.IsNullOrEmpty(d)).DefaultIfEmpty("").Max();
        await using var conn = new NpgsqlConnection(writeConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            @"insert into sync.state (source, entity, watermark, updated_at) values ('groupware', 'project', @w, now())
              on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()", conn);
        cmd.Parameters.AddWithValue("w", (object?)watermark ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TryRecordErrorAsync(string writeConn, string message, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(writeConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "insert into sync.error (source, entity, source_pk, message, created_at) values ('groupware', 'project', null, @m, now())", conn);
            cmd.Parameters.AddWithValue("m", message.Length > 2000 ? message[..2000] : message);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "sync.error 기록 실패");
        }
    }
}
