using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 영업부 사양정리에 프로젝트마다 더 붙는 줄(화성4 ver01: 고정방식·Tracking rail·오프너 클리너·Vision & MIP·에어 제습기).
/// 사양서에 그 내용이 있을 때만 줄을 만들고(Others 앞), 없으면 만들지 않는다(2026-10-28 사용자). 값은 사양서 말을 짧게, 근거 원문은 Source.
/// 여러 공정 초안의 고정방식은 ProcessMatrix가 공정마다 채운다(여기서는 비어 있을 때만).
/// </summary>
public static class ExtraRows
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly (string label, Regex re)[] Mounts =
    [
        ("하프 벽걸이", new(@"하프\s*(?:월|벽걸이)|HALF\s*(?:WALL|벽걸이)|벽걸이\s*(?:형|고정|타입)", Opt)),
        ("7축 레일", new(@"7\s*축\s*(?:로봇|레일)|주행\s*축\s*포함|레일\s*하우스\s*포함", Opt)),
        ("선반형", new(@"선반\s*(?:형|타입)|SHELF\s*(?:TYPE|MOUNT)", Opt)),
        ("플로어", new(@"플로어\s*(?:형|타입|마운트)|FLOOR\s*(?:TYPE|MOUNT)", Opt)),
    ];
    private static readonly Regex Rail = new(@"(?:주행|트래킹|TRACKING)\s*(?:레일|RAIL)[^.。|]{0,25}?(\d{1,2}(?:[.,]\d{1,3})?)\s*(m|mm)\b|레일\s*길이\s*[:：]?\s*(\d{1,2}(?:[.,]\d{1,3})?)\s*(m|mm)\b", Opt);
    private static readonly Regex RailRc = new(@"\b(\d)\s*R\s*(\d)\s*C\b", Opt);
    private static readonly Regex OpenerCleaner = new(@"오프너\s*(?:클리너|세정\s*(?:장치|기|함)?)|벨\s*컵\s*/\s*오프너\s*세정|OPENER\s*CLEAN", Opt);
    private static readonly Regex Vision = new(@"차종\s*(?:감지|인식|판별)\s*(?:비\s*[전젼]|VISION|장치|시스템)?|차종\s*비\s*[전젼]|VISION\s*SYSTEM", Opt);
    private static readonly Regex Mip = new(@"\bMIP\b|수동\s*입력\s*반", Opt);
    private static readonly Regex Dryer = new(@"제습\s*(?:기|장치)|DEHUMIDIF|에어\s*드라이어|AIR\s*DRYER", Opt);
    private static readonly Regex Count = new(@"(\d{1,3})\s*(?:SET|세트|EA|대|개소)\b", Opt);

    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        var lines = blocks.Select(b => (b, t: Regex.Replace(b.Text, @"\s+", " ").Trim()))
            // 목차와 과거 문제·개선 사례표(FMEA "[문제] … [개선] …")는 이번 설비 요구가 아니라 뺀다
            .Where(x => x.t.Length > 0 && !Regex.IsMatch(x.t, @"목\s*차|CONTENTS|\[\s*문제\s*\]|\[\s*개선\s*\]|FMEA", RegexOptions.IgnoreCase)).ToList();
        string Short(string t) => t.Length <= 90 ? t : t[..90] + "…";

        // 고정방식: 사양서가 로봇 고정 형식을 말할 때(하프 벽걸이·7축 레일·선반형·플로어)
        if (!d.Rows.Any(r => r.Key == "고정방식"))
        {
            var found = Mounts.Select(m => (m.label, hit: lines.FirstOrDefault(x => m.re.IsMatch(x.t)))).Where(x => x.hit.t is not null).ToList();
            if (found.Count > 0)
            {
                var row = DraftLayout.EnsureRow(d, "고정방식");
                row.Value = string.Join(" / ", found.Select(f => f.label));
                row.Pages = found.Select(f => f.hit.b.UnitIndex).Distinct().Order().ToList();
                row.Source = found.Select(f => $"{f.label} ← {Short(f.hit.t)} (p{f.hit.b.UnitIndex})").ToList();
            }
        }

        // Tracking rail: 레일 길이(+nRnC). 있으면 Robot 기타 사항의 '주행레일'은 이 줄로 옮긴다
        if (lines.Select(x => (x.b, x.t, m: Rail.Match(x.t))).FirstOrDefault(x => x.m.Success) is { m: not null } rh)
        {
            var len = rh.m.Groups[1].Success ? $"{rh.m.Groups[1].Value}{rh.m.Groups[2].Value}" : $"{rh.m.Groups[3].Value}{rh.m.Groups[4].Value}";
            var rc = lines.Where(x => x.b.UnitIndex == rh.b.UnitIndex).Select(x => RailRc.Match(x.t)).FirstOrDefault(m => m.Success);
            var row = DraftLayout.EnsureRow(d, "Tracking rail");
            row.Value = len + (rc is { Success: true } ? $" / {rc.Groups[1].Value}R{rc.Groups[2].Value}C" : "");
            row.Pages = [rh.b.UnitIndex];
            row.Source = [$"{row.Value} ← {Short(rh.t)} (p{rh.b.UnitIndex})"];
            var robot = d.Rows.First(r => r.Key == "Robot");
            robot.Note = string.Join("\n", robot.Note.Split('\n').Where(n => !n.StartsWith("주행레일")));
        }

        // 오프너 클리너(오프너 세정장치)
        if (lines.FirstOrDefault(x => OpenerCleaner.IsMatch(x.t)) is { t: not null } oc)
        {
            var row = DraftLayout.EnsureRow(d, "오프너 클리너");
            var qty = Count.Match(oc.t[OpenerCleaner.Match(oc.t).Index..]);
            row.Value = "오프너 세정장치" + (qty.Success && !Regex.IsMatch(oc.t, @"벨\s*컵\s*/\s*오프너") ? $" : {qty.Groups[1].Value}" : "");
            if (Regex.IsMatch(oc.t, @"벨\s*컵\s*/\s*오프너")) row.Note = "벨컵·오프너 세정장치 통합 수량" + (qty.Success ? $" {qty.Groups[1].Value}대" : "");
            row.Pages = [oc.b.UnitIndex];
            row.Source = [$"{(row.Note.Length > 0 ? row.Note : row.Value)} ← {Short(oc.t)} (p{oc.b.UnitIndex})"];
        }

        // Vision & MIP: 차종감지(비전)·MIP·수동입력반. 있으면 Others의 '차종 비전'은 이 줄로 옮긴다
        var vis = lines.FirstOrDefault(x => Vision.IsMatch(x.t));
        var mip = lines.FirstOrDefault(x => Mip.IsMatch(x.t));
        if (vis.t is not null || mip.t is not null)
        {
            var row = DraftLayout.EnsureRow(d, "Vision & MIP");
            var parts = new List<string>();
            string Qty((Block b, string t) x, Regex re) => Count.Match(x.t[re.Match(x.t).Index..]) is { Success: true } q && q.Index < 40 ? $" : {q.Groups[1].Value}" : "";
            if (vis.t is not null) parts.Add((Regex.IsMatch(vis.t, @"비\s*[전젼]|VISION", RegexOptions.IgnoreCase) ? "차종감지 비전" : "차종감지") + Qty(vis, Vision));
            if (mip.t is not null) parts.Add((Regex.IsMatch(mip.t, @"\bMIP\b", RegexOptions.IgnoreCase) ? "MIP" : "수동입력반") + Qty(mip, Mip));
            row.Value = string.Join(" / ", parts);
            row.Pages = new[] { vis.b, mip.b }.OfType<Block>().Select(b => b.UnitIndex).Distinct().Order().ToList();
            row.Source = new[] { vis, mip }.Where(x => x.t is not null).Select(x => $"{row.Value} ← {Short(x.t)} (p{x.b.UnitIndex})").ToList();
            var others = d.Rows.First(r => r.Key == "Others");
            others.Value = string.Join(" / ", others.Value.Split(" / ").Where(v => v.Length > 0 && !v.StartsWith("차종 비전")));
        }

        // 에어 제습기
        if (lines.FirstOrDefault(x => Dryer.IsMatch(x.t)) is { t: not null } dr)
        {
            var row = DraftLayout.EnsureRow(d, "에어 제습기");
            var at = dr.t[Dryer.Match(dr.t).Index..];
            row.Value = "에어 제습기" + (Count.Match(at) is { Success: true } q && q.Index < 40 ? $" : {q.Groups[1].Value}" : "")
                + (Regex.IsMatch(at, @"바이\s*패스", RegexOptions.IgnoreCase) ? " (바이패스 가능)" : "");
            row.Pages = [dr.b.UnitIndex];
            row.Source = [$"{row.Value} ← {Short(dr.t)} (p{dr.b.UnitIndex})"];
        }
    }
}
