using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DyOne.Api.Modules.Projects;
using Npgsql;

namespace DyOne.Api.Modules.Motosim;

public record MotosimProjectDto(long Id, string Code, string Name);
public record MotosimUploadStartRequest(long ZoneId, long ProjectId, string FileName, long TotalSize, string? CommitMessage, string? MotosimVersion, string? LocalPath);
public record MotosimUploadDto(Guid Id, long ZoneId, long ProjectId, string FileName, long TotalSize, long Received, string Status, int ChunkSize, long? CellFileId, string? Error);

/// <summary>
/// MotoSim 2단계 — Cell 파일 실물(NAS) 올리기·내려받기·삭제, 즐겨찾기 바꾸기, Zone에 연결된 프로젝트.
/// 저장 위치(2026-10-23 결정, NAS 폴더 규칙):
///   &lt;FileStorage:Root&gt;/프로젝트/&lt;코드 연도&gt;/&lt;코드&gt;_&lt;이름&gt;/MotoSim Cell/&lt;공장&gt;/&lt;부스&gt;/&lt;Zone&gt;/&lt;YYYYMMDD-HHMM&gt;_&lt;파일명&gt;
///   고객사 층은 없다. 폴더는 첫 업로드 때 만든다. 금지문자는 '_'.
/// 큰 Cell(수 GB)도 받을 수 있게 조각으로 나눠 올린다(끊기면 받은 크기부터 이어 올림).
/// 원본 런처의 Google Drive 이력(file_ref_id 없음)은 목록에만 보이고 내려받기는 안 된다(Drive 이관 전).
/// 권한(작업자·관리자 구분)은 아직 넣지 않았다 — 로그인한 직원이면 누구나 올리고 지울 수 있다.
/// </summary>
public static class MotosimFiles
{
    public const int ChunkSize = 8 * 1024 * 1024;
    private const int MaxChunk = 32 * 1024 * 1024;
    private const long MaxFileSize = 50L * 1024 * 1024 * 1024;
    private static readonly Regex CodeYear = new(@"^[A-Za-z]+-(\d{2})-", RegexOptions.Compiled);

