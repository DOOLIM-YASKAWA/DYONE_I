using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>
/// 일정 주의(11-04) — 사람이 올리거나 저장한 사양정리·수행계획서·사양서(NAS 2·3칸)에서 일정을 읽고, 지금 상태와 비교해 꼬리표를 단다.
///  ① 파이썬 도우미(python/schedule_lines.py)가 '단계 낱말 + 날짜'가 함께 있는 줄만 뽑는다(읽기 전용).
///  ② 사내 AI가 그 줄을 {단계, 날짜}로 정리한다. 줄 안에 없는 월·연도가 나오면 버린다. 줄이 어제와 같으면 다시 부르지 않는다.
///  ②' 그림 일정표(11-04): 글자로 날짜가 거의 없는 '일정' 쪽은 도우미가 그림으로 만들고, 사내 AI가 그림을 읽는다. 그림에서 본 글자로 날짜를 검사·보정한다.
///  ③ 단계별 기준 일정 = 사양정리 › 수행계획서 › 사양서, 같은 종류면 새 파일. ERP 공사 기간은 예상값이라 판정에 쓰지 않는다(갱신 필요 표시만).
///  ④ 판정: 지연 의심(late) · 일정 임박(soon, 30일 안) · 양산 후 작업 중(after_sop) · ERP 기간 갱신 필요(erp, 화면에 안 띄움).
/// DY ONE이 자동으로 만드는 사양정리 초안은 쓰지 않는다(11-04 사용자). 근거: docs/roadmap/회사 주요 프로젝트 선정 기준 v3 10절.
/// </summary>
public static partial class ScheduleWriter
{
    private static readonly JsonSerializerOptions Js = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly IReadOnlyDictionary<string, string> StageName = new Dictionary<string, string>
    {
        ["olt"] = "OLT", ["ship"] = "선적", ["install"] = "현장 설치", ["commission"] = "시운전", ["pre"] = "시생산", ["sop"] = "양산",
    };
    private static readonly string[] Stages = ["olt", "ship", "install", "commission", "pre", "sop"];

    private sealed record Line(string File, string Path, string Kind, double Mtime, string Loc, string Text, string? Png = null);
    private sealed record Milestone(string Stage, DateOnly From, DateOnly To, Line Src);

    public static async Task<(int Read, int Alerts)> RunAsync(NpgsqlConnection db, MainData d, DateOnly today, IReadOnlyList<string> nasRoots,
        string python, WorkerLlm? llm, ILogger log, CancellationToken ct)
    {
        // 대상: ★ ∪ 회사 주요 선정 ∪ 오늘 후보 30건(브리핑 대상과 같음)
        var keys = new HashSet<string>();
        await Collect(db, "select distinct p.code from project.favorite f join project.project p on p.id = f.project_id", keys, ct);
        await Collect(db, "select code from project.featured where status = 'selected' and ended_at is null", keys, ct);
        await Collect(db, "select code from project.featured_suggestion where run_date = (select max(run_date) from project.featured_suggestion)", keys, ct);
        var targets = keys.Select(d.KeyOf).Distinct().ToList();

        // ① 일정 줄 뽑기(파이썬, 읽기 전용)
        var dirs = TraceCollector.FindProjectDirs(nasRoots);
        // 해외/국내(11-05): 선적은 해외만. NAS 해외 폴더 · ERP 고객사 영문 표기
        var overseas = targets.ToDictionary(k => k, k => d.IsOverseas(k, d.CodesOf(k).SelectMany(c => dirs.GetValueOrDefault(c) ?? [])));
        var input = new Dictionary<string, string>();
        foreach (var key in targets)
            foreach (var c in d.CodesOf(key))
                if (dirs.TryGetValue(c, out var ds))
                    for (var i = 0; i < ds.Count; i++) input[ds.Count == 1 && c == key ? key : $"{key}|{c}|{i}"] = ds[i];
        var read = 0;
        var tmp = Path.Combine(Path.GetTempPath(), $"dyone-sched-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
        var lines = input.Count == 0 ? [] : await ReadLinesAsync(python, input, tmp, log, ct);
        var byKey = lines.GroupBy(kv => kv.Key.Split('|')[0]).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).Where(l => l.Kind != "error").ToList());

