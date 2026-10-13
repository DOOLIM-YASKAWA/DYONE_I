using System.Net;
using System.Text.RegularExpressions;

namespace DyOne.Worker.Jobs;

/// <summary>그룹웨어(tims) WORKS 원문 해석 공용 도우미 — 시각(KST), 활동 글 HTML, 변경 이력 메시지.</summary>
public static partial class GwText
{
    /// <summary>그룹웨어 timestamp(without time zone)는 한국 시각이다 → UTC</summary>
    public static DateTimeOffset? Kst(DateTime? local) =>
        local is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Unspecified), TimeSpan.FromHours(9)).ToUniversalTime() : null;

    /// <summary>활동 글 HTML → 화면용 글자. 줄바꿈은 살리고 태그·스타일은 버린다.</summary>
    public static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = BlockTags().Replace(html, "\n");
        s = ImgTag().Replace(s, "[이미지]");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        s = string.Join('\n', s.Split('\n').Select(l => MultiSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0));
        return s.Length == 0 ? null : s;
    }

    // 그룹웨어는 변경 이력을 '[머리] 내용'으로 남기고, 머리에 ${resource('키')} 번역 키를 쓴다.
    // 예) [${resource('task.status')} ${resource('task.action.change')}] 4. 시운전 -> 5. 외부검수
    //     [출하 월 ${resource('task.action.edit')}] 미지정 -> 26년 9월   (여러 필드를 한 번에 바꾸면 줄마다 하나씩)
    private static readonly Dictionary<string, string> Resources = new()
    {
        ["task.status"] = "상태",
        ["task.action.change"] = "변경",
        ["task.action.edit"] = "변경",
        ["applet.log.added.doc"] = "등록",
        ["applet.log.added.doc.bymappingcopy"] = "등록(다른 앱에서 복사)",
        ["applet.log.added.activity"] = "활동기록 등록",
        ["applet.log.edited.activity"] = "활동기록 수정",
        ["applet.log.deleted.activity"] = "활동기록 삭제",
        ["task.log.added.comment"] = "댓글 등록",
        ["task.log.edited.comment"] = "댓글 수정",
        ["task.log.removed.comment"] = "댓글 삭제",
    };

    /// <param name="Kind">status · edit · created · activity · comment · etc</param>
    public sealed record ParsedLog(string Kind, string Title, string? Detail, string? From, string? To);

    private static string Translate(string text) =>
        ResourceKey().Replace(text, m => Resources.TryGetValue(m.Groups[1].Value, out var ko) ? ko : m.Groups[1].Value);

    public static ParsedLog ParseLog(string? message)
    {
        var raw = message ?? "";
        var m = LogHead().Match(raw);
        var headRaw = m.Success ? m.Groups[1].Value : "";
        var head = MultiSpace().Replace(Translate(headRaw), " ").Trim();
        var body = (m.Success ? raw[m.Length..] : raw).Trim();

        if (headRaw.Contains("task.status"))
        {
            var parts = body.Split("->", 2);
            string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : MultiSpace().Replace(v, " ").Trim();
            return new ParsedLog("status", "상태 변경", Clean(body), Clean(parts[0]), parts.Length > 1 ? Clean(parts[1]) : null);
        }

        var kind = headRaw.Contains("added.doc") ? "created"
            : headRaw.Contains("activity") ? "activity"
            : headRaw.Contains("comment") ? "comment"
            : headRaw.Contains("task.action.edit") ? "edit" : "etc";
        // 여러 필드를 한 번에 바꾼 경우: 줄마다 '[필드 변경] 전 -> 후' → '필드: 전 → 후'
        var lines = Translate(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => FieldLine().Replace(l.Trim(), "$1: ")).Select(l => MultiSpace().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        var detail = kind == "edit" ? string.Join('\n', lines)
            : body.Length == 0 ? null : HtmlToText(body);
        if (detail is { Length: > 1000 }) detail = detail[..1000] + "…";
        var title = kind == "edit" ? (lines.Count > 1 ? "정보 변경" : head) : head;
        return new ParsedLog(kind, title.Length == 0 ? "변경" : title, detail, null, null);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex MultiSpace();
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d|/tr)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BlockTags();
    [GeneratedRegex(@"<\s*img\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ImgTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"\$\{resource\('([^']+)'\)\}")] private static partial Regex ResourceKey();
    [GeneratedRegex(@"^\[([^\]]*)\]")] private static partial Regex LogHead();
    [GeneratedRegex(@"^\[(.+?)\s*변경\]\s*")] private static partial Regex FieldLine();
}
