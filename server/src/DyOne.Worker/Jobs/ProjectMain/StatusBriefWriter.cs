using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>
/// 오늘의 프로젝트 브리핑 — 대상(누군가 ★한 프로젝트 ∪ 회사 주요 선정 ∪ 오늘 후보 상위)의 '지금 상태' 한 줄.
/// 프로그램이 사실(지금 어디·최근 무엇·다음 무엇)을 모으고, 사내 AI는 그 사실로 문장만 쓴다.
/// 문장 속 숫자가 사실에 없으면 버리고 규칙 문장으로 대신한다. 입력(facts_hash)이 안 바뀌면 다시 만들지 않는다.
/// </summary>
public static partial class StatusBriefWriter
{
    private static readonly JsonSerializerOptions Js = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<(int Made, int Ai)> RunAsync(NpgsqlConnection db, MainData d, DateOnly today, int suggestionTop, WorkerLlm? llm, ILogger log, CancellationToken ct)
    {
        // 대상 묶음
        var keys = new List<string>();
        await Collect(db, "select distinct p.code from project.favorite f join project.project p on p.id = f.project_id", keys, ct);
        await Collect(db, "select code from project.featured where status = 'selected' and ended_at is null", keys, ct);
        await using (var cmd = new NpgsqlCommand("select code from project.featured_suggestion where run_date = @d and rank <= @n", db))
        {
            cmd.Parameters.AddWithValue("d", today);
            cmd.Parameters.AddWithValue("n", suggestionTop);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
        }
        var targets = keys.Select(d.KeyOf).Distinct().ToList();
        _year = today.Year;

        int made = 0, ai = 0;
        foreach (var key in targets)
        {
            var codes = d.CodesOf(key);
            var (facts, ruleSentence, evidence, changed) = await FactsAsync(db, d, key, codes, today, ct);
            var factsJson = facts.ToJsonString(Js);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(factsJson)))[..32];
            // 사실이 같으면 다시 쓰지 않는다. 단 지난번이 규칙 문장(사내 AI가 없거나 검사 탈락)이고 지금 AI가 있으면 다시 써 본다.
            await using (var same = new NpgsqlCommand("select facts_hash, source from project.status_brief where code = @c order by made_at desc limit 1", db))
            {
                same.Parameters.AddWithValue("c", key);
                await using var sr = await same.ExecuteReaderAsync(ct);
                if (await sr.ReadAsync(ct) && sr.GetString(0) == hash && (sr.GetString(1) == "ai" || llm is null)) continue;
            }

