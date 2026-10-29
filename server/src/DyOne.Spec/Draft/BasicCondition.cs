using System.Globalization;
using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 2장 'Basic Condition' 칸: 생산 능력(UPH/JPH) · 컨베이어 방식·속도 · 바디 피치 · 사이클타임.
/// 영업부 양식: "72 UPH (MAX 75 UPH) / 연속 컨베이어 8.28m/min / 바디 피치 6.9m / 사이클타임 48초".
/// 사이클타임이 사양서에 없으면 최대 UPH로 계산(3600 ÷ 최대 UPH)하고 기타 사항에 "계산"이라고 적는다
/// — 로봇은 가장 빠른 조건에서도 칠해야 하므로 설계·티칭 기준은 최대 UPH(영업부 사양정리와 같은 규칙).
/// </summary>
public static class BasicCondition
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Rate = new(@"(\d{2,3}(?:\.\d+)?)\s*(UPH|JPH)", Opt);
    // "MAX : 75UPH", "최대 운영 가능 : 75 UPH", "75UPH 기준(최대 운영)"
    private static readonly Regex MaxRate = new(@"(?:MAX|최대)[^|\n\d]{0,15}(\d{2,3}(?:\.\d+)?)\s*(?:UPH|JPH)|(\d{2,3}(?:\.\d+)?)\s*(?:UPH|JPH)[^|\n]{0,10}(?:최대|MAX)", Opt);
    private static readonly Regex Conveyor = new(@"(연속|간헐|STOP\s*&\s*GO|스톱\s*앤?\s*고|CONTINUOUS|INDEX(?:ING)?)\s*(?:식\s*)?(?:콘|컨)?\s*베이?어|(?:콘|컨)\s*베이?어\s*[:：]?\s*(연속|간헐)|(CONTINUOUS|STOP\s*&\s*GO)\s*CONVEYOR", Opt);
    private static readonly Regex Speed = new(@"(\d+(?:\.\d+)?)\s*m\s*/\s*min", Opt);
    private static readonly Regex Pitch = new(@"피\s*치\s*[:：]?\s*(\d[\d,]*(?:\.\d+)?)\s*(mm|m)\b|(?:BODY\s*)?PITCH\s*[:：]?\s*(\d[\d,]*(?:\.\d+)?)\s*(mm|m)\b", Opt);
    private static readonly Regex PitchNot = new(@"레일|광축|라이트\s*커튼|나사|볼트|LM", Opt);
    private static readonly Regex Cycle = new(@"(?:사이클\s*타임|싸이클\s*타임|CYCLE\s*TIME|C\s*/\s*T(?:IME)?|택트\s*타임|TACT\s*TIME)\s*[:：]?\s*(\d+(?:\.\d+)?)\s*(초|sec|s)", Opt);
    private static readonly Regex CycleNot = new(@"칼라\s*체인지|COLOR\s*CHANGE|세정", Opt);

    /// <summary>
    /// Basic Condition 칸 채우기. onlyMissing=true(AI 초안)면 값이 이미 있을 때 빠진 사이클타임만 덧붙인다.
    /// </summary>
    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks, bool onlyMissing = false)
    {
        var row = d.Rows.First(r => r.Key == "Basic Condition");
        var lines = blocks.Select(b => (b, t: Regex.Replace(b.Text, @"\s+", " "))).Where(x => x.t.Length > 0).ToList();
        var pages = new SortedSet<int>();

        // 생산 능력: 최대와 일반(운영)
        double? max = null, normal = null;
        var unit = "UPH";
        foreach (var x in lines)
            foreach (Match m in MaxRate.Matches(x.t))
            {
                var n = Num(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (n is >= 5 and <= 120 && (max is null || n > max)) { max = n; pages.Add(x.b.UnitIndex); }
            }
        foreach (var x in lines)
            foreach (Match m in Rate.Matches(x.t))
            {
                var n = Num(m.Groups[1].Value);
                unit = m.Groups[2].Value.ToUpperInvariant();
                if (n is >= 5 and <= 120 && n != max && normal is null) { normal = n; pages.Add(x.b.UnitIndex); }
            }
        if (max is null && normal is not null) (max, normal) = (normal, null);
        if (normal > max) (max, normal) = (normal, max);

        var parts = new List<string>();
        if (max is { } mx)
            parts.Add(normal is { } nm ? $"{F(nm)} {unit} (MAX {F(mx)} {unit})" : $"최대 {F(mx)} {unit}");

        // 컨베이어: 방식 + 속도 (같은 줄 우선)
        var conv = lines.FirstOrDefault(x => Conveyor.IsMatch(x.t));
        var speedLine = lines.FirstOrDefault(x => Speed.IsMatch(x.t) && Regex.IsMatch(x.t, @"(?:콘|컨)\s*베이?어|C\s*/\s*V|CONVEYOR|속\s*도", RegexOptions.IgnoreCase))
            is { t: not null } s1 ? s1 : lines.FirstOrDefault(x => Speed.IsMatch(x.t));
        var convText = "";
        if (conv.t is not null)
        {
            var m = Conveyor.Match(conv.t);
            var kind = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).ToUpperInvariant();
            convText = kind.StartsWith("연속") || kind.StartsWith("CONTINUOUS") ? "연속 컨베이어" : kind.StartsWith("간헐") || kind.StartsWith("INDEX") ? "간헐 컨베이어" : "Stop & Go 컨베이어";
            pages.Add(conv.b.UnitIndex);
        }
        double? speed = null;
        if (speedLine.t is not null)
        {
            var raw = Speed.Match(speedLine.t).Groups[1].Value; // 사양서 숫자 그대로(8.28을 8.3으로 반올림하지 않음)
            speed = Num(raw);
            convText = (convText.Length > 0 ? convText : "컨베이어") + $" {raw}m/min";
            pages.Add(speedLine.b.UnitIndex);
        }
        if (convText.Length > 0) parts.Add(convText);

        // 바디 피치: 레일 고정 피치·라이트커튼 광축 피치 등은 뺀다
        double? pitchM = null;
        if (lines.FirstOrDefault(x => Pitch.IsMatch(x.t) && !PitchNot.IsMatch(x.t)) is { t: not null } px)
        {
            var m = Pitch.Match(px.t);
            var (num, u) = m.Groups[1].Success ? (m.Groups[1].Value, m.Groups[2].Value) : (m.Groups[3].Value, m.Groups[4].Value);
            var v = Num(num.Replace(",", ""));
            pitchM = u.Equals("mm", StringComparison.OrdinalIgnoreCase) ? v / 1000 : v;
            parts.Add($"바디 피치 {num}{u.ToLowerInvariant()}");
            pages.Add(px.b.UnitIndex);
        }

        // 사이클타임: 사양서 값 → 없으면 최대 UPH로 계산
        string? cycleNote = null;
        // 칼라체인지·세정 시간("칼라체인지 C/T 12초")은 뺀다 — 값 앞 20자만 본다("C/TIME 54.1초 이내(스타트~세정/칼라체인지)"는 사이클타임)
        static Match? CycleOf(string t) =>
            Cycle.Matches(t).FirstOrDefault(m => !CycleNot.IsMatch(t[Math.Max(0, m.Index - 20)..(m.Index + m.Length)]));
        if (lines.Select(x => (x.b, m: CycleOf(x.t))).FirstOrDefault(x => x.m is not null) is { m: { } cm } cx)
        {
            parts.Add($"사이클타임 {cm.Groups[1].Value}초");
            pages.Add(cx.b.UnitIndex);
        }
        else if (max is { } m2)
        {
            parts.Add($"사이클타임 {F(3600 / m2)}초");
            cycleNote = $"사이클타임은 MAX {F(m2)} {unit} 기준 계산 (3600 ÷ {F(m2)})";
            // 검산: 피치 ÷ 컨베이어 속도 = 운영 UPH의 사이클
            if (pitchM is { } p && speed is { } sp && sp > 0)
                cycleNote += $" / 컨베이어 검산 {F(p / sp * 60)}초 = {(normal is { } n2 ? $"{F(n2)} {unit}" : "운영")} 기준";
        }

        // 컨베이어 속도가 사양서에 없고 연속 컨베이어면 UPH × 피치 ÷ 60으로 계산해 기타 사항에(값 칸에는 넣지 않음, 2026-10-27 사용자)
        // 운영 UPH가 있으면 운영 UPH로(아산: 72 UPH × 6.9m ÷ 60 = 사양서 8.28m/min과 같음)
        if (speed is null && convText.StartsWith("연속") && pitchM is { } pm && (normal ?? max) is { } rate)
        {
            var speedNote = $"컨베이어 속도 약 {(rate * pm / 60).ToString("0.##", CultureInfo.InvariantCulture)}m/min (계산: {F(rate)} {unit} × {F(pm)}m ÷ 60, 사양서에 속도 없음)";
            cycleNote = cycleNote is null ? speedNote : $"{cycleNote}\n{speedNote}";
        }

        if (parts.Count == 0) return;
        var value = string.Join(" / ", parts);
        if (onlyMissing && row.Value.Length > 0)
        {
            if (!Regex.IsMatch(row.Value, @"사이클|CYCLE|C\s*/\s*T", RegexOptions.IgnoreCase) && parts.FirstOrDefault(p => p.StartsWith("사이클")) is { } c)
            {
                row.Value += $" / {c}";
                if (cycleNote is not null) foreach (var l in cycleNote.Split('\n')) row.AddRemark(l);
            }
            return;
        }
        row.Value = value;
        // 계산·검산은 사양서 말이 아니라 참고 사항(pptx에는 안 들어감)
        row.Note = "";
        row.Remark = "";
        if (cycleNote is not null) foreach (var l in cycleNote.Split('\n')) row.AddRemark(l);
        row.Pages = pages.ToList();
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
