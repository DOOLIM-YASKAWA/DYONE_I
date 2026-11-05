using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>
/// '상세히' 창의 최근 흐름(NAS 작업 · ERP 발주)을 묶는 규칙(11-05).
/// 묶기·꼬리표(꾸준히 작업·잦아듦 등)는 프로그램이 정하고, 사내 AI는 각 묶음의 짧은 제목과 추세 한 줄만 쓴다.
/// AI가 없거나 검사에 걸리면 여기의 규칙 제목·규칙 추세를 쓴다.
/// 입력: sync.project_trace.nas_flow(날짜×폴더 수정 수·대표 파일) · po_recent(최근 90일 발주서의 거래처·품목, 금액 없음).
/// </summary>
public static partial class BriefFlow
{
    public sealed record Step(string When, string RuleTitle, string Detail, JsonObject Fact);
    public sealed record Flow(string Tag, string RuleTrend, List<Step> Steps);

    [GeneratedRegex(@"^\s*\d+\s*\.\s*")] private static partial Regex NumPrefixRe();

    /// <summary>모듈라 상태 '1. 준비중' → '준비중'(화면·문장용. 순번은 판정에만 쓴다)</summary>
    public static string? StripNo(string? s) => s is null ? null : NumPrefixRe().Replace(s, "").Trim();

    private static string Md(DateOnly d) => $"{d.Month:00}/{d.Day:00}";
    private static string Last(string dir) => dir.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? dir;
    private static string Short(string name) => name.Length <= 48 ? name : name[..46] + "…";

    /// <summary>
    /// NAS 최근 2주 흐름. 하루 수정 수가 10개 이상이고 2주 합계의 30% 이상이면 '묶음 저장'한 날로 본다.
    /// 묶음 저장한 날마다 한 단계, 마지막 묶음 뒤의 작은 수정들은 '하나씩 수정' 한 단계로. 묶음이 없으면 '조금씩 수정' 한 단계. 최근 3단계만.
    /// </summary>
    public static Flow? Nas(IEnumerable<JsonNode?> cells, DateOnly today)
    {
        var from = today.AddDays(-13);
        var rows = cells.OfType<JsonObject>()
            .Select(c => (Day: DateOnly.TryParse(c["d"]?.ToString(), out var d) ? d : DateOnly.MinValue, Dir: c["dir"]?.ToString() ?? "", N: c["n"]?.GetValue<int>() ?? 0,
                Files: (c["files"] as JsonArray ?? []).Select(f => f?.ToString() ?? "").Where(f => f.Length > 0).ToList()))
            .Where(c => c.Day >= from && c.Day <= today && c.N > 0).ToList();
        if (rows.Count == 0) return null;
        var days = rows.GroupBy(c => c.Day).OrderBy(g => g.Key).Select(g =>
        {
            var top = g.OrderByDescending(c => c.N).First();
            return (Day: g.Key, N: g.Sum(c => c.N), Dir: top.Dir, Files: g.OrderByDescending(c => c.N).SelectMany(c => c.Files).Distinct().Take(3).ToList());
        }).ToList();
        var total = days.Sum(d => d.N);
        bool Burst((DateOnly Day, int N, string Dir, List<string> Files) d) => d.N >= 10 && d.N >= total * 0.3;
        var steps = new List<Step>();
        foreach (var d in days.Where(Burst))
            steps.Add(new Step(Md(d.Day), $"{Last(d.Dir)} 묶음 저장", string.Join(", ", d.Files.Select(Short)),
                new JsonObject { ["언제"] = Md(d.Day), ["폴더"] = d.Dir, ["파일 수"] = $"{d.N}개", ["대표 파일"] = new JsonArray(d.Files.Select(f => (JsonNode)f).ToArray()), ["종류"] = "한꺼번에 저장한 날" }));
        var lastBurst = days.Where(Burst).Select(d => (DateOnly?)d.Day).LastOrDefault();
        var tail = days.Where(d => !Burst(d) && (lastBurst is null || d.Day > lastBurst)).ToList();
        if (tail.Count > 0)
        {
            var files = tail.OrderByDescending(d => d.Day).SelectMany(d => d.Files).Distinct().Take(3).ToList();
            var dir = tail.GroupBy(d => d.Dir).OrderByDescending(g => g.Sum(x => x.N)).First().Key;
            var kind = lastBurst is null ? "조금씩 수정" : "하나씩 수정";
            steps.Add(new Step($"{Md(tail[0].Day)}~", kind, string.Join(", ", files.Select(Short)),
                new JsonObject { ["언제"] = $"{Md(tail[0].Day)}~", ["폴더"] = dir, ["파일 수"] = $"{tail.Sum(d => d.N)}개", ["고친 날"] = $"{tail.Count}일", ["대표 파일"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()), ["종류"] = "날마다 몇 개씩 고침" }));
        }
        steps = steps.TakeLast(3).ToList();
        var share = (double)days.Max(d => d.N) / total;
        var tag = days.Count >= 5 ? "꾸준히 작업" : share >= 0.6 && days.Count <= 3 ? "한 번에 올림" : "가끔 작업";
        var bursts = days.Count(Burst);
        var mainDir = Last(days.GroupBy(d => d.Dir).OrderByDescending(g => g.Sum(x => x.N)).First().Key);
        var rule = bursts > 0 && tail.Count > 0 ? $"{mainDir} 파일을 한꺼번에 올린 뒤, 지금은 하나씩 고치는 중입니다."
            : bursts > 0 ? $"{mainDir} 파일을 한꺼번에 올렸습니다."
            : $"{mainDir} 파일을 조금씩 고치고 있습니다.";
        return new Flow(tag, rule, steps);
    }