            string sentence = ruleSentence, source = "rule";
            if (llm is not null)
            {
                try
                {
                    var j = await llm.JsonAsync(BriefPrompt, factsJson, "status-brief", ct);
                    var s = j["sentence"]?.ToString().Trim();
                    if (s is { Length: >= 10 and <= 140 } && NumbersOk(s, factsJson)) { sentence = s; source = "ai"; ai++; }
                    else log.LogInformation("브리핑 문장 검사 탈락 {Code}: {S}", key, s);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("브리핑 문장 실패 {Code}: {Msg}", key, ex.Message);
                }
            }
            await using var ins = new NpgsqlCommand("""
                insert into project.status_brief (code, made_at, facts, facts_hash, sentence, source, changed_yesterday, evidence)
                values (@c, now(), @f::jsonb, @h, @s, @src, @cy, @e::jsonb)
                """, db);
            ins.Parameters.AddWithValue("c", key);
            ins.Parameters.AddWithValue("f", factsJson);
            ins.Parameters.AddWithValue("h", hash);
            ins.Parameters.AddWithValue("s", sentence);
            ins.Parameters.AddWithValue("src", source);
            ins.Parameters.AddWithValue("cy", changed);
            ins.Parameters.AddWithValue("e", evidence.ToJsonString(Js));
            await ins.ExecuteNonQueryAsync(ct);
            made++;
        }
        return (made, ai);
    }

    private static async Task Collect(NpgsqlConnection db, string sql, List<string> into, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) into.Add(r.GetString(0));
    }

    private static int _year = DateTime.Today.Year;
    /// <summary>올해면 '11/18', 다른 해면 '2027/8/29'(연도 없이 쓰면 올해로 읽힌다).</summary>
    private static string Md(DateOnly d) => d.Year == _year ? $"{d.Month}/{d.Day}" : $"{d.Year}/{d.Month}/{d.Day}";
    private static string Md(DateTime d) => Md(DateOnly.FromDateTime(d));

    private static async Task<(JsonObject Facts, string Rule, JsonArray Evidence, int Changed)> FactsAsync(
        NpgsqlConnection db, MainData d, string key, List<string> codes, DateOnly today, CancellationToken ct)
    {
        d.Erp.TryGetValue(key, out var erp);
        var t = d.TraceOf(key);
        var mods = d.ModularOf(key);
        var texts = await MainData.ReadTextsAsync(db, codes, 14, 5, ct);
        var ids = codes.Where(d.ProjectId.ContainsKey).Select(c => d.ProjectId[c]).ToArray();
        int journals = 0, files = 0;
        string? stage = null;
        if (ids.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                select (select count(*) from project.project_journal j where j.project_id = any(@ids) and j.is_active and j.created_at > now() - interval '14 days')::int,
                       (select count(*) from project.project_file f where f.project_id = any(@ids) and f.is_active and f.uploaded_at > now() - interval '14 days')::int,
                       (select c.name from project.project_stage s join code.code c on c.id = s.stage_id where s.project_id = any(@ids) order by c.sort_order desc limit 1)
                """, db);
            cmd.Parameters.AddWithValue("ids", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { journals = r.GetInt32(0); files = r.GetInt32(1); stage = r.IsDBNull(2) ? null : r.GetString(2); }
        }

        // 지금 어디
        var live = mods.Where(m => !m.IsEnd).ToList();
        var modNow = (live.Count > 0 ? live : mods).Select(m => m.Status).Where(s => s is not null).Distinct().ToList();
        var now = new JsonObject { ["ERP 상태"] = erp?.Status };
        if (erp?.From is { } f && erp.To is { } to) now["ERP 공사 기간"] = $"{f:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";
        if (stage is not null) now["DY ONE 진행 단계"] = stage;
        if (modNow.Count > 0) now["모듈라 상태"] = string.Join(", ", modNow);
        var procs = mods.Select(m => m.Process).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(3).ToList();
        if (procs.Count > 0) now["공정"] = string.Join(" / ", procs);

        // 최근 무엇
        var recent = new JsonObject();
        if (t.Po30 > 0) { recent["최근 30일 발주"] = $"{t.Po30}건"; if (t.LastPo is { } lp) recent["마지막 발주"] = Md(lp); }
        string? nasTopDir = null;
        if (t.Nas14 > 0)
        {
            recent["NAS 최근 2주 수정 파일"] = $"{t.Nas14}개";
            if (t.NasTop is { } top && JsonNode.Parse(top) is JsonArray ta && ta.Count > 0)
            {
                nasTopDir = ta[0]?["dir"]?.ToString();
                recent["NAS 많이 바뀐 폴더"] = string.Join(", ", ta.Take(3).Select(x => x?["dir"]?.ToString()));
            }
        }
        if (t.Act30 > 0) recent["모듈라·ES 기록 30일"] = $"{t.Act30}건";
        if (journals > 0) recent["업무 일지 2주"] = $"{journals}건";
        if (files > 0) recent["올린 자료 2주"] = $"{files}건";
        var notes = new JsonArray(texts.Select(x => (JsonNode)$"{Md(x.At.ToLocalTime())} {x.Source}: {x.Text}").ToArray());

        // 다음 무엇: 오늘 이후 가장 가까운 모듈라 일정 → 없으면 ERP 공사 종료
        var upcoming = mods.SelectMany(m => new[] { (m.Install, "설치 시작"), (m.Commissioning, "시운전 시작"), (m.Ship, "출하") })
            .Where(x => x.Item1 is { } dt && dt >= today).OrderBy(x => x.Item1).Select(x => $"{Md(x.Item1!.Value)} 모듈라 {x.Item2}").FirstOrDefault();
        if (upcoming is null && erp?.To is { } et && et >= today) upcoming = $"{Md(et)} ERP 공사 종료";

        var facts = new JsonObject { ["지금"] = now, ["최근"] = recent, ["최근 글"] = notes, ["다음 일정"] = upcoming };

        // 규칙 문장(AI가 없거나 검사에 걸릴 때)
        var head = modNow.Count > 0 ? $"모듈라 {modNow[0]}" : stage is not null ? $"{stage} 단계" : $"ERP {erp?.Status ?? "상태 없음"}";
        var bits = new List<string>();
        if (t.Po30 > 0) bits.Add($"최근 30일 발주 {t.Po30}건");
        if (t.Nas14 > 0) bits.Add(nasTopDir is null ? $"NAS 2주 파일 {t.Nas14}개" : $"NAS 2주 파일 {t.Nas14}개(주로 {nasTopDir})");
        if (t.Act30 > 0) bits.Add($"모듈라·ES 기록 {t.Act30}건");
        var rule = head + " · " + (bits.Count > 0 ? string.Join(", ", bits) : "최근 30일 활동 없음") + (upcoming is null ? "" : $" · 다음 {upcoming}");

        var ev = new JsonArray();
        if (mods.Count > 0) ev.Add(new JsonObject { ["kind"] = "modular", ["label"] = $"모듈라 {string.Join(", ", modNow.DefaultIfEmpty("기록"))}" });
        if (t.Nas14 > 0 && t.NasDir is not null) ev.Add(new JsonObject { ["kind"] = "nas", ["label"] = $"NAS {nasTopDir ?? ""} {t.Nas14}개".Replace("  ", " "), ["path"] = t.NasDir });
        if (t.Po30 > 0) ev.Add(new JsonObject { ["kind"] = "po", ["label"] = $"ERP 발주 30일 {t.Po30}건" });
        if (texts.Count > 0) ev.Add(new JsonObject { ["kind"] = "post", ["label"] = $"최근 글 {texts.Count}건", ["at"] = texts[0].At.ToString("o") });
        return (facts, rule, ev, t.Act1 + t.Nas1);
    }

    [GeneratedRegex(@"\d+")] private static partial Regex NumRe();

    /// <summary>문장에 나온 숫자는 모두 사실(facts) 안에 있어야 한다.</summary>
    public static bool NumbersOk(string sentence, string factsJson)
    {
        var allowed = NumRe().Matches(factsJson).Select(m => m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0").ToHashSet();
        return NumRe().Matches(sentence).All(m => allowed.Contains(m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0"));
    }

    private const string BriefPrompt = """
        너는 두림야스카와 사내 프로젝트 포털의 '오늘의 프로젝트 브리핑'을 쓰는 비서다.
        입력 JSON의 사실만으로 이 프로젝트가 지금 어디까지 와 있는지 한국어 한 문장(50~90자)으로 쓴다.
        틀: [지금 단계·상태] + [최근 핵심 진행] + [다음 일정].
        - 사실에 없는 숫자·날짜·사람·내용을 만들지 않는다. 숫자는 입력에 있는 그대로만 쓴다.
        - 프로젝트 이름·코드는 쓰지 않는다(화면에 따로 나온다).
        - 최근 글이 있으면 거기서 가장 중요한 진행 하나를 짧게 담는다. 다음 일정이 없으면 그 부분은 뺀다.
        - 최근 활동이 없으면 '최근 30일 활동이 없습니다'처럼 사실대로 쓴다.
        - 날짜는 '다음 일정'·'마지막 발주'에 적힌 모양 그대로 쓴다(올해는 '12/31', 다른 해는 '2027/8/29').
        - '~입니다' 또는 '~습니다'로 끝낸다.
        JSON으로만 답한다: {"sentence":"…"}
        """;
}
