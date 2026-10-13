using Npgsql;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 그룹웨어 변경 알림을 sync.change_notice에 남긴다(같은 트랜잭션 → 반영된 변경만 알린다).
/// API(GwChangeNoticeRelay)가 읽어 그 앱을 쓰는 부서·사람에게 실시간 알림(화면 토스트 · 알림함)으로 보내고, 30일 동안 알림함에 남는다.
/// 한 번에 알림이 너무 많으면(예: Worker를 오래 꺼 두었다 켠 경우) 앞의 MaxPerRun건만 따로 보내고 나머지는 한 줄로 묶는다.
/// </summary>
public static class GwNotices
{
    public const int MaxPerRun = 20;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30); // 알림함(놓친 알림 보기)에 30일 보여 준다

    /// <param name="Kind">changed · created · deleted</param>
    public sealed record Notice(long DocId, string Kind, string Title, string Body, string Link);

    /// <summary>
    /// 문서 하나의 바뀐 칸을 알림 문장으로. 상태 변경을 앞에 두고 나머지 칸은 세 개까지 이름을 적는다.
    /// </summary>
    public static string Summary(string? statusFrom, string? statusTo, IReadOnlyList<string> fieldLabels, int overwritten)
    {
        var parts = new List<string>();
        if (statusTo is not null) parts.Add($"상태 {statusFrom ?? "없음"} → {statusTo}");
        if (fieldLabels.Count > 0)
            parts.Add(string.Join(", ", fieldLabels.Take(3)) + (fieldLabels.Count > 3 ? $" 외 {fieldLabels.Count - 3}칸" : "") + " 변경");
        if (overwritten > 0) parts.Add($"DY ONE에서 고친 {overwritten}칸은 그룹웨어 값으로 덮음");
        return string.Join(" · ", parts);
    }

    public static async Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string entity, string appName, string listLink,
        IReadOnlyList<Notice> notices, CancellationToken ct)
    {
        if (notices.Count == 0) return;
        var rows = notices.Take(MaxPerRun).ToList();
        if (notices.Count > MaxPerRun)
            rows.Add(new Notice(0, "changed", $"그룹웨어 변경 · {appName}", $"그 밖에 {notices.Count - MaxPerRun}건이 그룹웨어에서 바뀌어 DY ONE에 반영했습니다.", listLink));
        foreach (var n in rows)
        {
            await using var cmd = new NpgsqlCommand(@"insert into sync.change_notice (source, entity, doc_id, kind, title, body, link)
                values ('groupware', @e, @d, @k, @t, @b, @l)", conn, tx);
            cmd.Parameters.AddWithValue("e", entity);
            cmd.Parameters.AddWithValue("d", n.DocId == 0 ? DBNull.Value : n.DocId);
            cmd.Parameters.AddWithValue("k", n.Kind);
            cmd.Parameters.AddWithValue("t", n.Title);
            cmd.Parameters.AddWithValue("b", n.Body.Length > 500 ? n.Body[..500] : n.Body);
            cmd.Parameters.AddWithValue("l", n.Link);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var clean = new NpgsqlCommand("delete from sync.change_notice where created_at < now() - @keep", conn, tx))
        {
            clean.Parameters.AddWithValue("keep", Keep);
            await clean.ExecuteNonQueryAsync(ct);
        }
    }
}