    /// <summary>
    /// ERP 발주 흐름(최근 90일). 발주일 사이가 10일 넘게 벌어지면 다른 묶음으로 나눈다. 최근 3묶음만.
    /// 꼬리표: 최근 30일 발주가 0 → 멈춤, 31~60일의 절반보다 적음 → 잦아듦, 1.5배 넘고 3건 이상 → 늘어남, 그 밖 → 이어짐.
    /// </summary>
    public static Flow? Po(IEnumerable<JsonNode?> pos, TraceInfo t)
    {
        var rows = pos.OfType<JsonObject>()
            .Select(p => (Day: DateOnly.TryParse(p["d"]?.ToString(), out var d) ? d : DateOnly.MinValue, Partner: p["partner"]?.ToString() ?? "",
                Items: (p["items"] as JsonArray ?? []).Select(i => i?.ToString() ?? "").Where(i => i.Length > 0).ToList()))
            .Where(p => p.Day != DateOnly.MinValue).OrderBy(p => p.Day).ToList();
        if (rows.Count == 0) return null;
        var groups = new List<List<(DateOnly Day, string Partner, List<string> Items)>> { new() };
        foreach (var r in rows)
        {
            if (groups[^1].Count > 0 && r.Day.DayNumber - groups[^1][^1].Day.DayNumber > 10) groups.Add([]);
            groups[^1].Add(r);
        }
        var steps = groups.TakeLast(3).Select(g =>
        {
            var when = g[0].Day == g[^1].Day ? Md(g[0].Day) : $"{Md(g[0].Day)}~{g[^1].Day.Day:00}";
            if (g[0].Day.Month != g[^1].Day.Month) when = $"{Md(g[0].Day)}~{Md(g[^1].Day)}";
            var partners = g.GroupBy(x => x.Partner).OrderByDescending(x => x.Count()).Select(x => x.Key).Where(x => x.Length > 0).Take(3).ToList();
            var items = g.SelectMany(x => x.Items).Distinct().Take(6).ToList();
            var detail = string.Join(", ", items.Take(4).Select(Short)) + (partners.Count > 0 ? $" · {string.Join(", ", partners.Take(2))}" : "");
            return new Step(when, items.FirstOrDefault() is { } f ? $"{(f.Length <= 14 ? f : f[..13] + "…")} 등" : "발주", detail,
                new JsonObject { ["언제"] = when, ["발주서"] = $"{g.Count}건", ["거래처"] = new JsonArray(partners.Select(p => (JsonNode)p).ToArray()), ["품목"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()) });
        }).ToList();
        var tag = t.Po30 == 0 ? "멈춤" : t.Po30 * 2 < t.Po31_60 ? "잦아듦" : t.Po30 * 2 > t.Po31_60 * 3 && t.Po30 >= 3 ? "늘어남" : "이어짐";
        var topPartners = rows.GroupBy(x => x.Partner).OrderByDescending(x => x.Count()).Select(x => x.Key).Where(x => x.Length > 0).Take(2).ToList();
        var rule = tag switch
        {
            "멈춤" => "최근 30일에는 발주가 없습니다.",
            "잦아듦" => "앞서 많이 발주했고, 지금은 발주가 줄었습니다.",
            "늘어남" => "최근 발주가 늘고 있습니다.",
            _ => "발주가 꾸준히 이어지고 있습니다.",
        } + (topPartners.Count > 0 ? $" 주 거래처: {string.Join(", ", topPartners)}." : "");
        return new Flow(tag, rule, steps);
    }
}