        // ② 날짜로 정리(바뀐 프로젝트만 사내 AI)
        foreach (var (key, ls) in byKey)
        {
            var picked = ls.Where(l => l.Png is null).OrderBy(l => KindRank(l.Kind)).ThenByDescending(l => l.Mtime).Take(40).ToList();
            var images = ls.Where(l => l.Png is not null).ToList();
            var abroad = overseas.GetValueOrDefault(key);
            // 입력 줄 + 지시문 + 해외/국내가 같으면 다시 읽지 않는다(지시문을 고치면 다음 실행에서 한 번 전부 다시 읽음)
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{RuleVersion}|{abroad}\n" + string.Join("\n", picked.Concat(images).Select(l => $"{l.Path}|{l.Mtime}|{l.Loc}|{l.Text}")))))[..32];
            if (await StateAsync(db, key, ct) == hash) continue;
            if (llm is null || picked.Count + images.Count == 0) continue;
            List<Milestone> ms;
            try
            {
                ms = picked.Count > 0 ? await ExtractAsync(llm, picked, today, abroad, ct) : [];
                foreach (var img in images)
                {
                    try { ms.AddRange(await ExtractImageAsync(llm, img, abroad, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("그림 일정 읽기 실패 {Code} {File}: {Msg}", key, img.File, ex.Message); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("일정 읽기 실패 {Code}: {Msg}", key, ex.Message); continue; }
            await SaveMilestonesAsync(db, key, ms, hash, ct);
            read++;
        }
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* 임시 폴더 정리 실패는 무시 */ } }

        // ③④ 판정 → project.schedule_alert
        var alerts = 0;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            // 판정은 계산할 때마다 통째로 바꾼다(같은 날 다시 돌려도 지난 판정이 남지 않게). 처음 나온 날(유예 계산)만 이어받는다.
            var firstSeen = new Dictionary<(string, string, string), DateOnly>();
            await using (var old = new NpgsqlCommand("select code, kind, stage, first_seen from project.schedule_alert", db, tx))
            await using (var r = await old.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) firstSeen[(r.GetString(0), r.GetString(1), r.GetString(2))] = DateOnly.FromDateTime(r.GetDateTime(3));
            await using (var clear = new NpgsqlCommand("delete from project.schedule_alert", db, tx)) await clear.ExecuteNonQueryAsync(ct);
            foreach (var key in targets)
            {
                var ms = await LoadMilestonesAsync(db, key, tx, ct);
                foreach (var a in Judge(d, key, ms, today, overseas.GetValueOrDefault(key)))
                {
                    await using var up = new NpgsqlCommand("""
                        insert into project.schedule_alert (code, kind, stage, due_date, days, reason, evidence, first_seen, last_seen)
                        values (@c, @k, @s, @due, @days, @r, @e::jsonb, @f, @t)
                        on conflict (code, kind, stage) do nothing
                        """, db, tx);
                    up.Parameters.AddWithValue("f", firstSeen.GetValueOrDefault((key, a.Kind, a.Stage), today));
                    up.Parameters.AddWithValue("c", key);
                    up.Parameters.AddWithValue("k", a.Kind);
                    up.Parameters.AddWithValue("s", a.Stage);
                    up.Parameters.AddWithValue("due", a.Due);
                    up.Parameters.AddWithValue("days", today.DayNumber - a.Due.DayNumber);
                    up.Parameters.AddWithValue("r", a.Reason);
                    up.Parameters.AddWithValue("e", a.Evidence.ToJsonString(Js));
                    up.Parameters.AddWithValue("t", today);
                    await up.ExecuteNonQueryAsync(ct);
                    alerts++;
                }
            }
            await tx.CommitAsync(ct);
        }
        return (read, alerts);
    }

