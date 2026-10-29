using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DyOne.Api.Modules.Projects;
using Npgsql;

namespace DyOne.Api.Modules.Motosim;

public record PluginAssetDto(string FileName, string? Sha256, long Size);
public record PluginLatestDto(string Id, string Version, PluginAssetDto? Full, PluginAssetDto? Update, string[] SupportedMotoSim, List<PluginNoteItem> Items);
public record PluginNoteItem(string Content);
public record PluginVersionDto(int Id, string Version, string DisplayName, List<PluginNoteItem> Items, bool FullUploaded, bool UpdateUploaded,
    bool FullOnNas, bool UpdateOnNas, DateTimeOffset UpdatedAt, string? UploadedByName);
public record PluginNotesRequest(List<PluginNoteItem> Items);
public record InstallerDto(string Name, string Label, string? VersionLabel, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// Smart Cell Plugin 배포·릴리즈노트, MotoSim 설치 파일 목록(원본 런처는 둘 다 Google Drive).
/// NAS 위치: &lt;Root&gt;/_motosim/plugins/smartcell/&lt;버전&gt;/ · &lt;Root&gt;/_motosim/installers/
/// 설치 파일 이름 규칙(원본과 같음): SmartCell_Plugin_Setup_(Full|Update)_v&lt;a.b.c.d&gt;.exe
/// 권한(시스템관리자만 올리기·지우기)은 아직 넣지 않았다.
/// </summary>
public static partial class MotosimPlugins
{
    private const string PluginId = "smartcell";
    private static readonly string[] SupportedMotoSim = ["2021", "2022", "2023", "2024", "2025"];

    public static void MapMotosimPlugins(this RouteGroupBuilder g)
    {
        // 최신 = Full 설치 파일이 NAS에 있는 가장 높은 버전
        g.MapGet("/plugins/smartcell/latest", async (IConfiguration config, CancellationToken ct) =>
        {
            var rows = await VersionsAsync(config, ct);
            var latest = rows.Where(r => r.Row.FullOnNas).OrderByDescending(r => r.Row.Version, VersionComparer.Instance).FirstOrDefault();
            if (latest is null) return Results.Problem("NAS에 올라간 Smart Cell Plugin Full 설치 파일이 없습니다.", statusCode: 404);
            return Results.Ok(new PluginLatestDto(PluginId, latest.Row.Version, latest.Full, latest.Update, SupportedMotoSim, latest.Row.Items));
        });

        g.MapGet("/plugins/smartcell/versions", async (IConfiguration config, CancellationToken ct) =>
            Results.Ok((await VersionsAsync(config, ct)).Select(r => r.Row).OrderByDescending(r => r.Version, VersionComparer.Instance)));

        g.MapGet("/plugins/smartcell/download", async (string channel, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            var rows = await VersionsAsync(config, ct);
            var latest = rows.Where(r => r.Row.FullOnNas).OrderByDescending(r => r.Row.Version, VersionComparer.Instance).FirstOrDefault();
            var path = channel == "update" ? latest?.UpdatePath ?? latest?.FullPath : latest?.FullPath;
            if (path is null || !storage.Exists(path)) return Results.Problem("설치 파일이 없습니다.", statusCode: 404);
            return Results.File(storage.FullPath(path), "application/vnd.microsoft.portable-executable", Path.GetFileName(path), enableRangeProcessing: true);
        });

        // 설치 파일 올리기(Full·Update 하나씩, 같은 버전). 같은 이름이 있으면 덮어쓴다.
        g.MapPost("/plugins/smartcell/upload", async (HttpRequest http, ClaimsPrincipal principal, IConfiguration config, FileStorage storage, CancellationToken ct) =>
        {
            if (!MotosimFiles.TryMe(principal, out var me)) return Results.Unauthorized();
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count is 0 or > 2) return Results.Problem("Full·Update 설치 파일을 하나씩 골라 주세요.", statusCode: 400);
            var parsed = form.Files.Select(f => (File: f, M: SetupName().Match(Path.GetFileName(f.FileName)))).ToList();
            if (parsed.Any(p => !p.M.Success))
                return Results.Problem("파일 이름은 SmartCell_Plugin_Setup_Full_v1.2.3.4.exe / SmartCell_Plugin_Setup_Update_v1.2.3.4.exe 형식이어야 합니다.", statusCode: 400);
            var version = parsed[0].M.Groups[2].Value;
            if (parsed.Any(p => p.M.Groups[2].Value != version)) return Results.Problem("Full·Update 버전이 같아야 합니다.", statusCode: 400);
            if (parsed.GroupBy(p => p.M.Groups[1].Value.ToLowerInvariant()).Any(x => x.Count() > 1)) return Results.Problem("Full·Update는 하나씩만 올릴 수 있습니다.", statusCode: 400);

            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand(@"
                insert into motosim.plugin_version (plugin_id, version, items, uploaded_by_name)
                select @p, @v, '[]'::jsonb, e.name from org.employee e where e.id = @me
                on conflict (plugin_id, version) do update set is_deleted = false, updated_at = now()", conn, tx))
            {
                c.Parameters.AddWithValue("p", PluginId);
                c.Parameters.AddWithValue("v", version);
                c.Parameters.AddWithValue("me", me);
                await c.ExecuteNonQueryAsync(ct);
            }
            var dir = Path.Combine(storage.Root, "_motosim", "plugins", PluginId, version);
            Directory.CreateDirectory(dir);
            foreach (var (file, m) in parsed)
            {
                var channel = m.Groups[1].Value.Equals("Full", StringComparison.OrdinalIgnoreCase) ? "full" : "update";
                var name = $"SmartCell_Plugin_Setup_{(channel == "full" ? "Full" : "Update")}_v{version}.exe";
                var full = Path.Combine(dir, name);
                await using (var src = file.OpenReadStream())
                await using (var dst = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst, ct);
                byte[] head = new byte[2];
                await using (var fs = File.OpenRead(full)) { await fs.ReadExactlyAsync(head, ct); }
                if (head[0] != 'M' || head[1] != 'Z') { File.Delete(full); return Results.Problem($"{name}은(는) 실행 파일(exe)이 아닙니다.", statusCode: 400); }
                string sha;
                await using (var fs = File.OpenRead(full)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
                long refId;
                await using (var c = new NpgsqlCommand(@"
                    insert into platform.file_ref (kind, storage, path, original_name, size_bytes, mime)
                    values ('motosim_plugin', @st, @path, @o, @s, 'application/vnd.microsoft.portable-executable') returning id", conn, tx))
                {
                    c.Parameters.AddWithValue("st", storage.Root.StartsWith(@"\\") ? "unc" : "local");
                    c.Parameters.AddWithValue("path", $"_motosim/plugins/{PluginId}/{version}/{name}");
                    c.Parameters.AddWithValue("o", Path.GetFileName(file.FileName));
                    c.Parameters.AddWithValue("s", file.Length);
                    refId = (long)(await c.ExecuteScalarAsync(ct))!;
                }
                await using (var c = new NpgsqlCommand($@"
                    update motosim.plugin_version set {channel}_file_ref_id = @r, {channel}_sha256 = @sha, {channel}_uploaded = true, updated_at = now()
                    where plugin_id = @p and version = @v", conn, tx))
                {
                    c.Parameters.AddWithValue("r", refId);
                    c.Parameters.AddWithValue("sha", sha);
                    c.Parameters.AddWithValue("p", PluginId);
                    c.Parameters.AddWithValue("v", version);
                    await c.ExecuteNonQueryAsync(ct);
                }
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { version });
        }).DisableAntiforgery();

        // 릴리즈노트 바꾸기(1~30개, 한 줄 300자 이하)
        g.MapPut("/plugins/smartcell/versions/{version}/notes", async (string version, PluginNotesRequest req, IConfiguration config, CancellationToken ct) =>
        {
            var items = (req.Items ?? []).Select(i => i.Content?.Trim() ?? "").Where(s => s.Length > 0).ToList();
            if (items.Count is 0 or > 30 || items.Any(s => s.Length > 300)) return Results.Problem("변경 내역은 1~30줄, 한 줄 300자 이하로 적어 주세요.", statusCode: 400);
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var c = new NpgsqlCommand(
                "update motosim.plugin_version set items = @i::jsonb, updated_at = now() where plugin_id = @p and version = @v and not is_deleted", conn);
            c.Parameters.AddWithValue("i", JsonSerializer.Serialize(items.Select(s => new { content = s })));
            c.Parameters.AddWithValue("p", PluginId);
            c.Parameters.AddWithValue("v", version);
            return await c.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        // 버전 내리기(지우지 않고 숨긴다. NAS 파일도 그대로)
        g.MapDelete("/plugins/smartcell/versions/{version}", async (string version, IConfiguration config, CancellationToken ct) =>
        {
            await using var conn = await MotosimFiles.OpenAsync(config, write: true, ct);
            await using var c = new NpgsqlCommand(
                "update motosim.plugin_version set is_deleted = true, updated_at = now() where plugin_id = @p and version = @v", conn);
            c.Parameters.AddWithValue("p", PluginId);
            c.Parameters.AddWithValue("v", version);
            return await c.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        // MotoSim 설치 파일: NAS 폴더에 있는 파일 그대로(연도 높은 순 → 최근 수정 순)
        g.MapGet("/installers", (FileStorage storage) =>
        {
            var dir = InstallerDir(storage);
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<InstallerDto>());
            var list = new DirectoryInfo(dir).EnumerateFiles()
                .Where(f => !f.Name.StartsWith('.') && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Select(f => new InstallerDto(f.Name, Path.GetFileNameWithoutExtension(f.Name), Year().Match(f.Name) is { Success: true } m ? m.Value : null,
                    f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
                .OrderByDescending(f => f.VersionLabel ?? "").ThenByDescending(f => f.ModifiedAt).ThenBy(f => f.Name)
                .ToList();
            return Results.Ok(list);
        });

        g.MapGet("/installers/download", (string name, FileStorage storage) =>
        {
            // 목록에 있는 파일 이름만(경로 섞기 차단)
            var safe = Path.GetFileName(name);
            var full = Path.Combine(InstallerDir(storage), safe);
            if (safe != name || !File.Exists(full)) return Results.Problem("설치 파일이 없습니다.", statusCode: 404);
            return Results.File(full, "application/octet-stream", safe, enableRangeProcessing: true);
        });
    }

    private static string InstallerDir(FileStorage storage) => Path.Combine(storage.Root, "_motosim", "installers");

    private sealed record VersionRow(PluginVersionDto Row, PluginAssetDto? Full, PluginAssetDto? Update, string? FullPath, string? UpdatePath);

    private static async Task<List<VersionRow>> VersionsAsync(IConfiguration config, CancellationToken ct)
    {
        await using var conn = await MotosimFiles.OpenAsync(config, write: false, ct);
        await using var q = new NpgsqlCommand(@"
            select v.plugin_version_id, v.version, v.items::text, v.full_uploaded, v.update_uploaded, v.updated_at, v.uploaded_by_name,
                   f.path, f.size_bytes, v.full_sha256, u.path, u.size_bytes, v.update_sha256
            from motosim.plugin_version v
            left join platform.file_ref f on f.id = v.full_file_ref_id
            left join platform.file_ref u on u.id = v.update_file_ref_id
            where v.plugin_id = @p and not v.is_deleted", conn);
        q.Parameters.AddWithValue("p", PluginId);
        await using var r = await q.ExecuteReaderAsync(ct);
        var list = new List<VersionRow>();
        while (await r.ReadAsync(ct))
        {
            var version = r.GetString(1);
            var items = JsonSerializer.Deserialize<List<PluginNoteItem>>(r.GetString(2), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var fullPath = r.IsDBNull(7) ? null : r.GetString(7);
            var updPath = r.IsDBNull(10) ? null : r.GetString(10);
            var row = new PluginVersionDto(r.GetInt32(0), version, $"SmartCell_Plugin_Setup_v{version}.exe", items, r.GetBoolean(3), r.GetBoolean(4),
                fullPath is not null, updPath is not null, r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetString(6));
            list.Add(new VersionRow(row,
                fullPath is null ? null : new PluginAssetDto(Path.GetFileName(fullPath), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(8) ? 0 : r.GetInt64(8)),
                updPath is null ? null : new PluginAssetDto(Path.GetFileName(updPath), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(11) ? 0 : r.GetInt64(11)),
                fullPath, updPath));
        }
        return list;
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            int[] P(string? s) => (s ?? "").Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
            var (x, y) = (P(a), P(b));
            for (var i = 0; i < 4; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }
    }

    [GeneratedRegex(@"^SmartCell_Plugin_Setup_(Full|Update)_v(\d+\.\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase)] private static partial Regex SetupName();
    [GeneratedRegex(@"20\d{2}")] private static partial Regex Year();
}
