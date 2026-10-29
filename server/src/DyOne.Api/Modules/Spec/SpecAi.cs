using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DyOne.Spec.Draft;
using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;

namespace DyOne.Api.Modules.Spec;

/// <summary>
/// 사내 AI(dy-linux SGLang, OpenAI 호환 /v1/chat/completions)로 고객 사양서 → 사양정리 초안 JSON.
/// 설정: Llm:BaseUrl(예 http://192.168.11.146:18101/v1), Llm:Model, Llm:ApiKey(user-secrets). 키가 없으면 IsConfigured=false → 규칙 초안.
/// 모델 자리(기설치·선정 필요)와 과거 후보는 AI에 맡기지 않고 DraftBuilder 규칙 + 설비 이력으로 붙인다(근거를 확실히 하려고).
/// </summary>
public sealed class SpecAi(IConfiguration config, IHttpClientFactory http, ILogger<SpecAi> log)
{
    private const int MaxChars = 60_000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Llm:ApiKey"]) && !string.IsNullOrWhiteSpace(config["Llm:BaseUrl"]);

    private const string System = """
        너는 두림야스카와(로봇 도장 설비 업체) 영업부의 사양정리 작성자다. 고객 사양서를 읽고 영업부 '사양정리' 초안을 JSON으로만 답한다.
        규칙:
        - 값은 오직 이 사양서에서만 가져온다. 추측·다른 프로젝트 값 금지. 없으면 빈 문자열.
        - 영어 사양서는 한국어로 옮긴다(제품명·고유 용어는 원문).
        - 두림(로봇 업체) 범위만 넣는다. 고객·타 업체 범위는 뺀다.
        - 표시: **굵게**(항목명·차종명), {{파랑}}(핵심 값), [[빨강]](중요·변경·선정 필요).
        - 모든 값에 근거 쪽 번호(pages)를 넣는다. 원문 앞의 [p숫자]가 쪽 번호다.
        - rows 키(고정): Robot, Booth size, Basic Condition, Body Size, Utility, Bell Cleaner, Atomizer, Paint, Color(CCV), FGP, Panel, System, Training, Schedule, Stand by, Others.
          value는 한 줄 요약(예: "스프레이 로봇 * 2ea / 오프너 로봇 * 1ea", "66.6 UPH / 연속 컨베이어 / 바디 피치 6.6m"), note는 '기타 사항' 열(짧게).
          Robot value에는 용도별 대수를 "스프레이 로봇 * n ea / 오프너 로봇 * n ea" 형식으로 쓴다. 모델이 정해지지 않았으면 모델은 쓰지 않는다.
        - etc: 설치·비용·범위에 영향을 주는 기타 사항 체크리스트 15~25개, "**항목명 :** 내용" 형식, 명사형으로 짧게. 사양설명회·Lab test·OLT·시뮬레이션·보증·안전장치 포함.
        - pcs: 도료 공급(PCS)·클리너·폐신너 관련 체크리스트.
        - schedule_page: 일정표(공사 일정·상세 일정·첨부 일정표)가 있는 쪽 번호(그림으로만 된 쪽도 쪽 제목으로 판단), 없으면 null. schedule_note: 일정 요약 한 줄.
        - layout_page: 설비 배치(레이아웃) 그림이 있는 쪽, 없으면 null.
        - total: "Total {{n robots}}\n(스프레이 a + 오프너 b)".
        출력 형식(JSON만):
        {"title":"…사양정리","cover":"…\n…\n사양정리","total":"…","col_header":"공정 이름",
         "rows":{"Robot":{"value":"","note":"","pages":[9]}, …},
         "etc":[{"text":"","pages":[8]}],"pcs":[{"text":"","pages":[15]}],
         "schedule_page":6,"schedule_note":"","layout_page":2,
         "edu":[["No","장소","과정","기간","비고"],["1","…","…","…","…"]]}
        """;

    public async Task<SummaryDraft> DraftAsync(IReadOnlyList<Block> blocks, string projectName, ProjectContext ctx, CancellationToken ct)
    {
        var text = new StringBuilder();
        foreach (var g in blocks.GroupBy(b => b.UnitIndex))
        {
            text.Append($"\n[p{g.Key}]\n");
            foreach (var b in g) text.AppendLine(b.Text);
            if (text.Length > MaxChars) break;
        }

        var client = http.CreateClient("llm");
        client.Timeout = TimeSpan.FromMinutes(5);
        var req = new HttpRequestMessage(HttpMethod.Post, config["Llm:BaseUrl"]!.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Llm:ApiKey"]);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = config["Llm:Model"] ?? "qwen3.8-27b-fp8",
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = System },
                new { role = "user", content = $"프로젝트: {projectName}\n고객 사양서 원문:\n{text}" },
            },
        }), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"사내 AI 응답 오류 {(int)res.StatusCode}");
        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("사내 AI가 JSON을 돌려주지 않았습니다.");
        var j = JsonNode.Parse(content[start..(end + 1)])!.AsObject();
        log.LogInformation("사내 AI 사양정리 초안 {Project}", projectName);
        return FromAi(j, projectName, ctx);
    }

    private static SummaryDraft FromAi(JsonObject j, string projectName, ProjectContext ctx)
    {
        var d = DraftBuilder.NewDraft(projectName, ctx);
        string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        List<int> P(JsonNode? n) => n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0).Where(i => i > 0).ToList() : [];
        int? I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        if (S(j["title"]) is { Length: > 0 } t) d.Title = t;
        if (S(j["cover"]) is { Length: > 0 } c) d.Cover = c;
        if (S(j["total"]) is { Length: > 0 } tot) d.Total = tot;
        if (S(j["col_header"]) is { Length: > 0 } ch) d.ColHeader = ch;
        if (j["rows"] is JsonObject rows)
            foreach (var r in d.Rows)
                if (rows[r.Key] is JsonObject o) { r.Value = S(o["value"]); r.Note = S(o["note"]); r.Pages = P(o["pages"]); }
        d.Etc = (j["etc"] as JsonArray ?? []).OfType<JsonObject>().Select(o => new DraftCheck(S(o["text"]), P(o["pages"]))).Where(x => x.Text.Length > 0).ToList();
        d.Pcs = (j["pcs"] as JsonArray ?? []).OfType<JsonObject>().Select(o => new DraftCheck(S(o["text"]), P(o["pages"]))).Where(x => x.Text.Length > 0).ToList();
        d.ScheduleNote = S(j["schedule_note"]);
        // 그림은 쪽 전체(여백 조금 뺀 A4 세로 기준)를 붙인다. 정확한 영역은 화면에서 확인.
        if (I(j["schedule_page"]) is { } sp) d.ScheduleImage = new PageClip(sp, [18, 60, 578, 800]);
        if (I(j["layout_page"]) is { } lp) d.LayoutImage = new PageClip(lp, [18, 60, 578, 800]);
        if (j["edu"] is JsonArray edu && edu.Count > 1)
            d.EduTable = new JsonObject
            {
                ["left"] = 9.4, ["top"] = 3.6, ["widths"] = new JsonArray(0.6, 2.2, 2.0, 1.8, 1.8),
                ["rows"] = edu.DeepClone(),
            };
        d.Engine = "ai";
        d.EngineNote = "사내 AI가 사양서를 읽어 작성했습니다. 값마다 근거 쪽을 눌러 원문과 맞는지 확인해 주세요.";
        return d;
    }
}
