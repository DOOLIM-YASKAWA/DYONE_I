using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>NAS 프로젝트 폴더 하나의 최근 작업 흔적(파일 이름·수정 시각만, 내용은 열지 않음).</summary>
public sealed class NasTrace
{
    public int N30, N14, N1;
    /// <summary>날짜(한국 시간)별 수정 파일 수 — '꾸준히 작업'과 '한 번에 올림'을 가른다</summary>
    public Dictionary<DateOnly, int> Days = [];
    public DateTimeOffset? Last;
    public string? Dir;
    public Dictionary<string, int> Top = [];
    public List<(string Name, string Dir, DateTimeOffset At)> Recent = [];
    /// <summary>날짜 × 폴더(작업 폴더 아래 3단까지)별 수정 파일 수와 대표 파일 이름(최대 3) — 근거 화면의 'NAS 흐름'(nas_flow)</summary>
    public Dictionary<(DateOnly Day, string Dir), FlowCell> Flow = [];
}

/// <summary>흐름 한 칸. 대표 파일은 '가장 최근에 고친 3개'(같은 시각이면 이름순) — 폴더를 읽는 순서와 상관없이 늘 같게.</summary>
public sealed class FlowCell
{
    public int N;
    public List<(string Name, DateTime At)> Picks = [];
    public void Add(string name, DateTime at)
    {
        N++;
        Keep(name, at);
    }
    public void Keep(string name, DateTime at)
    {
        if (Picks.Any(p => p.Name == name)) return;
        Picks.Add((name, at));
        if (Picks.Count > 3) Picks = Picks.OrderByDescending(p => p.At).ThenBy(p => p.Name, StringComparer.Ordinal).Take(3).ToList();
    }
    public IEnumerable<string> Files => Picks.OrderByDescending(p => p.At).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name);
}

