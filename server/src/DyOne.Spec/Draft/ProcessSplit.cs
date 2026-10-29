using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>공정 하나(예: 1공정 프론트 도어 = FRT)와 그 공정의 로봇 대수</summary>
public sealed record SpecProcess(int No, string Label, string Code, int Spray, int Opener, int Page)
{
    /// <summary>열 이름: "1공정 프론트 도어 (FRT)"</summary>
    public string Title => $"{(No > 0 ? $"{No}공정 " : "")}{Label}{(Code.Length > 0 ? $" ({Code})" : "")}";
}

/// <summary>
/// 사양서의 공정 나누기와 공정별 로봇 대수.
///  - "1공정 (프론트 도어) : 오프너 2대, 스프레이 2대" 같은 줄을 읽는다(아산 V5 p6).
///  - 같은 공정을 사양서·영업부가 여러 이름으로 부른다: 프론트 = FR = FRT = FRONT → FRT, 리어 = RR = REAR → RR
///    (아산 V5 p7 그림 "FR DR INR / RR DR INR", 영업부 사양정리 "중도 DOOR 내판 (FRT) / (RR)").
/// </summary>
public static class ProcessSplit
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    // "1공정 (프론트 도어) : 오프너 2대, 스프레이 2대" / "2공정(리어도어): 스프레이 2대 + 오프너 2대"
    private static readonly Regex Line = new(@"(\d)\s*공\s*정\s*\(?\s*([^):：]{1,20}?)\s*\)?\s*[:：]\s*(.+)", Opt);
    private static readonly Regex Count = new(@"(오프너|OPENER|스프레이|도장|SPRAY|PAINT(?:ING)?)\s*(?:로\s*봇|ROBOTS?)?\s*[:：]?\s*(\d{1,2})\s*(?:대|EA|SET)", Opt);
    private static readonly Regex Front = new(@"프론트|\bFRT?\b|FRONT", Opt);
    private static readonly Regex Rear = new(@"리어|\bRR\b|REAR", Opt);

    /// <summary>프론트·리어 표기를 FRT·RR로 맞춘다(그 밖은 빈 값)</summary>
    public static string CodeOf(string label) => Front.IsMatch(label) ? "FRT" : Rear.IsMatch(label) ? "RR" : "";

    public static List<SpecProcess> Read(IReadOnlyList<Block> blocks)
    {
        var list = new List<SpecProcess>();
        foreach (var b in blocks)
        {
            var t = Regex.Replace(b.Text, @"\s+", " ");
            foreach (Match m in Line.Matches(t))
            {
                int spray = 0, opener = 0;
                foreach (Match c in Count.Matches(m.Groups[3].Value))
                {
                    var n = int.Parse(c.Groups[2].Value);
                    if (Regex.IsMatch(c.Groups[1].Value, "오프너|OPENER", RegexOptions.IgnoreCase)) opener += n; else spray += n;
                }
                if (spray + opener == 0) continue;
                var no = int.Parse(m.Groups[1].Value);
                var label = Regex.Replace(m.Groups[2].Value.Trim(), @"(\S)(도어)", "$1 $2"); // "리어도어" → "리어 도어"
                if (list.All(p => p.No != no)) list.Add(new SpecProcess(no, label, CodeOf(label), spray, opener, b.UnitIndex));
            }
        }
        return list.Count >= 2 ? list.OrderBy(p => p.No).ToList() : [];
    }

    /// <summary>사양서에 FR/RR(FRT) 표기가 있는 쪽(근거 표시용)</summary>
    public static List<int> CodePages(IReadOnlyList<Block> blocks) =>
        blocks.Where(b => Regex.IsMatch(b.Text, @"\bFRT?\s*DR\b|\bRR\s*DR\b|\bFRT\b", RegexOptions.IgnoreCase)).Select(b => b.UnitIndex).Distinct().ToList();
}
