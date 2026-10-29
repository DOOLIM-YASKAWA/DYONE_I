using System.Security.Claims;
using Npgsql;

namespace DyOne.Api.Modules.Demo;

/// <summary>
/// [임시 · 데모 기간] 화면 메모(포스트잇) — 화면마다 궁금한 점을 붙여 두고 나중에 팀원에게 묻기 위한 메모(demo.memo, server/db/demo_memo.sql).
/// 정식 기능이 아니다. 데모가 끝나면 이 파일·스키마·화면(web/src/components/DemoMemos.tsx)을 함께 지운다.
/// 켜기: DemoMemo:Enabled=true(개발 설정에서 켬). 쓰기는 ConnectionStrings:DyOneWrite. 로그인한 사람은 모두 보고 쓸 수 있다.
/// </summary>
public static class DemoMemoModule
{
    public record MemoDto(long Id, string Path, string? Screen, double X, int Y, string Body, bool Resolved, string? Answer,
        string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public record MemoCreate(string Path, string? Screen, double X, int Y, string Body);
    public record MemoUpdate(string? Body = null, bool? Resolved = null, string? Answer = null, double? X = null, int? Y = null);

    public static void MapDemoMemos(this IEndpointRouteBuilder app, IConfiguration config)
    {
        if (!config.GetValue<bool>("DemoMemo:Enabled")) return;
        var g = app.MapGroup("/api/demo-memos").WithTags("DemoMemo").RequireAuthorization();

        static string Read(IConfiguration c) => c.GetConnectionString("DyOne") ?? "";
        static string? Write(IConfiguration c) => c.GetConnectionString("DyOneWrite") is { Length: > 0 } s ? s : null;

        // 목록: path를 주면 그 화면 것만, 없으면 전체(모든 화면 메모 모아 보기)
        g.MapGet("", async (string? path, IConfiguration c, CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(Read(c));
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                select m.id, m.path, m.screen, m.x_pct, m.y_px, m.body, m.resolved, m.answer, e.name, m.created_at, m.updated_at
                from demo.memo m left join org.employee e on e.id = m.created_by
                where @path::text is null or m.path = @path
                order by m.resolved, m.path, m.created_at", conn);
            cmd.Parameters.AddWithValue("path", (object?)path ?? DBNull.Value);
            var list = new List<MemoDto>();
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add(new MemoDto(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), (double)r.GetDecimal(3), r.GetInt32(4),
                        r.GetString(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                        r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10)));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // demo_memo.sql 적용 전: 빈 목록
            }
            return Results.Ok(list);
        });

        g.MapPost("", async (MemoCreate body, ClaimsPrincipal p, IConfiguration c, CancellationToken ct) =>
        {
            if (Write(c) is not { } cs) return Results.Problem("메모를 저장할 쓰기 연결(ConnectionStrings:DyOneWrite)이 없습니다.", statusCode: 503);
            var text = body.Body?.Trim() ?? "";
            if (text.Length == 0 || text.Length > 2000 || string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { message = "메모 내용을 확인해 주세요(2000자까지)." });
            long? me = long.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var e) ? e : null;
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"insert into demo.memo (path, screen, x_pct, y_px, body, created_by)
                values (@path, @screen, @x, @y, @body, @me) returning id", conn);
            cmd.Parameters.AddWithValue("path", body.Path.Trim());
            cmd.Parameters.AddWithValue("screen", (object?)body.Screen ?? DBNull.Value);
            cmd.Parameters.AddWithValue("x", (decimal)Math.Clamp(body.X, 0, 100));
            cmd.Parameters.AddWithValue("y", Math.Max(0, body.Y));
            cmd.Parameters.AddWithValue("body", text);
            cmd.Parameters.AddWithValue("me", (object?)me ?? DBNull.Value);
            return Results.Ok(new { id = (long)(await cmd.ExecuteScalarAsync(ct))! });
        });

        // 고치기: 내용 · 해결 여부 · 들은 답 · 위치(끌어 옮기기) 중 보낸 것만
        g.MapPut("/{id:long}", async (long id, MemoUpdate body, IConfiguration c, CancellationToken ct) =>
        {
            if (Write(c) is not { } cs) return Results.Problem("쓰기 연결(ConnectionStrings:DyOneWrite)이 없습니다.", statusCode: 503);
            if (body.Body is { } b && (b.Trim().Length == 0 || b.Length > 2000)) return Results.BadRequest(new { message = "메모 내용을 확인해 주세요(2000자까지)." });
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"update demo.memo set
                    body = coalesce(@body, body), resolved = coalesce(@resolved, resolved),
                    answer = case when @answerSet then @answer else answer end,
                    x_pct = coalesce(@x, x_pct), y_px = coalesce(@y, y_px), updated_at = now()
                where id = @id", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("body", (object?)body.Body?.Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("resolved", (object?)body.Resolved ?? DBNull.Value);
            cmd.Parameters.AddWithValue("answerSet", body.Answer is not null);
            cmd.Parameters.AddWithValue("answer", string.IsNullOrWhiteSpace(body.Answer) ? DBNull.Value : body.Answer.Trim());
            cmd.Parameters.AddWithValue("x", body.X is { } x ? (decimal)Math.Clamp(x, 0, 100) : DBNull.Value);
            cmd.Parameters.AddWithValue("y", body.Y is { } y ? Math.Max(0, y) : DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        g.MapDelete("/{id:long}", async (long id, IConfiguration c, CancellationToken ct) =>
        {
            if (Write(c) is not { } cs) return Results.Problem("쓰기 연결(ConnectionStrings:DyOneWrite)이 없습니다.", statusCode: 503);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("delete from demo.memo where id = @id", conn);
            cmd.Parameters.AddWithValue("id", id);
            return await cmd.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}