    private static int KindRank(string k) => k switch { "summary" => 0, "plan" => 1, _ => 2 };

    private static async Task Collect(NpgsqlConnection db, string sql, HashSet<string> into, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) into.Add(r.GetString(0));
    }

    private static async Task<Dictionary<string, List<Line>>> ReadLinesAsync(string python, Dictionary<string, string> input, string tmp, ILogger log, CancellationToken ct)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "python", "schedule_lines.py");
        var inFile = Path.Combine(tmp, "in.json");
        var outFile = Path.Combine(tmp, "out.json");
        try
        {
            await File.WriteAllTextAsync(inFile, JsonSerializer.Serialize(input, Js), ct);
            var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(inFile);
            psi.ArgumentList.Add(outFile);
            psi.ArgumentList.Add(Path.Combine(tmp, "img"));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("파이썬을 시작하지 못했습니다.");
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(25));
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0) { log.LogWarning("일정 줄 뽑기 실패({Code}): {Err}", p.ExitCode, (await stderr).Trim()[..Math.Min(300, (await stderr).Trim().Length)]); return []; }
            log.LogInformation("일정 줄 뽑기: {Out}", (await stdout).Trim());
            var j = JsonNode.Parse(await File.ReadAllTextAsync(outFile, ct))!.AsObject();
            return j.ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray ?? []).Select(x => new Line(
                x!["file"]!.ToString(), x["path"]?.ToString() ?? "", x["kind"]!.ToString(), x["mtime"]?.GetValue<double>() ?? 0, x["loc"]?.ToString() ?? "", x["line"]!.ToString(),
                x["png"]?.ToString())).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("일정 줄 뽑기 결과를 읽지 못했습니다: {Msg}", ex.Message); return []; }
    }

    private static async Task<string?> StateAsync(NpgsqlConnection db, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select watermark from sync.state where source = 'schedule' and entity = @k", db);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>지시문·단계 규칙의 판. 바꾸면 다음 실행에서 모든 프로젝트를 한 번 다시 읽는다(워터마크에 들어감).</summary>
    private const string RuleVersion = "2026-11-05c";

    // 단계 규칙(11-05, 사양서 원문 대조): 로봇 입고·납기(안 씀) → 모듈라·OLT(두림 시운전 포함) → 선적(해외만) → 현장 설치('철거&설치' 포함)
    //  → 현장 시운전 ↔ 품확·시생산(겹치거나 순서가 바뀔 수 있음) → 양산. 근거: docs/roadmap/일정 단계 규칙 수정 결과 (2026-11-05).md
    private const string Prompt = """
        너는 설비 프로젝트 문서에서 일정만 정리하는 도우미다. 입력 첫 줄에 프로젝트가 해외인지 국내인지 적혀 있고, 그 아래는 번호가 붙은 문서 줄이다(사양정리·수행계획서·사양서).
        각 줄에서 아래 단계의 날짜를 찾아 JSON으로만 답한다: {"items":[{"i":번호,"stage":"단계","from":"YYYY-MM-DD","to":"YYYY-MM-DD"}]}
        단계(진행 순서):
          olt = 모듈라 빌드·제작·조립, OLT, 두림(공장) 시운전·I/O Check. 현장 설치보다 앞선 '시운전'은 olt다.
          ship = 선적·출하·Shipping·Transport. 해외 프로젝트만 쓴다. 국내 프로젝트는 ship을 절대 쓰지 않는다.
          install = 현장 설치·Installation. '철거&설치'·'철거/설치'(고객 현장 기존 설비 철거 후 신규 설치)도 install이다.
          commission = 현장 시운전·커미셔닝(현장 설치 뒤).
          pre = 시생산·품확·품질확보·Pre-production·선행양산.
          sop = SOP·양산·Mass production.
        - '납기'·'입고'·'반입'(로봇 납기, 두림 입고, 공사 납기)의 날짜와 로봇·컨트롤러·부품 발주표 행(발주일~납기일)은 어느 단계도 아니다. 넣지 않는다.
          단, '납기'가 있는 줄이라도 설치·시운전·품확·양산 등 다른 단계 낱말 옆의 날짜는 빠뜨리지 말고 넣는다('납기(SOP)'는 표의 칸 이름일 뿐이다).
          예: "납기(SOP) : 26년 8월 (양산 : 27.1.3)" → sop 2027-01-03만. "로봇 납기 7/27, 7/28부터 모듈라 조립" → olt 7/28만.
          "납기 : ~27.08.30 … ~27.08.30 설치 및 단동/연동 시운전" → install·commission 2027-08-30(설치·시운전 낱말 옆 날짜이므로 넣는다).
        - 대금 지급 조건(예: "설치 후 검수 완료 후 40%")의 날짜는 일정이 아니다. 넣지 않는다.
        - '설치 및 시운전'·'설치&시운전'·'Install & Commission'처럼 설치와 붙어 있는 시운전은 현장 시운전이다 → install과 commission 둘 다 넣는다.
          시운전을 olt로 넣는 것은 '두림'·'공장'·'OLT'·'모듈라'·'I/O Check'와 함께 있거나, 같은 문서에서 현장 설치보다 앞선 날짜로 적혀 있을 때만이다.
        - 이설 공사에서 OLT 앞에 오는 '철거'(예: "4공장 철거/오버홀/OLT")는 olt로 본다. 같은 줄 뒤에 이어지는 '설치'(예: "외판 설치 ('24년 10월)")는 그대로 install로 넣는다.
        - 시운전과 품확·시생산은 겹치거나 순서가 바뀔 수 있다. 한 줄에 둘 다 있으면(예: "시운전+품질확보", "시운전/선행양산") 같은 날짜로 둘 다 넣는다.
        - 줄에 적힌 날짜만 쓴다. 지어내지 않는다. 날짜가 없거나 단계가 애매하면 그 줄은 넣지 않는다.
        - 연도가 없으면 '문서 연도'를 기준으로, 그 날짜가 문서보다 6개월 넘게 앞서면 다음 해로 본다. "'27", "27." 같은 두 자리 연도는 2027이다.
        - "9월 말"은 그 달 마지막 날, "9월 초"는 1일, "9월 중순"은 15일, 달만 있으면 from=1일·to=마지막 날.
        - "~ 2026.03.31"처럼 기간이면 from과 to를 모두 쓴다. 하루면 from과 to를 같게.
        - 같은 단계가 여러 줄에 있으면 모두 넣는다.
        """;

    [GeneratedRegex(@"\d+")] private static partial Regex NumRe();

    /// <summary>선적이 아닌데 선적으로 나오기 쉬운 줄: '납기'·'입고'·'반입'만 있고 선적·출하 낱말은 없는 줄(로봇 납기·두림 입고·발주표 행·공사 납기)</summary>
    [GeneratedRegex(@"납기|입고|반입")] private static partial Regex DeliveryRe();
    [GeneratedRegex(@"선적|출하|ship|transport|loading|船", RegexOptions.IgnoreCase)] private static partial Regex ShipWordRe();

    /// <summary>AI 답을 단계 규칙으로 한 번 더 거른다: 국내는 선적 없음, 납기·입고 줄은 선적 아님.</summary>
    private static bool StageOk(string stage, string text, bool abroad) =>
        stage != "ship" || (abroad && !(DeliveryRe().IsMatch(text) && !ShipWordRe().IsMatch(text)));

    private static async Task<List<Milestone>> ExtractAsync(WorkerLlm llm, List<Line> lines, DateOnly today, bool abroad, CancellationToken ct)
    {
        var sb = new StringBuilder($"프로젝트: {(abroad ? "해외(선적 단계 있음)" : "국내(선적 단계 없음 — ship을 쓰지 않는다)")}\n오늘: {today:yyyy-MM-dd}\n");
        for (var i = 0; i < lines.Count; i++)
        {
            var year = DateTimeOffset.FromUnixTimeSeconds((long)lines[i].Mtime).Year;
            sb.Append($"{i + 1}. [{lines[i].File} · 문서 연도 {year}] {lines[i].Text}\n");
        }
        var j = await llm.JsonAsync(Prompt, sb.ToString(), "schedule-read", ct, maxTokens: 8000);
        var list = new List<Milestone>();
        foreach (var it in j["items"] as JsonArray ?? [])
        {
            if (it?["i"]?.GetValue<int>() is not { } n || n < 1 || n > lines.Count) continue;
            var stage = it["stage"]?.ToString() ?? "";
            if (!Stages.Contains(stage)) continue;
            if (!DateOnly.TryParseExact(it["from"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) continue;
            var to = DateOnly.TryParseExact(it["to"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2) && t2 >= from ? t2 : from;
            var line = lines[n - 1];
            if (!StageOk(stage, line.Text, abroad)) continue;
            // 줄에 연도가 없으면 연도는 프로그램이 정한다: 문서 날짜보다 6개월 넘게 앞서면 다음 해(AI가 문서 연도를 그대로 붙이는 경우가 있음)
            if (YearsIn(line.Text).Count == 0)
            {
                var docDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds((long)line.Mtime).DateTime);
                from = FixYear(from, docDay);
                to = FixYear(to, docDay);
                if (to < from) to = from;
            }
            if (!DateOk(line.Text, from) || !DateOk(line.Text, to)) continue; // 줄 안에 없는 월·연도 → 버림
            list.Add(new Milestone(stage, from, to, line));
        }
        return list;
    }


    /// <summary>연도 없는 날짜: 문서 날짜 기준 −6개월 ~ +18개월 안으로 맞춘다</summary>
    private static DateOnly FixYear(DateOnly d, DateOnly doc)
    {
        var x = new DateOnly(doc.Year, d.Month, Math.Min(d.Day, DateTime.DaysInMonth(doc.Year, d.Month)));
        if (x < doc.AddMonths(-6)) x = x.AddYears(1);
        return x;
    }

    [GeneratedRegex(@"(?<![\d])(20\d{2})(?![\d])|[’‘'`]\s?(\d{2})(?![\d])|(?<![\d])(\d{2})\s*년|(?<![\d./])(\d{2})\s*\.\s*\d{1,2}\s*[./월]")] private static partial Regex YearTokRe();

    private const string ImagePrompt = """
        설비 프로젝트 사양서 안의 일정표 그림이다(간트 차트·타임라인). 그림에 보이는 단계별 일정을 JSON으로만 답한다.
        {"items":[{"stage":"olt|ship|install|commission|pre|sop","from":"YYYY-MM-DD","to":"YYYY-MM-DD","seen":"그림에서 본 글자 그대로"}]}
        단계: olt(OLT·Modular Build Test·제작/OLT·두림(공장) 시운전 — 현장 설치보다 앞선 시운전), ship(Transport·선적·출하 — 해외 프로젝트만),
              install(Installation·설치·철거&설치), commission(현장 시운전·Teaching/Spray Test·커미셔닝),
              pre(Pre-production·시생산·품확·시험생산), sop(SOP·Mass production·양산, 양산 시작 표시 M·화살표).
        - 로봇 입고·납기 막대는 어느 단계도 아니다. 넣지 않는다.
        - 막대는 머리줄의 연·월 칸으로 시작·끝 달을 읽는다. 막대 옆이나 아래에 날짜 글자(예: 3/15, '27.6/30, 2026. 06. 01)가 있으면 그 날짜를 쓴다.
        - seen에는 그 단계 이름과 근처에 적힌 날짜 글자를 그대로 적는다(예: "설치(6), 3/15, 9/15"). 머리줄 연도도 함께 적는다(예: "'26").
        - 보이지 않는 단계는 넣지 않는다. 지어내지 않는다.
        """;

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*([./])\s*(\d{1,2})(?!\d)")] private static partial Regex MdRe();

    /// <summary>그림 일정표 한 장 → 일정. 그림에서 본 글자(seen)에 월/일이 있으면 그 날로 맞추고, 월이 seen에 없으면(막대 위치로만 짐작) 버린다.</summary>
    private static async Task<List<Milestone>> ExtractImageAsync(WorkerLlm llm, Line img, bool abroad, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(img.Png!, ct);
        var mime = img.Png!.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg"; // 도우미가 JPEG로 줄여 준다
        // 그림은 지시문에 해외/국내를 덧붙인다(국내는 선적 막대가 있어도 넣지 않음 — 아래 StageOk로도 거름)
        var prompt = ImagePrompt + (abroad ? "\n- 이 프로젝트는 해외다." : "\n- 이 프로젝트는 국내다. ship은 넣지 않는다.");
        var j = await llm.VisionJsonAsync(prompt, bytes, mime, "schedule-image", ct, 2500);
        var list = new List<Milestone>();
        foreach (var it in j["items"] as JsonArray ?? [])
        {
            var stage = it?["stage"]?.ToString() ?? "";
            var seen = it?["seen"]?.ToString().Trim() ?? "";
            if (!Stages.Contains(stage) || seen.Length == 0) continue;
            if (!StageOk(stage, seen, abroad)) continue;
            if (!DateOnly.TryParseExact(it!["from"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)) continue;
            var to = DateOnly.TryParseExact(it["to"]?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2) && t2 >= from ? t2 : from;
            // 그림에 적힌 월/일로 맞춘다(막대 끝을 달 단위로 읽어 보름쯤 어긋나는 것 보정)
            // '.'로 나뉜 값은 일이 두 자리일 때만 날짜(06. 01 · '26.8.10). "4.5"·"6.5" 같은 기간(개월)은 날짜가 아니다
            var md = MdRe().Matches(seen)
                .Where(m => m.Groups[2].Value == "/" || m.Groups[3].Value.Length == 2)
                .Select(m => (M: int.Parse(m.Groups[1].Value), D: int.Parse(m.Groups[3].Value)))
                .Where(x => x.M is >= 1 and <= 12 && x.D is >= 1 and <= 31).ToList();
            // 그림에 적힌 월/일: 둘 이상이면 앞 = 시작, 뒤 = 끝(끝 달이 앞서면 다음 해). 하나면 가까운 쪽 끝만 맞춘다.
            DateOnly At(int y, (int M, int D) x) => new(y, x.M, Math.Min(x.D, DateTime.DaysInMonth(y, x.M)));
            int YearNear(DateOnly d, int m) => m - d.Month > 6 ? d.Year - 1 : d.Month - m > 6 ? d.Year + 1 : d.Year;
            if (md.Count >= 2)
            {
                var f = md[0];
                var l = md[^1];
                from = At(YearNear(from, f.M), f);
                var ty = l.M < f.M || (l.M == f.M && l.D < f.D) ? from.Year + 1 : from.Year;
                to = At(ty, l);
            }
            else if (md.Count == 1)
            {
                var x = md[0];
                if (Math.Abs(x.M - to.Month) < Math.Abs(x.M - from.Month)) to = At(YearNear(to, x.M), x);
                else from = At(YearNear(from, x.M), x);
            }
            if (to < from) to = from;
            if (!DateOk(seen, from) && !DateOk(seen, to)) continue; // 그림에서 본 글자에 그 월이 없으면(막대로만 짐작) 버림
            list.Add(new Milestone(stage, from, to, img with { Text = "[그림] " + seen, Loc = img.Loc + " 그림" }));
        }
        return list;
    }

    /// <summary>줄에 적힌 연도들(2026 · ’27 · 27년 · 26.10/ 모양) → 4자리</summary>
    private static HashSet<int> YearsIn(string text)
    {
        var set = new HashSet<int>();
        foreach (Match m in YearTokRe().Matches(text))
            for (var g = 1; g <= 4; g++)
                if (m.Groups[g].Success && int.TryParse(m.Groups[g].Value, out var y)) set.Add(y < 100 ? 2000 + y : y);
        return set;
    }

    /// <summary>날짜의 월은 줄 안에 숫자로 있어야 하고, 줄에 연도가 적혀 있으면 그중 하나여야 한다(AI가 '27을 2026으로 읽는 일 방지).</summary>
    private static bool DateOk(string text, DateOnly d)
    {
        var nums = NumRe().Matches(text).Select(m => m.Value).ToList();
        if (!nums.Any(x => int.TryParse(x, out var v) && v == d.Month)) return false;
        var years = YearsIn(text);
        return years.Count == 0 || years.Contains(d.Year);
    }

    private static async Task SaveMilestonesAsync(NpgsqlConnection db, string key, List<Milestone> ms, string hash, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("delete from project.schedule_milestone where code = @c", db, tx))
        {
            del.Parameters.AddWithValue("c", key);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var m in ms)
        {
            await using var ins = new NpgsqlCommand("""
                insert into project.schedule_milestone (code, stage, date_from, date_to, raw_text, doc_name, doc_path, doc_kind, doc_mtime, page)
                values (@c, @s, @f, @t, @raw, @dn, @dp, @dk, @dm, @pg)
                """, db, tx);
            ins.Parameters.AddWithValue("c", key);
            ins.Parameters.AddWithValue("s", m.Stage);
            ins.Parameters.AddWithValue("f", m.From);
            ins.Parameters.AddWithValue("t", m.To);
            ins.Parameters.AddWithValue("raw", m.Src.Text);
            ins.Parameters.AddWithValue("dn", m.Src.File);
            ins.Parameters.AddWithValue("dp", m.Src.Path);
            ins.Parameters.AddWithValue("dk", m.Src.Kind);
            ins.Parameters.AddWithValue("dm", DateTimeOffset.FromUnixTimeSeconds((long)m.Src.Mtime).UtcDateTime);
            ins.Parameters.AddWithValue("pg", m.Src.Loc);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var st = new NpgsqlCommand("""
            insert into sync.state (source, entity, watermark, updated_at) values ('schedule', @k, @h, now())
            on conflict (source, entity) do update set watermark = excluded.watermark, updated_at = now()
            """, db, tx))
        {
            st.Parameters.AddWithValue("k", key);
            st.Parameters.AddWithValue("h", hash);
            await st.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private sealed record Stored(string Stage, DateOnly To, string Raw, string Doc, string Kind, DateTime? Mtime, string? Page);

    private static async Task<List<Stored>> LoadMilestonesAsync(NpgsqlConnection db, string key, NpgsqlTransaction tx, CancellationToken ct)
    {
        var list = new List<Stored>();
        await using var cmd = new NpgsqlCommand("select stage, coalesce(date_to, date_from), raw_text, doc_name, doc_kind, doc_mtime, page from project.schedule_milestone where code = @c", db, tx);
        cmd.Parameters.AddWithValue("c", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Stored(r.GetString(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    private sealed record Alert(string Kind, string Stage, DateOnly Due, string Reason, JsonObject Evidence);

    private static string Md(DateOnly d, DateOnly today) => d.Year == today.Year ? $"{d.Month}/{d.Day}" : $"{d.Year}/{d.Month}/{d.Day}";

    /// <summary>모듈라 진행 순번: 1 준비중 · 2 입고제작 · 3 설치 · 4 시운전 · 5 외부검수 · 6 철거·팩킹 · 7 지연 · 8 잔여정리 · 9 문서정리·완료. 홀딩·없음 = null</summary>
    private static (int? No, string? Name) ModularNow(List<ModularInfo> mods)
    {
        (int No, string Name)? best = null;
        foreach (var m in mods)
        {
            var st = Regex.Replace(m.Status ?? "", @"\s+", " ").Trim();
            int? no = st.Length > 0 && char.IsDigit(st[0]) ? st[0] - '0' : st.StartsWith("잔여") ? 8 : null;
            if (no is null) continue;
            if (best is null || (no < 9 && (best.Value.No >= 9 || no > best.Value.No))) best = (no.Value, st);
        }
        return best is { } b ? (b.No, b.Name) : (null, null);
    }

    private static List<Alert> Judge(MainData d, string key, List<Stored> ms, DateOnly today, bool abroad)
    {
        var res = new List<Alert>();
        if (!abroad) ms = ms.Where(m => m.Stage != "ship").ToList(); // 선적은 해외만(11-05). 예전에 저장된 국내 선적 줄이 남아 있어도 판정에 안 씀
        if (ms.Count == 0) return res;
        // 단계별 기준 일정: 연도가 적힌 글자 줄 › 그림 일정표 › 연도를 추정한 글자 줄, 그다음 사양정리 › 수행계획서 › 사양서, 같은 종류면 새 파일
        var basis = ms.GroupBy(m => m.Stage).ToDictionary(g => g.Key, g => g
            .OrderBy(m => m.Raw.StartsWith("[그림]") ? 1 : YearsIn(m.Raw).Count > 0 ? 0 : 2).ThenBy(m => KindRank(m.Kind)).ThenByDescending(m => m.Mtime).First());
        var (modNo, modName) = ModularNow(d.ModularOf(key));
        var t = d.TraceOf(key);
        JsonObject Ev(Stored b) => new()
        {
            ["doc"] = b.Doc, ["kind"] = b.Kind, ["page"] = b.Page, ["line"] = b.Raw, ["modular"] = modName,
            ["po30"] = t.Po30, ["nasDays14"] = t.NasDays14,
        };

        // 지연 의심: 양산 › 선적 › OLT 순으로 하나만(모듈라 정보가 있을 때)
        if (modNo is { } no and < 9)
        {
            foreach (var (stage, needNo) in new[] { ("sop", 6), ("ship", 6), ("olt", 4) })
            {
                if (!basis.TryGetValue(stage, out var b) || today.DayNumber - b.To.DayNumber <= 7 || no >= needNo) continue;
                res.Add(new Alert("late", stage, b.To, $"{StageName[stage]} {Md(b.To, today)} 지남 · 모듈라 '{modName}'", Ev(b)));
                break;
            }
        }
        // 일정 임박: 30일 안에 오는 가장 가까운 일정
        var soon = basis.Values.Where(b => b.To >= today && b.To.DayNumber - today.DayNumber <= 30).OrderBy(b => b.To).FirstOrDefault();
        if (soon is not null) res.Add(new Alert("soon", soon.Stage, soon.To, $"{StageName[soon.Stage]} {Md(soon.To, today)} 예정", Ev(soon)));
        // 양산 후 작업 중: 양산이 30일 넘게 지났는데 발주·도면 작업·모듈라가 계속
        if (basis.TryGetValue("sop", out var sop) && today.DayNumber - sop.To.DayNumber > 30 && !res.Any(a => a.Kind == "late")
            && ((t.Po30 > 0 && t.NasDays14 >= 2) || modNo is >= 2 and <= 5)) // 발주만 이어지는 건(보증·잔여 발주) 빼고, 도면·모듈라 작업도 계속될 때
            res.Add(new Alert("after_sop", "sop", sop.To, $"양산 {Md(sop.To, today)} 지남 · {(modNo is >= 2 and <= 5 ? $"모듈라 '{modName}'" : "발주·도면 작업")} 이어짐", Ev(sop)));
        // ERP 기간 갱신 필요(화면에 안 띄움 — 영업 확인 목록용)
        if (basis.TryGetValue("sop", out var s2) && d.Erp.TryGetValue(key, out var e) && e.To is { } erpTo && s2.To.DayNumber - erpTo.DayNumber > 30)
            res.Add(new Alert("erp", "sop", s2.To, $"ERP 공사 종료 {Md(erpTo, today)} < 문서 양산 {Md(s2.To, today)}", Ev(s2)));
        return res;
    }
}
