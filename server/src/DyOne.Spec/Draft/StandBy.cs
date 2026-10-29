using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// Stand by(양산 대응) 칸: 영업부 양식처럼 인원·교대·기간을 풀어서 쓴다.
///   영업부 예: "제어 1 & 기계 1 * 2 shift * 3주 / 티쳐 1 * 2 shift * 4주 (총 100 M/D)"
/// 사양서 표기 그대로 숫자를 옮긴다(주↔일 환산·합계 계산은 하지 않음 — 사양서에 적힌 합계만 붙인다).
///   아산 V5 p2 "기계, 제어) 2명× 15일× 2Shift(주/야)" → "기계·제어 2명 * 2 shift * 15일"
///   광주2 p34 "1차) 주,야 2주 대응 : 7M/D X 2 Weeks X 2Man X 2 Shift" → "1차 주,야 2주 대응 : 2명 * 2 shift * 2주"
///   GM SOR p12 "min. 4 persons(1 Safety,1 PLC & 2 teach engineers) x 2 shift x 45 days" → "4명 (1 Safety,1 PLC & 2 teach engineers) * 2 shift * 45일"
/// </summary>
public static class StandBy
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private const string X = @"\s*[×xX*]\s*";
    // 역할) n명 × d일 × s Shift  (표 칸 구분 '|'도 역할 끝으로)
    private static readonly Regex ByDays = new(@"([가-힣A-Za-z][가-힣A-Za-z,/·\s]{0,12}?)\s*[)|:：]\s*(\d{1,2})\s*(?:명|인|M\s*/\s*D|MAN)" + X + @"(\d{1,3})\s*일" + X + @"(\d)\s*SHIFT(?:\s*\(([^)]*)\))?", Opt);
    // n차) 설명 : dM/D × w Weeks × m Man × s Shift
    private static readonly Regex ByWeeks = new(@"(\d)\s*차\s*\)\s*([^:：]{0,20}?)\s*[:：]\s*\d{1,2}\s*M\s*/\s*D" + X + @"(\d{1,2})\s*WEEKS?" + X + @"(\d{1,2})\s*MAN" + X + @"(\d)\s*SHIFT", Opt);
    // n persons(…) x s shift x d days
    private static readonly Regex Persons = new(@"(\d{1,2})\s*PERSONS?\s*(\([^)]*\))?" + X + @"(\d)\s*SHIFTS?" + X + @"(\d{1,3})\s*DAYS", Opt);
    private static readonly Regex Total = new(@"(?:계|총|합계|TOTAL)\s*[:：|]?\s*(\d{2,4})\s*M\s*/\s*D", Opt);

    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var parts = new List<(string text, int page, string src)>();
        foreach (var b in blocks)
        {
            var t = Regex.Replace(b.Text, @"\s+", " ");
            foreach (Match m in ByDays.Matches(t))
            {
                var role = Regex.Replace(m.Groups[1].Value.Trim(), @"\s*[,/]\s*", "·");
                parts.Add(($"{role} {m.Groups[2].Value}명 * {m.Groups[4].Value} shift{(m.Groups[5].Success ? $"({m.Groups[5].Value})" : "")} * {m.Groups[3].Value}일", b.UnitIndex, m.Value));
            }
            foreach (Match m in ByWeeks.Matches(t))
                parts.Add(($"{m.Groups[1].Value}차 {m.Groups[2].Value.Trim()} : {m.Groups[4].Value}명 * {m.Groups[5].Value} shift * {m.Groups[3].Value}주", b.UnitIndex, m.Value));
            foreach (Match m in Persons.Matches(t))
                parts.Add(($"{m.Groups[1].Value}명 {m.Groups[2].Value} * {m.Groups[3].Value} shift * {m.Groups[4].Value}일".Replace("  ", " "), b.UnitIndex, m.Value));
        }
        // 같은 역할이 표와 본문에 두 번 나오면(아산 p2·p4) 처음 것만
        parts = parts.DistinctBy(p => Regex.Replace(p.text, @"\s|\([^)]*\)", "")).ToList();
        if (parts.Count == 0) return;
        var total = blocks.Select(b => (b, m: Total.Match(Regex.Replace(b.Text, @"\s+", " ")))).FirstOrDefault(x => x.m.Success);
        var row = d.Rows.First(r => r.Key == "Stand by");
        var before = row.Value;
        row.Value = string.Join(" / ", parts.Select(p => p.text)) + (total.m is { Success: true } tm ? $" (총 {tm.Groups[1].Value} M/D)" : "");
        row.Source = [.. parts.Select(p => $"{p.src} (p{p.page})"), .. total.m is { Success: true } ? new[] { $"{total.m.Value} (p{total.b.UnitIndex})" } : []];
        if (before.Length > 0 && !row.Value.Contains(before)) row.Source.Add($"사양서 요약 값: {before}");
        // 값을 새로 만들었으니 '원문 줄 그대로' 표시는 지운다
        row.Remark = string.Join("\n", row.Remark.Split('\n').Where(n => !n.StartsWith("사양서 원문 줄")));
        row.Pages = [.. row.Pages.Concat(parts.Select(p => p.page)).Concat(total.m is { Success: true } ? [total.b.UnitIndex] : Array.Empty<int>()).Distinct().Order()];
    }
}
