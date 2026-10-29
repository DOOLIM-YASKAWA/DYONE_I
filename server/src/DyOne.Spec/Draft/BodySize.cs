using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// Body Size 칸: 적용 차종(SpecValues 'body') 아래에 사양서 차량 제원 표의 차종별 치수를 붙인다(영업부: "1차종 (NQ5 PE) 4,554(L) x 1,865(W) x 1,432(H)").
///  - 차량 제원 표: 머리에 전장·전폭·전고 또는 "L × W × H"가 있는 블록에서 "차종 a b c"(차종 앞) / "a × b × c 차종"(차종 뒤)
///  - 기본 설계조건(설계 사이즈)은 차량 실제 치수가 아니라 설비 설계 기준이라 값이 아니라 기타 사항(RowNotes)에.
///  - 값이 애매하면(L·W·H 표기가 어긋남 등) 값에 넣지 않고 기타 사항에 원문과 함께.
/// </summary>
public static class BodySize
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Table = new(@"전\s*장.{0,40}전\s*폭.{0,40}전\s*고|L\s*[x×*]\s*W\s*[x×*]\s*H|차량\s*제원|바디\s*제원", Opt | RegexOptions.Singleline);
    private const string N = @"(\d,\d{3})";
    // 차종 뒤: "4,771 × 1,861 × 1,318 DN8 FL"
    private static readonly Regex After = new($@"{N}\s*[x×*]\s*{N}\s*[x×*]\s*{N}\s+([A-Z][A-Z0-9]{{1,5}}(?:\s+(?:FL|PE|N|EV|HEV))?)\b(?!\s*\()", Opt);
    // 차종 앞: "NQ5 PE 4,554 1,865 1,432"
    private static readonly Regex Before = new($@"\b([A-Z][A-Z0-9]{{1,5}}(?:\s+(?:FL|PE|N|EV|HEV))?)\s+{N}\s+{N}\s+{N}\b", Opt);

    // 차종 + 단위 붙은 치수(영문 사양서): "9BUB : 4,178mmL × 1,805mmW × 1,432mmH"
    private static readonly Regex Labeled = new($@"\b([0-9A-Z]{{2,8}}(?:\s+(?:FL|PE|N|EV|HEV))?)\s*[:：]\s*{N}\s*mm\s*L\s*[x×*]\s*{N}\s*mm\s*W\s*[x×*]\s*{N}\s*mm\s*H", Opt);

    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var row = d.Rows.First(r => r.Key == "Body Size");
        if (row.Edited) return;
        var found = new List<(string car, string l, string w, string h, int page)>();
        // 설계 최대 크기("Maximum body size for system design" 다음 줄 "BP1 : 5,000mmL …")는 차량이 아니라 기타 사항(RowNotes)
        static bool Design(string s) => Regex.IsMatch(s, @"MAXIMUM|DESIGN|설계", RegexOptions.IgnoreCase);
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (!Labeled.IsMatch(b.Text) || Design(b.Text) || (i > 0 && blocks[i - 1].UnitIndex == b.UnitIndex && Design(blocks[i - 1].Text))) continue;
            foreach (Match m in Labeled.Matches(Regex.Replace(b.Text, @"\s+", " ")))
                found.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, b.UnitIndex));
        }
        // 차량 제원 표: 머리(전장·전폭·전고) 블록 또는 그 바로 뒤 줄들(pptx·엑셀 표는 머리 행과 값 행이 블록으로 나뉜다 — "NQ5 PE | 4,554 | 1,865 | 1,432")
        var tableRows = blocks.Select((b, i) => (b, i)).Where(x => Table.IsMatch(x.b.Text)
            || Enumerable.Range(Math.Max(0, x.i - 3), Math.Min(3, x.i)).Any(j => blocks[j].UnitIndex == x.b.UnitIndex && Table.IsMatch(blocks[j].Text))).Select(x => x.b);
        foreach (var b in tableRows)
        {
            var t = Regex.Replace(b.Text.Replace(" | ", " "), @"\s+", " ");
            // 설계 조건 문장(기본 설계조건 : 4,800(L)×…)은 차량 치수가 아님
            if (Regex.IsMatch(t, @"설계\s*(?:조건|사이즈|치수)|DESIGN", RegexOptions.IgnoreCase) && !Regex.IsMatch(t, @"전\s*장|BIW")) continue;
            foreach (Match m in After.Matches(t)) found.Add((m.Groups[4].Value.Trim(), m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, b.UnitIndex));
            foreach (Match m in Before.Matches(t)) found.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, b.UnitIndex));
        }
        var all = found.DistinctBy(f => f.car.ToUpperInvariant()).ToList();
        // 승용 차체로 보기 어려운 치수(전폭 1,500 미만 등)는 사양서 표기 확인이 필요해 값이 아니라 기타 사항에
        static int I(string s) => int.Parse(s.Replace(",", ""));
        bool Odd((string car, string l, string w, string h, int page) c) =>
            I(c.l) is < 3000 or > 6000 || I(c.w) is < 1500 or > 2200 || I(c.h) is < 1000 or > 2200;
        foreach (var c in all.Where(Odd))
        {
            var text = $"{c.car} {c.l}(L) x {c.w}(W) x {c.h}(H)";
            row.AddRemark($"{c.car} 치수가 일반 차체와 달라 값 칸에 넣지 않음 — 사양서 표기 확인");
            row.Note = string.Join("\n", new[] { row.Note, text }.Where(s => s.Length > 0));
            row.Source = [.. row.Source, $"{text} ← {c.car} | {c.l} | {c.w} | {c.h} (p{c.page})"];
            if (!row.Pages.Contains(c.page)) row.Pages = [.. row.Pages.Append(c.page).Order()];
        }
        var cars = all.Where(c => !Odd(c)).ToList();
        if (cars.Count == 0) return;
        var dims = string.Join(" / ", cars.Select(c => $"{c.car} {c.l}(L) x {c.w}(W) x {c.h}(H)"));
        row.Value = row.Value.Length == 0 ? $"차량 제원: {dims}" : $"{row.Value}\n차량 제원: {dims}";
        foreach (var p in cars.Select(c => c.page).Distinct())
            if (!row.Pages.Contains(p)) row.Pages = [.. row.Pages.Append(p).Order()];
        // 근거 줄: 화면에서 그 쪽의 차종·치수 자리를 칠할 수 있게 차종과 숫자를 그대로
        foreach (var g in cars.GroupBy(c => c.page))
            row.Source = [.. row.Source, $"차량 제원 ← {string.Join(" | ", g.Select(c => $"{c.car} | {c.l} | {c.w} | {c.h}"))} (p{g.Key})"];
    }
}
