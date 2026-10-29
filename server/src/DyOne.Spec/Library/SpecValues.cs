using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Library;

/// <summary>고객 사양서에서 뽑은 사양정리 항목 값 한 개와 근거 위치</summary>
public sealed record SpecValue(string Value, string UnitKind, int Unit, string Source);

/// <summary>
/// 고객 사양서 → 사양정리 항목 값(규칙). 숫자·모델명처럼 규칙으로 뽑을 수 있는 것만 뽑는다.
/// 값마다 근거 줄(Source)과 쪽 번호를 남겨, 사양정리 초안에서 "이번 고객 사양서 n쪽"으로 보여 준다.
/// 판단이 들어가는 문장(보강 필요 등)은 뽑지 않는다(과거 사양정리 값을 본보기로 쓴다).
/// </summary>
public static class SpecValues
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Regex UphMax = new(@"MAX\s*[:：]?\s*(\d{2,3}(?:\.\d+)?)\s*UPH|최대[^|\n]{0,15}?(\d{2,3}(?:\.\d+)?)\s*UPH|(\d{2,3}(?:\.\d+)?)\s*UPH\s*\(?\s*MAX", Opt);
    private static readonly Regex Uph = new(@"(\d{2,3}(?:\.\d+)?)\s*UPH|UPH\s*[:：]\s*(\d{2,3}(?:\.\d+)?)", Opt);
    private static readonly Regex Speed = new(@"(\d+(?:\.\d+)?)\s*m\s*/\s*min", Opt);
    private static readonly Regex Pitch = new(@"피\s*치\s*[:：]?\s*(\d[\d,]*(?:\.\d+)?)\s*(mm|m)\b", Opt);
    private static readonly Regex Cycle = new(@"(?:사이클\s*타임|cycle\s*time|C/T)\s*[:：]?\s*(\d+(?:\.\d+)?)\s*(초|sec|s)\b", Opt);
    private static readonly Regex Conveyor = new(@"(연속|간헐|STOP\s*&\s*GO|스톱\s*앤\s*고)\s*(?:식\s*)?(?:콘|컨)베이어|(?:콘|컨)베이어\s*[:：]?\s*(연속|간헐)", Opt);

    private static readonly Regex RobotCount = new(
        @"(스프레이|도장|오프너|핸들링|실러|샌딩|폴리싱|검사|비전|와이핑)\s*로\s*봇(?:(?!로\s*봇|레일|\d\))[^\n]){0,60}?(\d{1,2})\s*(SET|대|EA)\b", Opt);
    private static readonly Regex Voltage = new(@"AC\s*(\d{3})\s*V[^|\n]{0,40}?(\d{2})\s*Hz", Opt);
    private static readonly Regex Air = new(@"(\d+(?:\.\d+)?(?:\s*~\s*\d+(?:\.\d+)?)?)\s*(bar|kgf\s*/\s*㎠|kgf/cm2|kg\s*/\s*㎠|MPa)", Opt);
    private static readonly Regex RailLen = new(@"레일[^|\n]{0,15}?(\d+(?:\.\d+)?)\s*m\b|(\d+(?:\.\d+)?)\s*m\s*(?:주행\s*)?레일", Opt);
    private static readonly Regex Colors = new(@"(?:칼라|컬러|colors?)\s*수?\s*[:：]\s*(\d{1,2})|(\d{1,2})\s*(?:칼라|컬러|색|colors?)\b", Opt);
    private static readonly Regex ColorSpare = new(@"스페어\s*[:：]?\s*(\d{1,2})", Opt);
    private static readonly Regex Sop = new(@"S\.?\s*O\.?\s*P\.?\s*[:：]?\s*([’'‘]?\s*\d{2,4}\s*[./년]\s*\d{1,2}(?:\s*[./월]\s*\d{1,2})?)", Opt);
    private static readonly Regex Md = new(@"(?:계|합계|총|total)[^\n]{0,10}?(\d{2,4})\s*M\s*/\s*D|(\d{2,4})\s*M\s*/\s*D", Opt);
    private static readonly Regex Training = new(@"교\s*육[^\n]{0,40}?(\d+)\s*(주|일|회|class)", Opt);
    private static readonly Regex Car = new(@"(?:적용\s*)?차\s*종\s*[:：|]\s*([^\n]{4,120})", Opt);
    private static readonly Regex Plc = new(@"(MITSUBISHI|미쓰비시|MELSEC[-\s]?\w*|SIEMENS|지멘스|S7-\d+|LS\s*(?:PLC|산전)|XGT|ALLEN[-\s]?BRADLEY|ROCKWELL|OMRON)", Opt);

    public static Dictionary<string, SpecValue> Extract(IReadOnlyList<Block> blocks)
    {
        var lines = blocks.Select(b => (b, text: b.Text.Replace('\n', ' ').Replace('\v', ' '))).Where(x => x.text.Length > 0).ToList();
        var v = new Dictionary<string, SpecValue>();

        SpecValue At((Block b, string text) x, string value) => new(value, x.b.UnitKind, x.b.UnitIndex, Trim(x.text));
        (Block b, string text)? First(Regex re) => lines.FirstOrDefault(x => re.IsMatch(x.text)) is { text: not null } f ? f : null;

        // 기본 조건: UPH(최대) / 컨베이어 / 속도 / 피치 / 사이클타임
        var basic = new List<string>();
        (Block b, string text)? basicAt = null;
        double? maxUph = null, normUph = null;
        foreach (var x in lines)
        {
            foreach (Match m in UphMax.Matches(x.text))
            {
                var n = double.Parse(G(m, 1, 2, 3));
                if (n is >= 5 and <= 120 && (maxUph is null || n > maxUph)) { maxUph = n; basicAt ??= x; }
            }
            foreach (Match m in Uph.Matches(x.text))
            {
                var n = double.Parse(G(m, 1, 2));
                if (n is >= 5 and <= 120 && n != maxUph) { normUph ??= n; basicAt ??= x; }
            }
        }
        if (maxUph is null && normUph is not null) (maxUph, normUph) = (normUph, null);
        if (maxUph is not null) basic.Add(normUph is not null && normUph < maxUph ? $"{normUph:0.#} UPH (MAX {maxUph:0.#})" : $"{maxUph:0.#} UPH(MAX)");
        if (First(Conveyor) is { } cv) { var m = Conveyor.Match(cv.text); basic.Add($"컨베이어({G(m, 1, 2)})"); basicAt ??= cv; }
        if (First(Speed) is { } sp) { basic.Add($"{Speed.Match(sp.text).Groups[1].Value}m/min"); basicAt ??= sp; }
        if (First(Pitch) is { } pt) { var m = Pitch.Match(pt.text); basic.Add($"피치 {m.Groups[1].Value}{m.Groups[2].Value}"); basicAt ??= pt; }
        if (First(Cycle) is { } ct) { basic.Add($"사이클타임 {Cycle.Match(ct.text).Groups[1].Value}초"); basicAt ??= ct; }
        if (basic.Count > 0 && basicAt is { } ba) v["basic"] = At(ba, string.Join(" / ", basic));

        // 로봇: 용도별 대수(같은 용도는 처음 나온 값)
        var robots = new Dictionary<string, int>();
        (Block b, string text)? robotAt = null;
        foreach (var x in lines)
            foreach (Match m in RobotCount.Matches(x.text))
            {
                var kind = m.Groups[1].Value is "도장" ? "스프레이" : m.Groups[1].Value;
                if (robots.TryAdd(kind, int.Parse(m.Groups[2].Value))) robotAt ??= x;
            }
        if (robots.Count > 0 && robotAt is { } ra)
            v["robot"] = At(ra, string.Join(", ", robots.Select(r => $"{r.Key}: {r.Value}대")) + (robots.Count > 1 ? $" (총 {robots.Values.Sum()}대)" : ""));

        // 유틸리티: 전원 + 압축 공기
        var util = new List<string>();
        (Block b, string text)? utilAt = null;
        if (First(Voltage) is { } vo) { var m = Voltage.Match(vo.text); util.Add($"Power: AC {m.Groups[1].Value}V × {m.Groups[2].Value}Hz"); utilAt = vo; }
        var airLine = lines.FirstOrDefault(x => Regex.IsMatch(x.text, "압축|에어|air", RegexOptions.IgnoreCase) && Air.IsMatch(x.text));
        if (airLine.text is not null) { var m = Air.Match(airLine.text); util.Add($"Compressed Air: {m.Groups[1].Value.Replace(" ", "")} {m.Groups[2].Value}"); utilAt ??= airLine; }
        if (util.Count > 0 && utilAt is { } ua) v["utility"] = At(ua, string.Join(" / ", util));

        if (First(RailLen) is { } rl) { var m = RailLen.Match(rl.text); v["rail"] = At(rl, $"레일 길이 {G(m, 1, 2)}m"); }

        // 도장기: 특징 규칙(PPH707·정전벨 …) 중 본문에 나온 것
        var atom = SpecFeatures.From("", string.Join("\n", lines.Where(x => Regex.IsMatch(x.text, "도장기|atomizer|벨|bell", RegexOptions.IgnoreCase)).Select(x => x.text).Take(40)));
        var atomLine = lines.FirstOrDefault(x => Regex.IsMatch(x.text, "도장기|atomizer", RegexOptions.IgnoreCase));
        if ((atom.Atomizers.Count > 0 || atom.Paints.Count > 0) && atomLine.text is not null)
            v["atomizer"] = At(atomLine, string.Join(" · ", atom.Atomizers.Concat(atom.Paints)));

        // 칼라(CCV): 칼라 수 + 스페어
        var colorLine = lines.FirstOrDefault(x => Regex.IsMatch(x.text, "CCV|칼라|컬러", RegexOptions.IgnoreCase) && Colors.IsMatch(x.text));
        if (colorLine.text is not null)
        {
            var m = Colors.Match(colorLine.text);
            var spare = ColorSpare.Match(colorLine.text);
            v["color"] = At(colorLine, $"{G(m, 1, 2)} 칼라" + (spare.Success ? $" + 스페어 {spare.Groups[1].Value}" : "")
                                         + (Regex.IsMatch(colorLine.text, "듀얼|dual", RegexOptions.IgnoreCase) ? " (듀얼 CCV)" : ""));
        }

        if (First(Plc) is { } pl) v["system"] = At(pl, "PLC: " + Plc.Match(pl.text).Groups[1].Value.ToUpperInvariant());
        if (First(Sop) is { } so) v["schedule"] = At(so, "SOP " + Regex.Replace(Sop.Match(so.text).Groups[1].Value, @"\s+", ""));

        // 양산 대기: 합계 M/D(가장 큰 값)
        var mds = lines.SelectMany(x => Md.Matches(x.text).Select(m => (x, n: int.Parse(G(m, 1, 2))))).Where(t => t.n is >= 5 and <= 2000).ToList();
        if (mds.Count > 0) { var best = mds.MaxBy(t => t.n); v["standby"] = At(best.x, $"총 {best.n} M/D"); }

        if (First(Training) is { } tr) { var m = Training.Match(tr.text); v["training"] = At(tr, $"교육 {m.Groups[1].Value}{m.Groups[2].Value}"); }
        if (First(Car) is { } car) v["body"] = At(car, "차종: " + Trim(Car.Match(car.text).Groups[1].Value.Split('|')[0], 100));
        return v;
    }

    private static string G(Match m, params int[] groups) =>
        groups.Select(g => m.Groups[g]).FirstOrDefault(g => g.Success && g.Value.Length > 0)?.Value ?? "";

    private static string Trim(string s, int max = 160) => s.Length <= max ? s.Trim() : s[..max].Trim() + "…";
}
