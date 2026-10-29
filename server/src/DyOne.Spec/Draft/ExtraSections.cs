using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 PCS 사항·제공 항목 장(규칙): 과거 사양정리 69건 분석(2026-10-28) — PCS 줄의 절반, 제공 항목 줄의 절반 이상이 고객 사양서 문장을 거의 그대로 옮긴 것.
///  - PCS: 건드롭·지관·FPR·폐신너/폐순수·덤프 배관·Two-pipe·도료 공급 항목 중 '~할 것·설치·철거·금지·반영' 같은 요구 문장
///  - 제공 항목: 스페어·비상대응 부품·특수공구·스페셜 툴·키락/OLP·로봇 커버·백업 메모리·교육센터 기간 중 수량이나 '제공·지급'이 있는 문장
/// 사양서 문장을 짧게(번호·● 표시·표 칸 빼고) 쪽 번호와 함께. 현장 판단(재활용·교체 필요)이나 협의 결과는 사람이 더한다 — 기타 장은 회의·협의라 채우지 않는다.
/// 2차(같은 날, 과거 13건 영업부 줄과 비교): 한 칸에 여러 요구가 붙은 글은 요구별로 나누고(①·-·※·'1EA, '),
/// 표의 병합된 항목 칸('비상 대응 관련')은 아래 줄까지 이어 보고, 모든 사양서에 있는 공통 문구(스페어 15년 공급·정품 라이센스 원칙)는 뺀다.
/// </summary>
public static class ExtraSections
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Pcs = new(@"\bPCS\b|건\s*드[롭랍]|지\s*관|\bFPR\b|폐\s*(?:신[너나]|순수|세정제)|덤프\s*(?:배관|헤더)|TWO[-\s]?PIPE|3RD\s*LINE|도료\s*(?:공급|배관|라인)|순수\s*라인|드[롭랍]\s*(?:박스|포인트)|세정액\s*회수|PAINT\s*SUPPLY|CIRCULATION|THINNER\s*LEAK|믹싱\s*룸", Opt);
    private static readonly Regex Must = new(@"할\s*것|하여야|해야|반영|설치|신설|철거|교체|금지|포함|구성|연결|이설|개조|적용|SHALL|INSTALL|PROVIDE|MUST", Opt);
    private static readonly Regex Supply = new(@"스페어|SPARE|비상\s*(?:대응|상황)\s*(?:용|파트|부품)?|특수\s*공구|스페셜\s*툴|SPECIAL\s*TOOL|정비\s*공구|(?:분해|조립|메인트)[^.。|]{0,8}공구|공구\s*(?:함|세트)|키\s*락|라이[센선]스|모토심|\bOLP\b|로[봇보]트?\s*커버|ROBOT\s*COVER|(?:데이터|메모리|CF\s*카드)[^.。|]{0,12}[백빽]\s*업|[백빽]\s*업\s*(?:메모리|하드|용)|메모리\s*(?:카드)?|교육\s*센[터타]|서보\s*모터\s*\d|도장기\s*\d+\s*EA|도징\s*펌프\s*\d|FGP\s*모터|벨\s*컵\s*\d+\s*(?:EA|개)", Opt);
    private static readonly Regex Given = new(@"\d+\s*(?:EA|SET|SETS|세트|개|벌|식|주)\b|제공|지급|공급|견적에\s*포함|PROVIDE|SUPPLY", Opt);
    // 표 칸 표시·번호·FMEA(과거 문제/개선) 같은 이번 요구가 아닌 줄
    private static readonly Regex Noise = new(@"\[\s*문제\s*\]|\[\s*개선\s*\]|FMEA|목\s*차|CONTENTS|편의\s*시설|^\s*항\s*목\s*사\s*양|세\s*부\s*사\s*양\s*비\s*고", Opt);
    // 수량("2set", "1EA", "4세트")
    private static readonly Regex Qty = new(@"\d+\s*(?:EA|SET|SETS|세트|개|벌|식)\b", Opt);
    // 거의 모든 사양서에 들어 있는 공통 문구 — 영업부 사양정리 13건 어디에도 옮기지 않았다(기한·정품·리스트·단가·무상 교환·기본 연결 작업)
    private static readonly Regex Boiler = new(@"\d+\s*년\s*(?:이상|간)|공급\s*기한|단종|정품|라이[센선]스\s*공급을\s*원칙|풀\s*리스트|리스트를|단가|무상\s*교환|공급\s*중단|여유를?\s*확보|도료\s*지관과\s*호스\s*연결\s*작업", Opt);

    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks)
    {
        if (d.Pcs.Count == 0) d.Pcs = Pick(blocks, Pcs, (t, _) => Must.IsMatch(t), 14);
        if (d.Supply.Count == 0)
            // 칼라 스페어("스페어 6color")·CCV 스페어는 도료 칼라 수라 빼고, 수량·제공 말이 있을 때만
            // 항목 칸이 '비상 대응'·'스페어'인 표 줄은 수량 말이 없어도 품목("FGP모터 A'ssy")
            d.Supply = Pick(blocks, Supply, (t, label) => (Given.IsMatch(t) || Supply.IsMatch(label)) && !Regex.IsMatch(t, @"칼라|컬러|COLOR|CCV", RegexOptions.IgnoreCase), 14);
    }

    private static List<DraftCheck> Pick(IReadOnlyList<Block> blocks, Regex topic, Func<string, string, bool> ok, int max)
    {
        var seen = new HashSet<string>();
        var list = new List<DraftCheck>();
        // 표의 항목 칸이 병합돼 있으면 아래 줄은 항목 칸이 비어 있다 — 같은 표의 마지막 항목 이름을 이어 쓴다
        var carry = new Dictionary<(int, int), string>();
        Block? last = null, before = null;
        foreach (var b in blocks)
        {
            var label = Label(b);
            if (b.Table > 0)
            {
                if (Regex.IsMatch(label, @"[가-힣A-Za-z]{2}")) carry[(b.UnitIndex, b.Table)] = label;
                else if (carry.TryGetValue((b.UnitIndex, b.Table), out var up)) label = up;
            }
            var whole = Regex.Replace(b.Text, @"\s+", " ");
            var prior = before; before = b;
            if (Noise.IsMatch(whole) || !topic.IsMatch(whole) && !topic.IsMatch(label)) continue;
            foreach (var text in Items(Content(b)))
            {
                // 표 한 장이 통째로 한 칸에 들어온 글(부스 사양표 등)은 요구 문장이 아니다
                if (text.Length < 5 || text.Length > 160 || Boiler.IsMatch(text)) continue;
                if (!topic.IsMatch(text) && !topic.IsMatch(label) || !ok(text, label)) continue;
                var key = Regex.Replace(text, @"[^0-9A-Za-z가-힣]", "");
                if (key.Length < 6 || !seen.Add(key)) continue;
                // 같은 요구가 여러 쪽에 되풀이되면 한 번만 — 글자 3-gram 60% 이상 겹치면 같은 줄
                // 표의 바로 다음 줄에 짧은 수량 세부만 있으면("도장기 메인트 : 2set") 위 줄 항목의 괄호에 붙인다
                if (text.Length < 30 && Qty.IsMatch(text) && b.Table > 0 && prior is { } pb && pb.UnitIndex == b.UnitIndex && pb.Table == b.Table && b.Row - pb.Row == 1 && list.Count > 0
                    && (last == pb || Similar(list[^1].Text, Content(pb)) >= 0.6))
                {
                    var prev = list[^1].Text;
                    list[^1] = new DraftCheck(prev.EndsWith(')') && prev.Contains(" (") && Qty.IsMatch(prev[prev.LastIndexOf(" (")..]) ? prev[..^1] + ", " + text + ")" : $"{prev} ({text})", list[^1].Pages);
                    last = b;
                    continue;
                }
                if (list.Any(x => Similar(x.Text, text) >= 0.6)) continue;
                list.Add(new DraftCheck(text, [b.UnitIndex]));
                last = b;
                if (list.Count >= max) return list;
            }
        }
        return list;
    }

    // 한 칸 안의 여러 요구를 나눈다: ①② 번호, ' - '·' ※ ' 글머리, '(1)' 번호.
    // 짧은 글머리 줄(30자 미만, "도장기 메인트 : 2set", "메모리 2EA/로봇별")은 앞 요구의 세부라 괄호로 붙인다(영업부 "스페셜 툴 (도장기 메인트 : 2set, …)").
    // 쉼표로 이은 수량 목록("서보모터 1EA, 도장기 1EA, 도징펌프 1EA")은 영업부처럼 한 줄씩.
    private static IEnumerable<string> Items(string text)
    {
        var heads = new List<(string Head, List<string> Subs)>();
        foreach (var p in Regex.Split(text, @"\s+(?=[①-⑳])|\s+[-•※▪□■]\s+|\s+(?=\(\d{1,2}\)\s)"))
        {
            var t = Regex.Replace(p, @"^(?:[①-⑳]|\(\d{1,2}\)|\d{1,2}[).]|[-•·※▪□■_]\s*)+\s*", "").Trim(' ', ',', '.', '_');
            if (t.Length == 0) continue;
            if (t.Length < 30 && Qty.IsMatch(t) && heads.Count > 0 && heads[^1].Head.Length >= 15) heads[^1].Subs.Add(t);
            else heads.Add((t, []));
        }
        foreach (var (head, subs) in heads)
        {
            if (subs.Count > 0) { yield return $"{head} ({string.Join(", ", subs)})"; continue; }
            foreach (var q in Regex.Split(head, @"(?<=(?:EA|SET|세트|개)\b\s*),\s*(?=[^,]*\d+\s*(?:EA|SET|세트|개)\b|[^,]{3,40}A['’]?SSY)", RegexOptions.IgnoreCase))
                if (q.Trim().Length > 0) yield return q.Trim();
        }
    }

    private static double Similar(string a, string b)
    {
        static HashSet<string> G(string s) { s = Regex.Replace(s, @"[^0-9A-Za-z가-힣]", ""); return [.. Enumerable.Range(0, Math.Max(0, s.Length - 2)).Select(i => s.Substring(i, 3))]; }
        var x = G(a); var y = G(b);
        return x.Count == 0 || y.Count == 0 ? 0 : (double)x.Intersect(y).Count() / Math.Min(x.Count, y.Count);
    }

    // 표 줄의 내용 칸: 번호·●·확인란 칸을 빼고 가장 긴 칸
    private static string Content(Block b)
    {
        var cells = b.Cells.Select(c => Regex.Replace(c.Replace("\n", " "), @"\s+", " ").Trim())
            .Where(c => c.Length > 0 && !Regex.IsMatch(c, @"^(?:[●○◎OX×✓\-]|\d{1,2}|승\s*인|검\s*수|완\s*료)$", RegexOptions.IgnoreCase)).ToList();
        return cells.OrderByDescending(c => c.Length).FirstOrDefault() ?? "";
    }

    // 줄 머리 칸(앞 두 칸의 짧은 항목 이름, 예: "PCS 배관", "비상 대응 관련") — 번호 칸·내용 칸은 빼고
    private static string Label(Block b) => b.Table == 0 ? "" : string.Join(" ", b.Cells.Take(2).Select(c => c.Replace("\n", " ").Trim())
        .Where(c => c.Length is > 0 and <= 25 && !Regex.IsMatch(c, @"^\d{1,2}$"))).Trim();
}