/// <summary>
/// 흔적 모으기: NAS(읽기 전용) · ERP 발주 집계 · 모듈라/ES 기록 → sync.project_trace(매일 전체 다시 씀).
/// v3(11-04): NAS는 작업 폴더(2.사양서 3.수행 계획서 4.도면 5.모듈라빌드 6~9 DATA)만 센다.
///   1.제안서·10.매뉴얼·11.회의록·12.완료보고서·13.기타자료·틀 밖 폴더, 사진·동영상, 소음 파일(Thumbs.db·CAD 잠금·.log·.bak·.tmp·~$)은 세지 않는다.
///   날짜별로 세어 '꾸준히 작업'(바뀐 날 수)과 '한 번에 올림'(하루 몰림)을 가른다. 근거: docs/roadmap/회사 주요 프로젝트 선정 기준 v3 3-2절.
/// </summary>
public static partial class TraceCollector
{
    private static readonly HashSet<string> NoiseExt = new(StringComparer.OrdinalIgnoreCase) { ".db", ".dwl", ".dwl2", ".log", ".bak", ".tmp", ".lck" };
    private static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".heic", ".bmp", ".gif", ".mp4", ".mov", ".avi", ".wmv" };

    /// <summary>작업 폴더: NAS 13칸 중 2~9번(사양서·수행 계획서·도면·모듈라빌드·티칭·PLC·HMI·커미셔닝 DATA)</summary>
    [GeneratedRegex(@"^[2-9]\s*\.")]
    private static partial Regex WorkDirRe();

    [GeneratedRegex(@"(DL[A-Z]*-\d{2}-\d{3})")]
    private static partial Regex CodeRe();

    public static Dictionary<string, NasTrace> ScanNas(IEnumerable<string> roots, DateTimeOffset now, ILogger log, CancellationToken ct)
    {
        var d30 = now.AddDays(-30).UtcDateTime;
        var d14 = now.AddDays(-14).UtcDateTime;
        var yStart = new DateTimeOffset(now.Date.AddDays(-1), now.Offset).UtcDateTime;
        var yEnd = new DateTimeOffset(now.Date, now.Offset).UtcDateTime;
        var dirs = new List<(string Code, string Path, string Label)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { log.LogWarning("NAS 뿌리 없음: {Root}", root); continue; }
            foreach (var cat in SafeDirs(root))
                foreach (var pd in SafeDirs(cat))
                    if (CodeRe().Match(Path.GetFileName(pd)) is { Success: true } m)
                        dirs.Add((m.Groups[1].Value, pd, Path.GetRelativePath(Path.GetDirectoryName(root) ?? root, pd)));
        }
        var result = new ConcurrentDictionary<string, NasTrace>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, MaxRecursionDepth = 14 };
        Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, d =>
        {
            var t = new NasTrace { Dir = d.Label };
            try
            {
                var files = new DirectoryInfo(d.Path).EnumerateDirectories()
                    .Where(w => WorkDirRe().IsMatch(w.Name))
                    .SelectMany(w => w.EnumerateFiles("*", opts));
                foreach (var f in files)
                {
                    var at = f.LastWriteTimeUtc;
                    if (at < d30 || at > now.UtcDateTime.AddDays(1)) continue;
                    if (NoiseExt.Contains(f.Extension) || MediaExt.Contains(f.Extension) || f.Name.StartsWith("~$", StringComparison.Ordinal) || f.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
                    t.N30++;
                    var day = DateOnly.FromDateTime(new DateTimeOffset(at, TimeSpan.Zero).ToOffset(now.Offset).DateTime);
                    t.Days[day] = t.Days.GetValueOrDefault(day) + 1;
                    if (at >= d14) t.N14++;
                    if (at >= yStart && at < yEnd) t.N1++;
                    var rel = Path.GetRelativePath(d.Path, f.DirectoryName ?? d.Path).Replace('\\', '/');
                    var parts = rel.Split('/');
                    var top = parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : parts[0];
                    t.Top[top] = t.Top.GetValueOrDefault(top) + 1;
                    var fdir = string.Join('/', parts.Take(3));
                    (t.Flow.TryGetValue((day, fdir), out var cell) ? cell : t.Flow[(day, fdir)] = new FlowCell()).Add(f.Name, at);
                    var ato = new DateTimeOffset(at, TimeSpan.Zero);
                    if (t.Last is null || ato > t.Last) t.Last = ato;
                    t.Recent.Add((f.Name, rel == "." ? "" : rel, ato));
                    if (t.Recent.Count > 40) t.Recent = t.Recent.OrderByDescending(x => x.At).Take(8).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogDebug("NAS 읽기 실패 {Dir}: {Msg}", d.Path, ex.Message); }
            if (t.N30 == 0) return;
            t.Recent = t.Recent.OrderByDescending(x => x.At).Take(8).ToList();
            result.AddOrUpdate(d.Code, t, (_, prev) => Merge(prev, t));
        });
        log.LogInformation("NAS 훑기: 프로젝트 폴더 {Dirs}개, 최근 30일 흔적 {Hit}개", dirs.Count, result.Count);
        return new Dictionary<string, NasTrace>(result);
    }

    /// <summary>NAS 뿌리 아래 '분류 › DLP-xx-xxx …' 프로젝트 폴더 목록(코드 → 폴더들). 읽기만 한다.</summary>
    public static Dictionary<string, List<string>> FindProjectDirs(IEnumerable<string> roots)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var cat in SafeDirs(root))
                foreach (var pd in SafeDirs(cat))
                    if (CodeRe().Match(Path.GetFileName(pd)) is { Success: true } m)
                        (map.TryGetValue(m.Groups[1].Value, out var l) ? l : map[m.Groups[1].Value] = []).Add(pd);
        }
        return map;
    }

    private static NasTrace Merge(NasTrace a, NasTrace b)
    {
        var m = new NasTrace { N30 = a.N30 + b.N30, N14 = a.N14 + b.N14, N1 = a.N1 + b.N1, Dir = a.N30 >= b.N30 ? a.Dir : b.Dir };
        m.Last = a.Last > b.Last ? a.Last : b.Last;
        foreach (var kv in a.Top.Concat(b.Top)) m.Top[kv.Key] = m.Top.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (var kv in a.Days.Concat(b.Days)) m.Days[kv.Key] = m.Days.GetValueOrDefault(kv.Key) + kv.Value;
        m.Recent = a.Recent.Concat(b.Recent).OrderByDescending(x => x.At).Take(8).ToList();
        foreach (var (k, v) in a.Flow.Concat(b.Flow))
        {
            var cell = m.Flow.TryGetValue(k, out var c0) ? c0 : m.Flow[k] = new FlowCell();
            cell.N += v.N;
            foreach (var (name, at) in v.Picks) cell.Keep(name, at);
        }
        return m;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private sealed record Act(int N30, int N1, DateTime? Last);

    /// <summary>모듈라(이력·활동글·댓글·파일) + ES 이슈(이력·활동글) 기록 수. ES 문서는 프로젝트 코드 필드(applet 285 연동)로 붙인다.</summary>
    private static async Task<Dictionary<string, Act>> ReadActivityAsync(NpgsqlConnection db, CancellationToken ct)
    {
        const string sql = """
            with wdoc as (
              select d.id doc_id, substring(d.display->>f.cid from '(DL[A-Z]*-\d{2}-\d{3})') code
                from works.doc d join works.field f on f.applet_id = d.applet_id and f.props->>'integrationAppletId' = '285'
               where not d.is_deleted),
            ev as (
              select b.project_code code, h.at from modular.history h join modular.build b on b.id = h.build_id where not b.is_deleted
              union all select b.project_code, a.created_at from modular.activity a join modular.build b on b.id = a.build_id where not b.is_deleted
              union all select b.project_code, c.created_at from modular.comment c join modular.activity a on a.id = c.activity_id join modular.build b on b.id = a.build_id where not b.is_deleted
              union all select b.project_code, f.created_at from modular.file f join modular.build b on b.id = f.build_id where not b.is_deleted
              union all select w.code, h.at from works.history h join wdoc w on w.doc_id = h.doc_id
              union all select w.code, a.created_at from works.activity a join wdoc w on w.doc_id = a.doc_id)
            select code,
                   count(*) filter (where at > now() - interval '30 days')::int,
                   count(*) filter (where at >= date_trunc('day', now() at time zone 'Asia/Seoul') at time zone 'Asia/Seoul' - interval '1 day'
                                      and at <  date_trunc('day', now() at time zone 'Asia/Seoul') at time zone 'Asia/Seoul')::int,
                   max(at)
              from ev where code is not null and at > now() - interval '30 days' group by code
            """;
        var d = new Dictionary<string, Act>();
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetString(0)] = new Act(r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetDateTime(3));
        return d;
    }

    /// <summary>sync.project_trace를 이번 계산으로 통째로 바꾼다(한 트랜잭션).</summary>
    public static async Task<int> WriteAsync(NpgsqlConnection db, IReadOnlyDictionary<string, ErpPoRow> po, IReadOnlyDictionary<string, NasTrace> nas, CancellationToken ct)
    {
        var act = await ReadActivityAsync(db, ct);
        var codes = po.Keys.Concat(nas.Keys).Concat(act.Keys).ToHashSet();
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("delete from sync.project_trace", db, tx)) await del.ExecuteNonQueryAsync(ct);
        await using (var w = await db.BeginBinaryImportAsync(
            "copy sync.project_trace (code, po_30d, po_90d, po_total, last_po_date, nas_30d, nas_14d, nas_1d, nas_last_at, nas_dir, nas_top, nas_recent, act_30d, act_1d, act_last_at, computed_at, po_31_60d, po_61_90d, nas_days_30, nas_days_14, nas_top_share, nas_flow, po_recent) from stdin (format binary)", ct))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var c in codes)
            {
                po.TryGetValue(c, out var p);
                nas.TryGetValue(c, out var n);
                act.TryGetValue(c, out var a);
                await w.StartRowAsync(ct);
                await w.WriteAsync(c, NpgsqlDbType.Text, ct);
                await w.WriteAsync(p?.Po30 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(p?.Po90 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(p?.PoTotal ?? 0, NpgsqlDbType.Integer, ct);
                if (p?.LastPo is { } lp) await w.WriteAsync(lp, NpgsqlDbType.Date, ct); else await w.WriteNullAsync(ct);
                await w.WriteAsync(n?.N30 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(n?.N14 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(n?.N1 ?? 0, NpgsqlDbType.Integer, ct);
                if (n?.Last is { } nl) await w.WriteAsync(nl.UtcDateTime, NpgsqlDbType.TimestampTz, ct); else await w.WriteNullAsync(ct);
                if (n?.Dir is { } nd) await w.WriteAsync(nd, NpgsqlDbType.Text, ct); else await w.WriteNullAsync(ct);
                if (n is not null)
                {
                    await w.WriteAsync(JsonSerializer.Serialize(n.Top.OrderByDescending(kv => kv.Value).Take(4).Select(kv => new { dir = kv.Key, n = kv.Value })), NpgsqlDbType.Jsonb, ct);
                    await w.WriteAsync(JsonSerializer.Serialize(n.Recent.Take(6).Select(x => new { name = x.Name, dir = x.Dir, at = x.At })), NpgsqlDbType.Jsonb, ct);
                }
                else { await w.WriteNullAsync(ct); await w.WriteNullAsync(ct); }
                await w.WriteAsync(a?.N30 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(a?.N1 ?? 0, NpgsqlDbType.Integer, ct);
                if (a?.Last is { } al) await w.WriteAsync(DateTime.SpecifyKind(al, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, ct); else await w.WriteNullAsync(ct);
                await w.WriteAsync(now.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
                await w.WriteAsync(p?.Po31_60 ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(p?.Po61_90 ?? 0, NpgsqlDbType.Integer, ct);
                var today = DateOnly.FromDateTime(DateTime.Now);
                await w.WriteAsync(n?.Days.Count ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(n?.Days.Keys.Count(k => k > today.AddDays(-14)) ?? 0, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(n is { N30: > 0 } ? Math.Round((decimal)n.Days.Values.Max() / n.N30, 3) : 0m, NpgsqlDbType.Numeric, ct);
                if (n is { Flow.Count: > 0 }) await w.WriteAsync(FlowJson(n), NpgsqlDbType.Jsonb, ct); else await w.WriteNullAsync(ct);
                if (p?.RecentJson is { } pr) await w.WriteAsync(pr, NpgsqlDbType.Jsonb, ct); else await w.WriteNullAsync(ct);
            }
            await w.CompleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        return codes.Count;
    }

    /// <summary>nas_flow: 최근 30일 날짜 × 폴더 칸, 새 날짜부터 · 같은 날은 많은 폴더부터, 최대 60칸.</summary>
    private static string FlowJson(NasTrace n) => JsonSerializer.Serialize(n.Flow
        .OrderByDescending(kv => kv.Key.Day).ThenByDescending(kv => kv.Value.N).Take(60)
        .Select(kv => new { d = kv.Key.Day.ToString("yyyy-MM-dd"), dir = kv.Key.Dir, n = kv.Value.N, files = kv.Value.Files }));
}
