using System.Text.RegularExpressions;
using DyOne.Spec.Draft;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Dept;

public sealed record DeptHit(int Page, string Text);
public sealed record DeptRowValue(string Key, string Value, List<int> Pages);
/// <summary>항목 하나의 근거: 사양정리 초안 줄 값 + 사양서 본문에서 찾은 쪽(많이 나온 쪽 위주, 최대 MaxPages) · 찾은 쪽 수</summary>
public sealed record DeptItemResult(string Id, string Title, string? Incident, bool AskIfMissing,
    List<DeptRowValue> Rows, List<DeptHit> Hits, int HitPages);
public sealed record DeptRoleResult(string Key, string Dept, string Group, string Does, string? Budget, List<DeptItemResult> Items);

/// <summary>
/// 부서별 확인 항목(DeptChecklist)을 이번 사양서에 맞춘다: 항목 키워드가 나온 쪽과 그 줄, 연결된 사양정리 초안 줄의 값·근거 쪽.
/// 규칙만 쓴다(사내 AI 연결 전). 쪽을 못 찾은 항목은 화면에서 '사양서에서 못 찾음'(AskIfMissing이면 '확인 필요')으로 보인다.
/// </summary>
public static class DeptMatcher
{
    private const int MaxPages = 4;
    private static readonly Regex Marks = new(@"\*\*|\{\{|\}\}|\[\[|\]\]|<<|>>", RegexOptions.Compiled);
    private static readonly Dictionary<string, Regex> Cache = [];

    public static List<DeptRoleResult> Match(IReadOnlyList<Block> blocks, SummaryDraft? draft) =>
        DeptChecklist.Roles.Select(r => new DeptRoleResult(r.Key, r.Dept, r.Group, r.Does, r.Budget,
            r.Items.Select(i => Item(r.Key, i, blocks, draft)).ToList())).ToList();

    private static DeptItemResult Item(string roleKey, DeptItem item, IReadOnlyList<Block> blocks, SummaryDraft? draft)
    {
        var rx = RegexOf($"{roleKey}.{item.Id}", item.Keywords);
        var byPage = blocks
            .Select(b => (b, m: rx.Match(b.Text)))
            .Where(x => x.m.Success)
            .GroupBy(x => x.b.UnitIndex)
            .ToList();
        var hits = byPage
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Take(MaxPages)
            .OrderBy(g => g.Key)
            .Select(g => new DeptHit(g.Key, Snippet(rx, g.First().b.Text)))
            .ToList();

        var rows = draft is null ? [] : item.Rows
            .Select(k => draft.Rows.FirstOrDefault(r => r.Key == k && !r.Hidden))
            .Where(r => r is not null && (r.Value.Length > 0 || r.Split.Count > 0))
            .Select(r => new DeptRowValue(r!.Key, Plain(r.Split.Count > 0 ? string.Join(" / ", r.Split.Where(s => s.Length > 0)) : r.Value), r.Pages))
            .ToList();
        return new DeptItemResult(item.Id, item.Title, item.Incident, item.AskIfMissing, rows, hits, byPage.Count);
    }

    private static Regex RegexOf(string key, string[] keywords)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var rx))
                Cache[key] = rx = new Regex(string.Join("|", keywords.Select(k => $"(?:{k})")), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return rx;
        }
    }

    private static string Plain(string s) => Marks.Replace(s, "").Trim();

    /// <summary>찾은 말 앞뒤로 잘라 한 줄(140자 안팎)</summary>
    private static string Snippet(Regex rx, string text)
    {
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        if (t.Length <= 140) return t;
        var at = rx.Match(t) is { Success: true } m ? m.Index : 0;
        var start = Math.Max(0, Math.Min(at - 50, t.Length - 140));
        var s = t.Substring(start, Math.Min(140, t.Length - start)).Trim();
        return (start > 0 ? "…" : "") + s + (start + 140 < t.Length ? "…" : "");
    }
}
