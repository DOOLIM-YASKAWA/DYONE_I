using System.Security.Claims;
using DyOne.Api.Modules.Projects;
using Npgsql;

namespace DyOne.Api.Modules.Motosim;

public record MotosimNoticeDto(long Id, string Title, string Body, string? AuthorName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record MotosimNoticeRequest(string Title, string? Body);
public record MotosimActivityDto(long Id, string Kind, string? Summary, string? ActorName, DateTimeOffset CreatedAt,
    long? ZoneId, string? ZonePath, long? ProjectId, string? ProjectCode, long? NoticeId);
public record MotosimStatDto(string Kind, int Today, int Week, int Month);
public record MotosimDailyDto(DateOnly Day, int Uploads, int Downloads);
public record MotosimHubDto(List<MotosimNoticeDto> Notices, List<MotosimActivityDto> Activities, List<MotosimStatDto> Stats,
    List<MotosimDailyDto> Daily, int CellCount, int ZoneCount, long StorageBytes, int DriveOnlyCount);
public record MotosimOpenUploadDto(Guid Id, string FileName, long TotalSize, long Received, string Status, string? CreatedByName,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ZonePath);
public record MotosimAdminDto(List<MotosimOpenUploadDto> Uploads, long TrashBytes, int TrashCount, long TempBytes);

/// <summary>
/// MotoSim 런처 허브(공지·최근 활동·사용 통계)와 관리자 센터(올리다 만 업로드 정리, 저장 용량).
/// 원본 런처는 이것들을 서버 JSON 파일에 두었고, 여기서는 motosim.notice · motosim.activity 표에 둔다.
/// 권한(시스템관리자만 공지 쓰기 등)은 아직 넣지 않았다 — 권한 단계에서 막는다.
/// </summary>
public static class MotosimHub
{
    private const string ZonePathSql =
        "(select f.name || ' / ' || b.name || ' / ' || z.name from plant.zone z join plant.booth b on b.id = z.booth_id join plant.factory f on f.id = b.factory_id where z.id = {0})";

    public static void MapMotosimHub(this RouteGroupBuilder g)
    {
        g.MapGet("/hub", async (IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: false, ct);
            var notices = await NoticesAsync(conn, 20, ct);

            var activities = new List<MotosimActivityDto>();
            await using (var q = new NpgsqlCommand($@"
                select a.id, a.kind, a.summary, a.actor_name, a.created_at, a.zone_id, {string.Format(ZonePathSql, "a.zone_id")}, a.project_id, p.code, a.notice_id
                from motosim.activity a left join project.project p on p.id = a.project_id
                order by a.created_at desc, a.id desc limit 30", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    activities.Add(new MotosimActivityDto(r.GetInt64(0), r.GetString(1), S(r, 2), S(r, 3), r.GetFieldValue<DateTimeOffset>(4),
                        N(r, 5), S(r, 6), N(r, 7), S(r, 8), N(r, 9)));

            // 오늘·7일·30일(한국 시간 기준 날짜)
            var stats = new List<MotosimStatDto>();
            await using (var q = new NpgsqlCommand(@"
                with d as (select (now() at time zone 'Asia/Seoul')::date today)
                select k.kind,
                       count(a.id) filter (where (a.created_at at time zone 'Asia/Seoul')::date = d.today)::int,
                       count(a.id) filter (where (a.created_at at time zone 'Asia/Seoul')::date > d.today - 7)::int,
                       count(a.id) filter (where (a.created_at at time zone 'Asia/Seoul')::date > d.today - 30)::int
                from (values ('cell-upload'), ('cell-download'), ('cell-delete')) k(kind) cross join d
                left join motosim.activity a on a.kind = k.kind and a.created_at > now() - interval '31 days'
                group by k.kind, d.today order by k.kind desc", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) stats.Add(new MotosimStatDto(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));

            var daily = new List<MotosimDailyDto>();
            await using (var q = new NpgsqlCommand(@"
                select g.day::date,
                       count(a.id) filter (where a.kind = 'cell-upload')::int,
                       count(a.id) filter (where a.kind = 'cell-download')::int
                from generate_series((now() at time zone 'Asia/Seoul')::date - 13, (now() at time zone 'Asia/Seoul')::date, interval '1 day') g(day)
                left join motosim.activity a on (a.created_at at time zone 'Asia/Seoul')::date = g.day::date
                group by g.day order by g.day", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) daily.Add(new MotosimDailyDto(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetInt32(1), r.GetInt32(2)));

            int cells, zones, driveOnly; long bytes;
            await using (var q = new NpgsqlCommand(@"
                select count(*)::int, count(distinct zone_id)::int, coalesce(sum(file_size) filter (where file_ref_id is not null), 0)::bigint,
                       count(*) filter (where file_ref_id is null)::int
                from motosim.cell_file where not is_deleted", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                (cells, zones, bytes, driveOnly) = (r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3));
            }
            return Results.Ok(new MotosimHubDto(notices, activities, stats, daily, cells, zones, bytes, driveOnly));
        });

        g.MapGet("/notices", async (IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: false, ct);
            return Results.Ok(await NoticesAsync(conn, 200, ct));
        });

        g.MapPost("/notices", async (MotosimNoticeRequest req, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            if (!MotosimFiles.TryMe(principal, out var me)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.Problem("제목을 입력해 주세요.", statusCode: 400);
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long id;
            await using (var c = new NpgsqlCommand(@"
                insert into motosim.notice (title, body, created_by, author_name)
                select @t, @b, e.id, e.name from org.employee e where e.id = @me returning id", conn, tx))
            {
                c.Parameters.AddWithValue("t", req.Title.Trim());
                c.Parameters.AddWithValue("b", req.Body?.Trim() ?? "");
                c.Parameters.AddWithValue("me", me);
                id = (long)(await c.ExecuteScalarAsync(ct))!;
            }
            await MotosimFiles.ActivityAsync(conn, tx, "notice", null, null, null, me, await NameAsync(conn, tx, me, ct), $"공지: {req.Title.Trim()}", ct, id);
            await tx.CommitAsync(ct);
            return Results.Ok(new { id });
        });

        g.MapPut("/notices/{id:long}", async (long id, MotosimNoticeRequest req, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.Problem("제목을 입력해 주세요.", statusCode: 400);
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var c = new NpgsqlCommand(
                "update motosim.notice set title = @t, body = @b, updated_at = now() where id = @id and is_active", conn);
            c.Parameters.AddWithValue("t", req.Title.Trim());
            c.Parameters.AddWithValue("b", req.Body?.Trim() ?? "");
            c.Parameters.AddWithValue("id", id);
            return await c.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        // 공지 내리기(지우지 않고 숨긴다)
        g.MapDelete("/notices/{id:long}", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var c = new NpgsqlCommand("update motosim.notice set is_active = false, updated_at = now() where id = @id", conn);
            c.Parameters.AddWithValue("id", id);
            return await c.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        // 관리자 센터: 올리다 만 업로드, 휴지통·임시 폴더 용량
        g.MapGet("/admin", async (IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: false, ct);
            var uploads = new List<MotosimOpenUploadDto>();
            await using (var q = new NpgsqlCommand($@"
                select u.id, u.file_name, u.total_size, u.received, u.status, e.name, u.created_at, u.updated_at, {string.Format(ZonePathSql, "u.zone_id")}
                from motosim.upload_session u left join org.employee e on e.id = u.created_by
                where u.status in ('open', 'failed') order by u.updated_at desc limit 100", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    uploads.Add(new MotosimOpenUploadDto(r.GetGuid(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), S(r, 5),
                        r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), S(r, 8)));
            var (trashBytes, trashCount) = DirSize(Path.Combine(storage.Root, "_trash", "motosim"));
            var (tempBytes, _) = DirSize(Path.Combine(storage.Root, "_tmp", "motosim-uploads"));
            return Results.Ok(new MotosimAdminDto(uploads, trashBytes, trashCount, tempBytes));
        });

        // 하루 넘게 멈춘 업로드를 취소하고 임시 조각을 지운다(NAS의 정식 Cell 파일은 건드리지 않는다)
        g.MapPost("/admin/cleanup-uploads", async (IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            var ids = new List<Guid>();
            await using (var c = new NpgsqlCommand(@"
                update motosim.upload_session set status = 'cancelled', error = '관리자 정리(24시간 넘게 멈춤)', updated_at = now()
                where status in ('open', 'failed') and updated_at < now() - interval '24 hours' returning id", conn))
            await using (var r = await c.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
            foreach (var id in ids)
                try { File.Delete(Path.Combine(storage.Root, "_tmp", "motosim-uploads", $"{id:N}.part")); } catch (IOException) { }
            return Results.Ok(new { cancelled = ids.Count });
        });
    }

    private static async Task<List<MotosimNoticeDto>> NoticesAsync(NpgsqlConnection conn, int limit, CancellationToken ct)
    {
        var list = new List<MotosimNoticeDto>();
        await using var q = new NpgsqlCommand(
            "select id, title, body, author_name, created_at, updated_at from motosim.notice where is_active order by created_at desc limit @n", conn);
        q.Parameters.AddWithValue("n", limit);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new MotosimNoticeDto(r.GetInt64(0), r.GetString(1), r.GetString(2), S(r, 3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5)));
        return list;
    }

    private static async Task<string> NameAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long me, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("select name from org.employee where id = @me", conn, tx);
        q.Parameters.AddWithValue("me", me);
        return (await q.ExecuteScalarAsync(ct)) as string ?? "";
    }

    private static (long Bytes, int Count) DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        long bytes = 0; var count = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) { bytes += new FileInfo(f).Length; count++; }
        return (bytes, count);
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? N(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
