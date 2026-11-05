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
            var (facts, ruleSentence, evidence, changed, nasFlow, poFlow, detail) = await FactsAsync(db, d, key, codes, today, ct);
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
            JsonNode? flowAi = null;
            if (llm is not null)
            {
                try
                {
                    var j = await llm.JsonAsync(BriefPrompt, factsJson, "status-brief", ct);
                    var s = j["sentence"]?.ToString().Trim();
                    if (s is { Length: >= 10 and <= 160 } && NumbersOk(s, factsJson)) { sentence = s; source = "ai"; ai++; }
                    else log.LogInformation("브리핑 문장 검사 탈락 {Code}: {S}", key, s);
                    flowAi = j;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("브리핑 문장 실패 {Code}: {Msg}", key, ex.Message);
                }
            }
            // '상세히' 창 자료: 흐름 제목·추세는 AI 것이 검사를 통과하면 쓰고, 아니면 규칙 것
            detail["nas"] = FlowNode(nasFlow, flowAi?["nasTrend"], flowAi?["nasTitles"], factsJson);
            detail["po"] = FlowNode(poFlow, flowAi?["poTrend"], flowAi?["poTitles"], factsJson);
            evidence.Add(detail);
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

    private static async Task<(JsonObject Facts, string Rule, JsonArray Evidence, int Changed, BriefFlow.Flow? Nas, BriefFlow.Flow? Po, JsonObject Detail)> FactsAsync(
        NpgsqlConnection db, MainData d, string key, List<string> codes, DateOnly today, CancellationToken ct)
    {
        d.Erp.TryGetValue(key, out var erp);
        var t = d.TraceOf(key);
        var mods = d.ModularOf(key);
        var texts = await MainData.ReadTextsAsync(db, codes, 14, 5, ct);
        var ids = codes.Where(d.ProjectId.ContainsKey).Select(c => d.ProjectId[c]).ToArray();
        int journals = 0, files = 0;
        string? stage = null;
        var otherRun = new List<string>();
        DateTime? stageAt = null;
        var history = new List<(DateTime At, string Text)>();
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
            await r.CloseAsync();
            // DY ONE에서 사람이 바꾼 단계: 함께 진행 중인 다른 단계, 마지막으로 바꾼 때, 최근 30일 변경 이력
            await using var st = new NpgsqlCommand("""
                select c.name from project.project_stage_status s join code.code c on c.id = s.stage_id
                 where s.project_id = any(@ids) and s.status = 'run' order by c.sort_order
                """, db);
            st.Parameters.AddWithValue("ids", ids);
            await using (var sr = await st.ExecuteReaderAsync(ct)) while (await sr.ReadAsync(ct)) otherRun.Add(sr.GetString(0));
            await using var hs = new NpgsqlCommand("""
                select h.changed_at, c.name, h.to_status from project.project_stage_history h left join code.code c on c.id = h.to_stage_id
                 where h.project_id = any(@ids) and h.source = 'dyone' order by h.changed_at desc limit 20
                """, db);
            hs.Parameters.AddWithValue("ids", ids);
            await using (var hr = await hs.ExecuteReaderAsync(ct))
                while (await hr.ReadAsync(ct))
                {
                    var at = hr.GetDateTime(0);
                    stageAt ??= at;
                    if (at < DateTime.UtcNow.AddDays(-30) || hr.IsDBNull(1)) continue;
                    var toSt = hr.IsDBNull(2) ? null : hr.GetString(2);
                    history.Add((at, $"DY ONE 단계 {hr.GetString(1)} {(toSt switch { "run" => "진행", "done" => "완료", "wait" => "대기로 되돌림", _ => "변경" })}"));
                }
        }
        otherRun.Remove(stage ?? "");
        // NAS 흐름 · 발주 흐름(11-05 흔적 칸)
        var nasCells = new List<JsonNode?>();
        var poRows = new List<JsonNode?>();
        await using (var fl = new NpgsqlCommand("select nas_flow::text, po_recent::text from sync.project_trace where code = any(@c)", db))
        {
            fl.Parameters.AddWithValue("c", codes.ToArray());
            await using var fr = await fl.ExecuteReaderAsync(ct);
            while (await fr.ReadAsync(ct))
            {
                if (!fr.IsDBNull(0) && JsonNode.Parse(fr.GetString(0)) is JsonArray na) nasCells.AddRange(na.Select(x => x?.DeepClone()));
                if (!fr.IsDBNull(1) && JsonNode.Parse(fr.GetString(1)) is JsonArray pa) poRows.AddRange(pa.Select(x => x?.DeepClone()));
            }
        }
        var nasFlow = BriefFlow.Nas(nasCells, today);
        var poFlow = t.Po90 > 0 ? BriefFlow.Po(poRows, t) : null;

        // 지금 어디
        var live = mods.Where(m => !m.IsEnd).ToList();
        var modNow = (live.Count > 0 ? live : mods).Select(m => BriefFlow.StripNo(m.Status)).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        // 단계는 하나만: DY ONE에서 사람이 정한 단계(설계·검토~라인 대응·종료)가 있으면 그것, 없으면(영업·수주) ERP 상태 — 11-05 사용자 결정
        var now = new JsonObject();
        if (stage is not null) now["예상 프로젝트 단계"] = stage;
        else now["ERP 상태"] = erp?.Status;
        if (otherRun.Count > 0) now["함께 진행 중인 단계"] = string.Join(", ", otherRun);
        if (erp?.From is { } f && erp.To is { } to) now["ERP 공사 기간"] = $"{f:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";
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

        // 다음 무엇: 오늘 이후 가장 가까운 모듈라 일정 → 없으면 ERP 공사 종료
        var upcoming = mods.SelectMany(m => new[] { (m.Install, "설치 시작"), (m.Commissioning, "시운전 시작"), (m.Ship, "출하") })
            .Where(x => x.Item1 is { } dt && dt >= today).OrderBy(x => x.Item1).Select(x => $"{Md(x.Item1!.Value)} 모듈라 {x.Item2}").FirstOrDefault();
        if (upcoming is null && erp?.To is { } et && et >= today) upcoming = $"{Md(et)} ERP 공사 종료";

        // 최근 바뀐 이력: DY ONE 단계 변경 + 모듈라·ES 글(새 것부터)
        var changes = history.Select(h => (h.At, Text: $"{Md(h.At.ToLocalTime())} {h.Text}"))
            .Concat(texts.Select(x => (At: x.At, Text: $"{Md(x.At.ToLocalTime())} {x.Source}: {x.Text}")))
            .OrderByDescending(x => x.At).Select(x => x.Text).Take(6).ToList();
        var facts = new JsonObject { ["지금"] = now, ["최근"] = recent, ["최근 바뀐 이력"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()), ["다음 일정"] = upcoming };
        if (nasFlow is not null) facts["NAS 작업 흐름"] = new JsonObject { ["꼬리표"] = nasFlow.Tag, ["묶음"] = new JsonArray(nasFlow.Steps.Select(x => (JsonNode)x.Fact.DeepClone()).ToArray()) };
        if (poFlow is not null) facts["ERP 발주 흐름"] = new JsonObject { ["꼬리표"] = poFlow.Tag, ["묶음"] = new JsonArray(poFlow.Steps.Select(x => (JsonNode)x.Fact.DeepClone()).ToArray()) };

        // 규칙 문장(AI가 없거나 검사에 걸릴 때)
        // 틀: "예상 프로젝트 단계는 ○○이며, [지금 작업 상태]. 최근 [바뀐 이력]." (11-05 사용자 결정)
        var head = stage is not null ? $"예상 프로젝트 단계는 {stage}이며" : $"ERP 상태는 {erp?.Status ?? "알 수 없음"}이며";
        var work = modNow.Count > 0 ? $"모듈라는 {modNow[0]} 상태입니다"
            : t.Nas14 > 0 ? $"최근 2주 NAS에서 파일 {t.Nas14}개를 고쳤습니다"
            : t.Po30 > 0 ? $"최근 30일 발주가 {t.Po30}건 있습니다"
            : "최근 30일 활동은 없습니다";
        var rule = $"{head}, {work}." + (changes.Count > 0 ? $" 최근 {changes[0]}." : "");

        var ev = new JsonArray();
        if (mods.Count > 0) ev.Add(new JsonObject { ["kind"] = "modular", ["label"] = $"모듈라 {string.Join(", ", modNow.DefaultIfEmpty("기록"))}" });
        if (t.Nas14 > 0 && t.NasDir is not null) ev.Add(new JsonObject { ["kind"] = "nas", ["label"] = $"NAS {nasTopDir ?? ""} {t.Nas14}개".Replace("  ", " "), ["path"] = t.NasDir });
        if (t.Po30 > 0) ev.Add(new JsonObject { ["kind"] = "po", ["label"] = $"ERP 발주 30일 {t.Po30}건" });
        if (texts.Count > 0) ev.Add(new JsonObject { ["kind"] = "post", ["label"] = $"최근 글 {texts.Count}건", ["at"] = texts[0].At.ToString("o") });
        var detail = new JsonObject
        {
            ["kind"] = "detail",
            ["stage"] = stage is not null
                ? new JsonObject { ["label"] = "DY ONE 예상 프로젝트 단계", ["value"] = stage, ["others"] = new JsonArray(otherRun.Select(o => (JsonNode)o).ToArray()), ["changedAt"] = stageAt?.ToString("o") }
                : new JsonObject { ["label"] = "ERP 상태", ["value"] = erp?.Status },
            ["modular"] = modNow.Count > 0 ? new JsonObject { ["status"] = string.Join(", ", modNow), ["lastAt"] = texts.FirstOrDefault(x => x.Source.Contains("모듈라"))?.At.ToString("o") } : null,
            ["erp"] = new JsonObject { ["status"] = erp?.Status, ["from"] = erp?.From?.ToString("yyyy-MM-dd"), ["to"] = erp?.To?.ToString("yyyy-MM-dd") },
            ["process"] = procs.Count > 0 ? string.Join(" / ", procs) : null,
            ["nasDir"] = t.NasDir,
            ["nas14"] = t.Nas14,
            ["nasDays14"] = t.NasDays14,
            ["lastPo"] = t.LastPo?.ToString("yyyy-MM-dd"),
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()),
        };
        return (facts, rule, ev, t.Act1 + t.Nas1, nasFlow, poFlow, detail);
    }

    /// <summary>흐름 한 상자: 꼬리표 · 추세 한 줄 · 단계(언제·제목·자세히). AI 제목·추세는 숫자 검사·길이·개수가 맞을 때만.</summary>
    private static JsonObject? FlowNode(BriefFlow.Flow? f, JsonNode? aiTrend, JsonNode? aiTitles, string factsJson)
    {
        if (f is null) return null;
        var trend = aiTrend?.ToString().Trim();
        var trendOk = trend is { Length: >= 8 and <= 80 } && NumbersOk(trend, factsJson);
        var titles = (aiTitles as JsonArray)?.Select(x => x?.ToString().Trim() ?? "").ToList();
        var titlesOk = titles is not null && titles.Count == f.Steps.Count && titles.All(x => x.Length is >= 2 and <= 20 && NumbersOk(x, factsJson));
        return new JsonObject
        {
            ["tag"] = f.Tag,
            ["trend"] = trendOk ? trend : f.RuleTrend,
            ["source"] = trendOk && titlesOk ? "ai" : "rule",
            ["steps"] = new JsonArray(f.Steps.Select((x, i) => (JsonNode)new JsonObject
            {
                ["when"] = x.When, ["title"] = titlesOk ? titles![i] : x.RuleTitle, ["detail"] = x.Detail,
            }).ToArray()),
        };
    }

    [GeneratedRegex(@"\d+")] private static partial Regex NumRe();

    /// <summary>문장에 나온 숫자는 모두 사실(facts) 안에 있어야 한다.</summary>
    public static bool NumbersOk(string sentence, string factsJson)
    {
        var allowed = NumRe().Matches(factsJson).Select(m => m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0").ToHashSet();
        return NumRe().Matches(sentence).All(m => allowed.Contains(m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0"));
    }

    private const string BriefPrompt = """
        너는 두림야스카와 사내 프로젝트 포털의 '오늘의 프로젝트 브리핑'을 쓰는 비서다. 입력 JSON의 사실만 쓴다.
        1) sentence: 한국어 한 문장(50~110자). 틀: "예상 프로젝트 단계는 ○○이며, [지금 작업 상태]. 최근 [바뀐 이력]."
           - '예상 프로젝트 단계'가 없고 'ERP 상태'만 있으면 "ERP 상태는 ○○이며, …"로 시작한다.
           - 지금 작업 상태: 모듈라 상태와 NAS·발주 흐름에서 가장 중요한 것 하나(예: "모듈라는 준비중으로 도면 작업이 이어지고 있습니다").
           - 최근 바뀐 이력: '최근 바뀐 이력'의 가장 중요한 것 하나를 날짜와 함께(예: "최근 10/27 모듈라 설치위치가 바뀌었습니다"). 없으면 뺀다.
           - 최근 활동이 없으면 사실대로 "최근 30일 활동은 없습니다"라고 쓴다.
        2) nasTrend: 'NAS 작업 흐름'을 한 줄(20~60자)로. 무엇을 언제 어떻게 고치는지(예: "승인도 묶음을 두 번 올린 뒤, 지금은 제작도를 하루 한두 개씩 고치는 중입니다"). 흐름이 없으면 "".
           nasTitles: 'NAS 작업 흐름'의 묶음마다 짧은 제목(2~14자, 묶음 순서 그대로, 개수 같게). 예: "승인도 묶음 저장", "하나씩 수정".
        3) poTrend: 'ERP 발주 흐름'을 한 줄(20~60자)로. 무엇을 사고 있는지 품목 종류로 묶어서(예: "9월 초에 핵심 부품을 몰아서 발주했고, 지금은 작업용품만 가끔 이어집니다"). 흐름이 없으면 "".
           poTitles: 'ERP 발주 흐름'의 묶음마다 품목 종류로 짧은 제목(2~14자, 개수 같게). 예: "핵심 부품 한꺼번에", "추가·보완", "작업용품".
        - 사실에 없는 숫자·날짜·사람·내용을 만들지 않는다. 숫자는 입력에 있는 그대로만 쓴다. 금액은 쓰지 않는다.
        - 프로젝트 이름·코드는 쓰지 않는다(화면에 따로 나온다). 모듈라 상태의 앞 번호(1. 2. …)는 쓰지 않는다.
        - 날짜는 입력에 적힌 모양 그대로 쓴다. 문장은 '~입니다' 또는 '~습니다'로 끝낸다.
        JSON으로만 답한다: {"sentence":"…","nasTrend":"…","nasTitles":["…"],"poTrend":"…","poTitles":["…"]}
        """;
}
