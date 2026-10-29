using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// Utility·System 칸을 사양서 표기 그대로 다듬는다(SpecValues는 비교용으로 줄여 읽음).
///  - Utility 전원: 사양서 식 그대로 "AC440V±10%)×3Φ ×(60Hz±5%)" (영업부: "AC 440V(±10%)×3Φ×(60Hz±5%)") — 줄이지 않는다.
///  - System PLC: 사양서 "PLC : …" 항목이 있으면 그 말 그대로(업체제안 등). PLC 메이커가 아니라 통신 카드(MELSEC NET-H 카드 준비)만
///    나온 경우는 값이 아니라 기타 사항에(애매한 값은 기타 사항 — 2026-10-27 사용자).
/// </summary>
public static class UtilitySystem
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    // 전원 식 원문 구간: "AC440V±10%)×3Φ ×(60Hz±5%)", "AC 400V × 50Hz"
    private static readonly Regex Power = new(@"\(?\s*AC\s*\d{3}\s*V.{0,60}?\d{2}\s*Hz[^)●|]{0,8}\)?", Opt);
    private static readonly Regex PlcItem = new(@"PLC\s*[:：]\s*([^●|\n]{2,40}?)\s*(?:[●|]|$)", Opt);
    private static readonly Regex Maker = new(@"MITSUBISHI|미쓰비시|미쯔비시|MELSEC-?Q|SIEMENS|지멘스|S7-\d+|LS\s*(?:PLC|산전)|XGT|ALLEN[-\s]?BRADLEY|ROCKWELL|OMRON|업체\s*제안", Opt);
    private static readonly Regex NetCard = new(@"MELSEC\s*NET[-\s]?\w*|CC-?LINK|PROFINET|ETHERNET\s*/?\s*IP", Opt);

    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var lines = blocks.Select(b => (b, t: Regex.Replace(b.Text, @"\s+", " ").Trim())).Where(x => x.t.Length > 0).ToList();

        var util = d.Rows.First(r => r.Key == "Utility");
        if (!util.Edited && util.Value.StartsWith("Power:") && lines.FirstOrDefault(x => Power.IsMatch(x.t)) is { t: not null } pw)
        {
            util.Value = Regex.Replace(util.Value, @"^Power:[^/]*", $"Power: {Power.Match(pw.t).Value.Trim()} ");
            if (!util.Pages.Contains(pw.b.UnitIndex)) util.Pages = [.. util.Pages.Append(pw.b.UnitIndex).Order()];
        }

        var sys = d.Rows.First(r => r.Key == "System");
        if (sys.Edited) return;
        var item = lines.Select(x => (x.b, m: PlcItem.Match(x.t))).FirstOrDefault(x => x.m.Success && Maker.IsMatch(x.m.Groups[1].Value));
        var net = lines.FirstOrDefault(x => NetCard.IsMatch(x.t));
        // 값이 통신 카드 이름뿐이면(PLC: MELSEC NET) 값에서 빼고 기타 사항으로
        var onlyNet = sys.Value.Length > 0 && NetCard.IsMatch(sys.Value) && !Regex.IsMatch(Regex.Replace(sys.Value, NetCard.ToString(), "", RegexOptions.IgnoreCase), Maker.ToString(), RegexOptions.IgnoreCase);
        if (item.m is { Success: true } im)
        {
            sys.Value = $"PLC: {Regex.Replace(im.Groups[1].Value.Trim(), @"\s+", " ")}";
            sys.Pages = [.. sys.Pages.Append(item.b.UnitIndex).Distinct().Order()];
        }
        else if (onlyNet) sys.Value = "";
        if (net.t is not null && (onlyNet || item.m is { Success: true }))
        {
            var name = NetCard.Match(net.t).Value.Trim();
            var note = (Regex.IsMatch(name, "MELSEC", RegexOptions.IgnoreCase) ? $"미쓰비시 PLC 네트워크({name}) 통신 요구" : $"{name} 통신 요구");
            if (!(item.m is { Success: true } im2 && !Regex.IsMatch(im2.Groups[1].Value, @"업체\s*제안")))
                sys.AddRemark($"PLC 메이커가 사양서에 확정되지 않음 — {name} 요구로 미쓰비시 가능성, 확인");
            if (!sys.Note.Contains(name, StringComparison.OrdinalIgnoreCase))
                sys.Note = string.Join("\n", new[] { sys.Note, note }.Where(s => s.Length > 0));
            var src = net.t.Length > 90 ? net.t[..90] + "…" : net.t;
            sys.Source = [.. sys.Source, $"{note} ← {src} (p{net.b.UnitIndex})"];
            if (!sys.Pages.Contains(net.b.UnitIndex)) sys.Pages = [.. sys.Pages.Append(net.b.UnitIndex).Order()];
        }
    }
}
