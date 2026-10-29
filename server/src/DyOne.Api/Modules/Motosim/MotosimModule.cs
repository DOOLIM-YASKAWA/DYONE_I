using System.Security.Claims;
using Npgsql;

namespace DyOne.Api.Modules.Motosim;

public record MotosimNodeDto(long Id, long? ParentId, string Name);
/// <param name="FileCount">삭제되지 않은 Cell 파일 수</param>
public record MotosimZoneDto(long Id, long BoothId, string Name, string? ZoneType, int FileCount, MotosimLatestDto? Latest);
public record MotosimLatestDto(string FileName, DateTimeOffset UploadedAt, string? UploadedByName, long? FileSize, string? MotosimVersion);
public record MotosimTreeDto(List<MotosimNodeDto> Customers, List<MotosimNodeDto> Factories, List<MotosimNodeDto> Booths, List<MotosimZoneDto> Zones);
/// <param name="Source">nas = DY ONE NAS에 있음(내려받기 가능) · drive = 원본 런처 Google Drive에만 있음</param>
public record MotosimCellFileDto(long Id, string FileName, string? FileUrl, long? FileSize, string? MotosimVersion, string? CommitMessage,
    string? UploadedByName, string? UploadedByDepartment, DateTimeOffset UploadedAt, bool IsLatest,
    long? ProjectId, string? ProjectCode, string? ProjectName, string Source);

/// <summary>
/// MotoSim (MotoSim Launcher 이관) — 조회. 쓰기(올리기·삭제 등)는 MotosimFiles.cs.
/// 공정 트리는 plant.*(사용 중인 공정만, 상위까지 사용 중), Cell 파일 이력·즐겨찾기는 motosim.*(server/db/motosim_schema.sql).
/// </summary>
public static class MotosimModule
{
    public static void MapMotosim(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/motosim").WithTags("MotoSim").RequireAuthorization();

        // 공정 트리(고객사 → 공장 → 부스 → Zone)와 Zone별 최신 Cell 파일. 원본 런처처럼 정렬은 순서(sort_order) → id.
        g.MapGet("/tree", async (IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var customers = await NodesAsync(conn, "select c.id, null::bigint, c.name from plant.customer c where c.is_active order by c.sort_order, c.id", ct);
            var factories = await NodesAsync(conn, @"
                select f.id, f.customer_id, f.name from plant.factory f join plant.customer c on c.id = f.customer_id
                where f.is_active and c.is_active order by f.sort_order, f.id", ct);
            var booths = await NodesAsync(conn, @"
                select b.id, b.factory_id, b.name from plant.booth b join plant.factory f on f.id = b.factory_id join plant.customer c on c.id = f.customer_id
                where b.is_active and f.is_active and c.is_active order by b.sort_order, b.id", ct);
            var zones = new List<MotosimZoneDto>();
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    select z.id, z.booth_id, z.name, t.name,
                           (select count(*)::int from motosim.cell_file x where x.zone_id = z.id and not x.is_deleted),
                           l.file_name, l.uploaded_at, l.uploaded_by_name, l.file_size, l.motosim_version
                    from plant.zone z
                    join plant.booth b on b.id = z.booth_id join plant.factory f on f.id = b.factory_id join plant.customer c on c.id = f.customer_id
                    left join code.code t on t.id = z.zone_type_id
                    -- 프로젝트마다 최신이 하나씩 있으므로 그중 가장 최근 것
                    left join lateral (
                        select coalesce(x.file_name, x.drive_file_name) file_name, x.uploaded_at, x.uploaded_by_name, x.file_size, x.motosim_version
                        from motosim.cell_file x where x.zone_id = z.id and x.is_latest and not x.is_deleted
                        order by x.uploaded_at desc limit 1) l on true
                    where z.is_active and b.is_active and f.is_active and c.is_active
                    order by z.sort_order, z.id", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    zones.Add(new MotosimZoneDto(r.GetInt64(0), r.GetInt64(1), r.GetString(2), S(r, 3), r.GetInt32(4),
                        r.IsDBNull(5) ? null : new MotosimLatestDto(r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), S(r, 7),
                            r.IsDBNull(8) ? null : r.GetInt64(8), S(r, 9))));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return MissingSchema();
            }
            return Results.Ok(new MotosimTreeDto(customers, factories, booths, zones));
        });

        // 한 Zone의 Cell 파일 이력(최신 먼저, 그다음 올린 순서 역순). 삭제한 파일은 빼고 준다.
        g.MapGet("/zones/{id:long}/files", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var list = new List<MotosimCellFileDto>();
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    select c.cell_file_id, coalesce(c.file_name, c.drive_file_name), c.drive_file_url, c.file_size, c.motosim_version, c.commit_message,
                           c.uploaded_by_name, c.uploaded_by_department, c.uploaded_at, c.is_latest,
                           c.project_id, p.code, p.name, case when c.file_ref_id is null then 'drive' else 'nas' end
                    from motosim.cell_file c left join project.project p on p.id = c.project_id
                    where c.zone_id = @zid and not c.is_deleted
                    order by c.is_latest desc, c.uploaded_at desc, c.cell_file_id desc", conn);
                cmd.Parameters.AddWithValue("zid", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new MotosimCellFileDto(r.GetInt64(0), r.GetString(1), S(r, 2), r.IsDBNull(3) ? null : r.GetInt64(3), S(r, 4), S(r, 5),
                        S(r, 6), S(r, 7), r.GetFieldValue<DateTimeOffset>(8), r.GetBoolean(9),
                        r.IsDBNull(10) ? null : r.GetInt64(10), S(r, 11), S(r, 12), r.GetString(13)));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return MissingSchema();
            }
            return Results.Ok(list);
        });

        // 내 즐겨찾기 Zone. 원본 런처는 즐겨찾기를 사번(org.employee.emp_no)으로 저장했다(권한 표는 로그인 아이디).
        g.MapGet("/favorites", async (ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            if (!long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var me)) return Results.Unauthorized();
            await using var conn = new NpgsqlConnection(config.GetConnectionString("DyOne"));
            await conn.OpenAsync(ct);
            var ids = new List<long>();
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    select f.zone_id from motosim.favorite_zone f
                    join org.employee e on f.user_id = e.emp_no or lower(f.user_id) = lower(e.login_id)
                    where e.id = @me order by f.created_at", conn);
                cmd.Parameters.AddWithValue("me", me);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return MissingSchema();
            }
            return Results.Ok(ids);
        });

        g.MapMotosimFiles(); // 2단계: 올리기·내려받기·삭제·즐겨찾기 바꾸기 (MotosimFiles.cs)
        g.MapMotosimHub();   // 런처 허브·관리자 센터: 공지·최근 활동·사용 통계 (MotosimHub.cs)
        g.MapMotosimPlugins(); // Smart Cell Plugin 배포·MotoSim 설치 파일 (MotosimPlugins.cs)
    }

    private static async Task<List<MotosimNodeDto>> NodesAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        var list = new List<MotosimNodeDto>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new MotosimNodeDto(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2)));
        return list;
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static IResult MissingSchema() => Results.Problem("MotoSim 표가 없습니다(server/db/motosim_schema.sql 적용 필요).");
}
