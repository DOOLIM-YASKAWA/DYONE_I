using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어(tims) WORKS 변경 감지 — 모든 앱(applet)이 함께 쓰는 '변경 이력' 피드.
///
/// 그룹웨어는 변경을 외부로 알려 주지 않으므로 DY ONE이 주기적으로 확인한다.
/// 문서의 필드 수정·상태 변경·등록·활동 글·댓글은 모두 go_applet_doc_logs에 log_id(증가하는 번호)로 남는다.
/// 마지막으로 본 log_id 뒤의 행만 log_id 색인(uk1_go_applet_doc_logs)으로 읽으므로, 감시하는 앱 수와 상관없이 가볍다.
/// (2026-10-13 실측: 1분치 0.6ms, 하루치 약 1만 건도 약 27ms. 문서 표 전체를 훑지 않는다.)
/// 문서 삭제는 이력에 남지 않으므로 각 동기화 작업의 하루 한 번 전체 비교에서 찾는다.
/// </summary>
public static class GwChangeFeed
{
    /// <summary>
    /// 늦게 커밋된 트랜잭션이 이미 지나간 번호를 쓸 수 있어, 확인할 때마다 이만큼 앞에서부터 다시 본다.
    /// (하루 약 1만 건 기준 200건 ≈ 30분. 같은 문서를 다시 읽어도 칸별 비교라 결과는 같다.)
    /// </summary>
    public const long Overlap = 200;

    /// <summary>그룹웨어 전체의 마지막 변경 이력 번호</summary>
    public static async Task<long> MaxLogAsync(string gwConn, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select coalesce(max(log_id), 0) from go_applet_doc_logs", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>afterLog 뒤에 이력이 생긴 문서 id를 앱별로 (appletIds에 있는 앱만)</summary>
    public static async Task<Dictionary<long, long[]>> ChangedDocsAsync(string gwConn, long afterLog, long[] appletIds, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(gwConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            @"select d.applet_id, array_agg(distinct dl.applet_doc_id)
              from go_applet_doc_logs dl join go_applet_docs d on d.id = dl.applet_doc_id
              where dl.log_id > @after and d.applet_id = any(@applets)
              group by d.applet_id", conn);
        cmd.Parameters.AddWithValue("after", Math.Max(0, afterLog - Overlap));
        cmd.Parameters.AddWithValue("applets", appletIds);
        var result = appletIds.ToDictionary(a => a, _ => Array.Empty<long>());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetInt64(0)] = r.GetFieldValue<long[]>(1);
        return result;
    }
}