    public static void MapMotosimFiles(this RouteGroupBuilder g)
    {
        // 이 Zone(또는 그 부스·공장 단위로 배정된)에 연결된 진행 프로젝트 — 업로드할 때 고른다
        g.MapGet("/zones/{id:long}/projects", async (long id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, write: false, ct);
            var list = new List<MotosimProjectDto>();
            await using var cmd = new NpgsqlCommand(@"
                select distinct p.id, p.code, p.name
                from plant.zone z
                join plant.booth b on b.id = z.booth_id
                join project.project_assignment a on a.is_active and a.removed_at is null
                     and (a.zone_id = z.id
                          or (a.zone_id is null and a.booth_id = b.id)
                          or (a.zone_id is null and a.booth_id is null and a.factory_id = b.factory_id))
                join project.project p on p.id = a.project_id and p.is_active
                where z.id = @zid
                order by p.code desc", conn);
            cmd.Parameters.AddWithValue("zid", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new MotosimProjectDto(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            return Results.Ok(list);
        });

        // 나눠 올리기 시작
        g.MapPost("/uploads", async (MotosimUploadStartRequest req, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            var name = Path.GetFileName(req.FileName ?? "").Trim();
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Bad("Cell 파일은 .zip 이어야 합니다.");
            if (req.TotalSize <= 0 || req.TotalSize > MaxFileSize) return Bad("파일 크기가 올바르지 않습니다(최대 50GB).");
            await using var conn = await OpenAsync(config, write: true, ct);
            if (!await ProjectOnZoneAsync(conn, req.ZoneId, req.ProjectId, ct))
                return Bad("이 Zone에 연결된 프로젝트가 아닙니다. 프로젝트를 다시 골라 주세요.");
            await using var cmd = new NpgsqlCommand(@"
                insert into motosim.upload_session (zone_id, project_id, file_name, total_size, commit_message, motosim_version, local_path, created_by)
                values (@z, @p, @n, @s, @m, @v, @l, @me) returning id", conn);
            cmd.Parameters.AddWithValue("z", req.ZoneId);
            cmd.Parameters.AddWithValue("p", req.ProjectId);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("s", req.TotalSize);
            cmd.Parameters.AddWithValue("m", (object?)Clean(req.CommitMessage) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("v", (object?)Clean(req.MotosimVersion) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("l", (object?)Clean(req.LocalPath) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("me", me);
            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            return Results.Ok(await SessionAsync(conn, id, ct));
        });

        g.MapGet("/uploads/{id:guid}", async (Guid id, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, write: false, ct);
            var s = await SessionAsync(conn, id, ct);
            return s is null ? Results.NotFound() : Results.Ok(s);
        });

        // 조각 하나(본문 그대로). offset이 지금까지 받은 크기와 같아야 한다 — 다르면 409와 함께 받은 크기를 돌려준다.
        g.MapPut("/uploads/{id:guid}/chunk", async (Guid id, long offset, HttpRequest http, ClaimsPrincipal principal,
            IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            await using var conn = await OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long total, received; string status; long owner;
            await using (var q = new NpgsqlCommand("select total_size, received, status, created_by from motosim.upload_session where id = @id for update", conn, tx))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                (total, received, status, owner) = (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3));
            }
            if (owner != me) return Results.Forbid();
            if (status != "open") return Bad("이미 끝났거나 취소된 업로드입니다.");
            if (offset != received) return Results.Conflict(new { received });

            var part = PartPath(storage, id);
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            long written;
            await using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(received); // 앞서 끊긴 조각 찌꺼기는 잘라낸다
                fs.Position = received;
                written = await CopyLimitedAsync(http.Body, fs, Math.Min(MaxChunk, total - received), ct);
            }
            if (written <= 0) return Bad("빈 조각입니다.");
            if (received + written > total) return Bad("파일 크기보다 많이 받았습니다.");
            await using (var u = new NpgsqlCommand("update motosim.upload_session set received = @r, updated_at = now() where id = @id", conn, tx))
            {
                u.Parameters.AddWithValue("r", received + written);
                u.Parameters.AddWithValue("id", id);
                await u.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { received = received + written, total });
        });

        // 다 받았으면 NAS 폴더로 옮기고 이력에 남긴다. 같은 Zone·프로젝트의 이전 최신은 최신 표시를 내린다.
        g.MapPost("/uploads/{id:guid}/complete", async (Guid id, ClaimsPrincipal principal, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            await using var conn = await OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long zoneId, projectId, total, received, owner; string fileName, status; string? message, version, localPath;
            await using (var q = new NpgsqlCommand(@"
                select zone_id, project_id, total_size, received, created_by, file_name, status, commit_message, motosim_version, local_path
                from motosim.upload_session where id = @id for update", conn, tx))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                (zoneId, projectId, total, received, owner) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
                (fileName, status) = (r.GetString(5), r.GetString(6));
                (message, version, localPath) = (S(r, 7), S(r, 8), S(r, 9));
            }
            if (owner != me) return Results.Forbid();
            if (status != "open") return Bad("이미 끝났거나 취소된 업로드입니다.");
            if (received != total) return Bad($"아직 다 받지 못했습니다({received}/{total} 바이트).");
            var part = PartPath(storage, id);
            if (!File.Exists(part) || new FileInfo(part).Length != total) return Bad("받은 임시 파일이 없거나 크기가 다릅니다. 다시 올려 주세요.");

            string sha;
            await using (var fs = File.OpenRead(part)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));

            var dirRel = await FolderAsync(conn, tx, zoneId, projectId, ct);
            if (dirRel is null) return Bad("Zone 또는 프로젝트를 찾을 수 없습니다.");
            var kst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
            var rel = storage.MoveInto(part, dirRel, $"{kst:yyyyMMdd-HHmm}_{fileName}");
            var stored = Path.GetFileName(rel);
            try
            {
                var (empNo, empName, dept) = await EmployeeAsync(conn, tx, me, ct);
                long refId;
                await using (var c = new NpgsqlCommand(@"
                    insert into platform.file_ref (kind, storage, path, original_name, size_bytes, mime)
                    values ('motosim_cell', @st, @p, @o, @s, 'application/zip') returning id", conn, tx))
                {
                    c.Parameters.AddWithValue("st", storage.Root.StartsWith(@"\\") ? "unc" : "local");
                    c.Parameters.AddWithValue("p", rel);
                    c.Parameters.AddWithValue("o", fileName);
                    c.Parameters.AddWithValue("s", total);
                    refId = (long)(await c.ExecuteScalarAsync(ct))!;
                }
                await using (var c = new NpgsqlCommand(
                    "update motosim.cell_file set is_latest = false where zone_id = @z and coalesce(project_id, 0) = @p and is_latest", conn, tx))
                {
                    c.Parameters.AddWithValue("z", zoneId);
                    c.Parameters.AddWithValue("p", projectId);
                    await c.ExecuteNonQueryAsync(ct);
                }
                long cellId;
                await using (var c = new NpgsqlCommand(@"
                    insert into motosim.cell_file (zone_id, project_id, file_name, file_ref_id, file_size, sha256, motosim_version, commit_message,
                        uploaded_by, uploaded_by_user_id, uploaded_by_name, uploaded_by_department, is_latest, metadata)
                    values (@z, @p, @n, @ref, @s, @sha, @v, @m, @me, @emp, @name, @dept, true, jsonb_build_object('localPath', @l::text, 'storedName', @stored::text))
                    returning cell_file_id", conn, tx))
                {
                    c.Parameters.AddWithValue("z", zoneId);
                    c.Parameters.AddWithValue("p", projectId);
                    c.Parameters.AddWithValue("n", fileName);
                    c.Parameters.AddWithValue("ref", refId);
                    c.Parameters.AddWithValue("s", total);
                    c.Parameters.AddWithValue("sha", sha);
                    c.Parameters.AddWithValue("v", (object?)version ?? DBNull.Value);
                    c.Parameters.AddWithValue("m", (object?)message ?? DBNull.Value);
                    c.Parameters.AddWithValue("me", me);
                    c.Parameters.AddWithValue("emp", (object?)empNo ?? DBNull.Value);
                    c.Parameters.AddWithValue("name", empName);
                    c.Parameters.AddWithValue("dept", (object?)dept ?? DBNull.Value);
                    c.Parameters.AddWithValue("l", (object?)localPath ?? DBNull.Value);
                    c.Parameters.AddWithValue("stored", stored);
                    cellId = (long)(await c.ExecuteScalarAsync(ct))!;
                }
                await using (var c = new NpgsqlCommand(
                    "update motosim.upload_session set status = 'completed', cell_file_id = @c, updated_at = now() where id = @id", conn, tx))
                {
                    c.Parameters.AddWithValue("c", cellId);
                    c.Parameters.AddWithValue("id", id);
                    await c.ExecuteNonQueryAsync(ct);
                }
                await ActivityAsync(conn, tx, "cell-upload", zoneId, projectId, cellId, me, empName, $"{fileName} 업로드", ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { cellFileId = cellId, path = rel, sha256 = sha });
            }
            catch
            {
                // DB에 못 남기면 옮긴 파일을 임시 자리로 되돌려 다시 완료할 수 있게 한다
                try { File.Move(storage.FullPath(rel), part); } catch { /* 되돌리기 실패는 그대로 둔다 */ }
                throw;
            }
        });

        g.MapDelete("/uploads/{id:guid}", async (Guid id, ClaimsPrincipal principal, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            await using var conn = await OpenAsync(config, write: true, ct);
            await using var cmd = new NpgsqlCommand(
                "update motosim.upload_session set status = 'cancelled', updated_at = now() where id = @id and created_by = @me and status = 'open'", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("me", me);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            try { File.Delete(PartPath(storage, id)); } catch (IOException) { }
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });

        // 내려받기(이어 받기용 Range 지원). Drive에만 있는 옛 이력은 404.
        g.MapGet("/files/{id:long}/download", async (long id, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            await using var conn = await OpenAsync(config, write: false, ct);
            string? path; string name; long zoneId; long? projectId; bool deleted;
            await using (var q = new NpgsqlCommand(@"
                select r.path, coalesce(c.file_name, c.drive_file_name, 'cell.zip'), c.zone_id, c.project_id, c.is_deleted
                from motosim.cell_file c left join platform.file_ref r on r.id = c.file_ref_id where c.cell_file_id = @id", conn))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                (path, name, zoneId, projectId, deleted) = (S(r, 0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetBoolean(4));
            }
            if (deleted) return Results.Problem("삭제된 파일입니다.", statusCode: 410);
            if (path is null) return Results.Problem("원본 런처의 Google Drive에 있는 파일입니다(NAS로 옮기기 전이라 여기서는 받을 수 없습니다).", statusCode: 404);
            var full = storage.FullPath(path);
            if (!File.Exists(full)) return Results.Problem("NAS에서 파일을 찾을 수 없습니다.", statusCode: 404);
            // 활동 기록은 받기를 마친 뒤 /downloaded 로 따로 남긴다(Range 조각마다 쌓이지 않게)
            return Results.File(full, "application/zip", name, enableRangeProcessing: true);
        });

        // 다운로드 기록(Broker·화면이 한 번 받기를 마친 뒤 부른다 — Range 조각마다 쌓이지 않게 분리)
        g.MapPost("/files/{id:long}/downloaded", async (long id, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            await using var conn = await OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long zoneId; long? projectId; string name;
            await using (var q = new NpgsqlCommand("select zone_id, project_id, coalesce(file_name, drive_file_name, '') from motosim.cell_file where cell_file_id = @id", conn, tx))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                (zoneId, projectId, name) = (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2));
            }
            var (_, empName, _) = await EmployeeAsync(conn, tx, me, ct);
            await ActivityAsync(conn, tx, "cell-download", zoneId, projectId, id, me, empName, $"{name} 다운로드", ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // 삭제: 이력은 지우지 않고 삭제 표시, NAS 파일은 _trash로 옮긴다. 지운 게 최신이면 같은 Zone·프로젝트의 다음 파일을 최신으로.
        g.MapPost("/files/{id:long}/delete", async (long id, ClaimsPrincipal principal, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            if (!TryMe(principal, out var me)) return Results.Unauthorized();
            await using var conn = await OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            long zoneId; long? projectId, refId; string name; string? path; bool latest, deleted;
            await using (var q = new NpgsqlCommand(@"
                select c.zone_id, c.project_id, c.file_ref_id, coalesce(c.file_name, c.drive_file_name, ''), r.path, c.is_latest, c.is_deleted
                from motosim.cell_file c left join platform.file_ref r on r.id = c.file_ref_id where c.cell_file_id = @id for update of c", conn, tx))
            {
                q.Parameters.AddWithValue("id", id);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                zoneId = r.GetInt64(0);
                projectId = r.IsDBNull(1) ? null : r.GetInt64(1);
                refId = r.IsDBNull(2) ? null : r.GetInt64(2);
                (name, path, latest, deleted) = (r.GetString(3), S(r, 4), r.GetBoolean(5), r.GetBoolean(6));
            }
            if (deleted) return Results.NoContent();
            if (refId is null) return Bad("Google Drive에 있는 옛 이력은 아직 여기서 지울 수 없습니다.");

            await using (var c = new NpgsqlCommand("update motosim.cell_file set is_deleted = true, is_latest = false where cell_file_id = @id", conn, tx))
            {
                c.Parameters.AddWithValue("id", id);
                await c.ExecuteNonQueryAsync(ct);
            }
            if (latest)
            {
                await using var c = new NpgsqlCommand(@"
                    update motosim.cell_file set is_latest = true where cell_file_id = (
                        select cell_file_id from motosim.cell_file
                        where zone_id = @z and coalesce(project_id, 0) = @p and not is_deleted
                        order by uploaded_at desc, cell_file_id desc limit 1)", conn, tx);
                c.Parameters.AddWithValue("z", zoneId);
                c.Parameters.AddWithValue("p", projectId ?? 0);
                await c.ExecuteNonQueryAsync(ct);
            }
            string? trashRel = null;
            if (path is not null && storage.Exists(path))
            {
                var kst = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
                trashRel = storage.MoveInto(storage.FullPath(path), $"_trash/motosim/{kst:yyyyMMdd}", Path.GetFileName(path));
                await using var c = new NpgsqlCommand("update platform.file_ref set path = @p where id = @id", conn, tx);
                c.Parameters.AddWithValue("p", trashRel);
                c.Parameters.AddWithValue("id", refId.Value);
                await c.ExecuteNonQueryAsync(ct);
            }
            try
            {
                var (_, empName, _) = await EmployeeAsync(conn, tx, me, ct);
                await ActivityAsync(conn, tx, "cell-delete", zoneId, projectId, id, me, empName, $"{name} 삭제", ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                if (trashRel is not null && path is not null)
                    try { File.Move(storage.FullPath(trashRel), storage.FullPath(path)); } catch { /* 되돌리기 실패는 그대로 둔다 */ }
                throw;
            }
            return Results.NoContent();
        });

        // 즐겨찾기 넣기·빼기. 원본 런처와 같게 사번으로 저장한다.
        g.MapPut("/favorites/{zoneId:long}", (long zoneId, ClaimsPrincipal p, IConfiguration config, CancellationToken ct) =>
            FavoriteAsync(zoneId, add: true, p, config, ct));
        g.MapDelete("/favorites/{zoneId:long}", (long zoneId, ClaimsPrincipal p, IConfiguration config, CancellationToken ct) =>
            FavoriteAsync(zoneId, add: false, p, config, ct));
    }

    private static async Task<IResult> FavoriteAsync(long zoneId, bool add, ClaimsPrincipal principal, IConfiguration config, CancellationToken ct)
    {
        if (!TryMe(principal, out var me)) return Results.Unauthorized();
        await using var conn = await OpenAsync(config, write: true, ct);
        var sql = add
            ? @"insert into motosim.favorite_zone (user_id, zone_id)
                select coalesce(e.emp_no, e.login_id), @z from org.employee e where e.id = @me
                on conflict do nothing"
            : @"delete from motosim.favorite_zone f using org.employee e
                where e.id = @me and f.zone_id = @z and (f.user_id = e.emp_no or lower(f.user_id) = lower(e.login_id))";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("z", zoneId);
        cmd.Parameters.AddWithValue("me", me);
        await cmd.ExecuteNonQueryAsync(ct);
        return Results.NoContent();
    }

    /// <summary>NAS 상대 폴더: 프로젝트/&lt;코드 연도&gt;/&lt;코드&gt;_&lt;이름&gt;/MotoSim Cell/&lt;공장&gt;/&lt;부스&gt;/&lt;Zone&gt;</summary>
    public static string BuildFolder(string projectCode, string projectName, DateOnly? startDate, string factory, string booth, string zone)
    {
        var m = CodeYear.Match(projectCode);
        var year = m.Success ? $"20{m.Groups[1].Value}" : startDate?.Year.ToString() ?? "연도미상";
        var seg = FileStorage.SafeSegment;
        return string.Join('/', "프로젝트", year, seg($"{projectCode}_{projectName}"), "MotoSim Cell", seg(factory), seg(booth), seg(zone));
    }

    private static async Task<string?> FolderAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long zoneId, long projectId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(@"
            select p.code, p.name, p.start_date, f.name, b.name, z.name
            from plant.zone z join plant.booth b on b.id = z.booth_id join plant.factory f on f.id = b.factory_id
            cross join project.project p where z.id = @z and p.id = @p", conn, tx);
        q.Parameters.AddWithValue("z", zoneId);
        q.Parameters.AddWithValue("p", projectId);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return BuildFolder(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2), r.GetString(3), r.GetString(4), r.GetString(5));
    }

    private static async Task<bool> ProjectOnZoneAsync(NpgsqlConnection conn, long zoneId, long projectId, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(@"
            select exists (
                select 1 from plant.zone z join plant.booth b on b.id = z.booth_id
                join project.project_assignment a on a.is_active and a.removed_at is null
                     and (a.zone_id = z.id
                          or (a.zone_id is null and a.booth_id = b.id)
                          or (a.zone_id is null and a.booth_id is null and a.factory_id = b.factory_id))
                where z.id = @z and a.project_id = @p)", conn);
        q.Parameters.AddWithValue("z", zoneId);
        q.Parameters.AddWithValue("p", projectId);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }

    private static async Task<(string? EmpNo, string Name, string? Dept)> EmployeeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long me, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            "select e.emp_no, e.name, d.name from org.employee e left join org.department d on d.id = e.department_id where e.id = @me", conn, tx);
        q.Parameters.AddWithValue("me", me);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (S(r, 0), r.GetString(1), S(r, 2)) : (null, "", null);
    }

    internal static async Task ActivityAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, long? zoneId, long? projectId, long? cellId,
        long me, string actorName, string summary, CancellationToken ct, long? noticeId = null)
    {
        await using var c = new NpgsqlCommand(@"
            insert into motosim.activity (kind, zone_id, project_id, cell_file_id, notice_id, actor_id, actor_name, summary)
            values (@k, @z, @p, @c, @n, @me, @a, @s)", conn, tx);
        c.Parameters.AddWithValue("k", kind);
        c.Parameters.AddWithValue("z", (object?)zoneId ?? DBNull.Value);
        c.Parameters.AddWithValue("p", (object?)projectId ?? DBNull.Value);
        c.Parameters.AddWithValue("c", (object?)cellId ?? DBNull.Value);
        c.Parameters.AddWithValue("n", (object?)noticeId ?? DBNull.Value);
        c.Parameters.AddWithValue("me", me);
        c.Parameters.AddWithValue("a", actorName);
        c.Parameters.AddWithValue("s", summary);
        await c.ExecuteNonQueryAsync(ct);
    }

    private static async Task<MotosimUploadDto?> SessionAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(
            "select zone_id, project_id, file_name, total_size, received, status, cell_file_id, error from motosim.upload_session where id = @id", conn);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new MotosimUploadDto(id, r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5), ChunkSize,
            r.IsDBNull(6) ? null : r.GetInt64(6), S(r, 7));
    }

    private static async Task<long> CopyLimitedAsync(Stream src, Stream dst, long limit, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            if (total + n > limit) throw new BadHttpRequestException("조각이 너무 큽니다.", 413);
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            total += n;
        }
        return total;
    }

    private static string PartPath(FileStorage storage, Guid id) => Path.Combine(storage.Root, "_tmp", "motosim-uploads", $"{id:N}.part");

    internal static async Task<NpgsqlConnection> OpenAsync(IConfiguration config, bool write, CancellationToken ct)
    {
        var cs = config.GetConnectionString("DyOne");
        // 쓰기 연결: 기본 연결 문자열의 읽기 전용 옵션(Options)을 뺀다(ProjectFilesModule과 같은 방식)
        if (write) cs = new NpgsqlConnectionStringBuilder(cs) { Options = null }.ConnectionString;
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    internal static bool TryMe(ClaimsPrincipal p, out long me) => long.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out me);
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static IResult Bad(string message) => Results.Problem(message, statusCode: 400);
}
