using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>
/// 사양정리 2장의 설비 줄(규칙): Color(CCV)·Bell Cleaner. 사양서 한 건에 맞추지 않고 모든 사양서에 같은 형식으로 적는다.
///  - Color(CCV): 영업부 양식 "CCV : 24개 (20칼라 + 스페어 4)", 기타 사항 "Dual Circuit 적용".
///    칼라 수가 여러 군데 나오면 CCV 줄(칼라수·스페어가 함께 적힌 줄)을 먼저 본다(예: 아산 V5 p10 "듀얼 CCV 적용 (칼라수 : 20EA / 스페어 : 4EA)").
///  - Bell Cleaner: 사양서에 벨컵 세정장치 요구가 있을 때만 채운다(없으면 비워 둔다 — 넣지 않는다). 모델은 ModelPicker가 제안.
/// </summary>
public static class EquipmentRows
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Ccv = new(@"CCV|칼라\s*체인지\s*밸브|COLOR\s*CHANGE\s*VALVE|컬러\s*체인저", Opt);
    // 칼라 수. 항목 번호("3.5 Color Change Valves")의 숫자는 칼라 수가 아니다(앞이 숫자·점이면 뺀다)
    private static readonly Regex Colors = new(@"(?:칼라|컬러|COLORS?)\s*(?:수|개수)\s*[:：]?\s*(\d{1,2})|(?:칼라|컬러)\s*[:：]\s*(\d{1,2})|(?<![\d.])(\d{1,2})\s*(?:EA\s*)?(?:칼라|컬러|색|colors?)\b", Opt);
    // CCV 수량 문장: "CCV number will be decided with tech. meeting (app. 20 ea)", "CCV 수량 : 20EA"
    private static readonly Regex CcvCount = new(@"(?:CCV|COLOR\s*CHANGE\s*VALVE)S?\s*(?:NUMBER|QTY|QUANTITY|수량|개수)[^\n\d]{0,60}?(\d{1,2})\s*(?:EA|개|칼라)", Opt);
    private static readonly Regex Approx = new(@"\bAPP(?:ROX)?\.?\b|약\s*\d|협\s*의|TECH\.?\s*MEETING|DECIDED", Opt);
    private static readonly Regex Spare = new(@"(?:스페어|SPARE|예비)\s*[:：]?\s*(\d{1,2})|(\d{1,2})\s*(?:EA|개)?\s*(?:스페어|SPARE|예비)", Opt);
    // 듀얼 CCV: "듀얼 CCV", "Double Circuit", "A/B 체인저", "AB color changer"(영문 SOR)
    private static readonly Regex Dual = new(@"듀얼|DUAL|더블|DOUBLE|A\s*/?\s*B\s*(?:체인|COLOR\s*CHANGER)", Opt);
    private static readonly Regex Cleaner = new(@"벨\s*컵?\s*세정\s*장치|벨\s*컵\s*세정|BELL\s*(?:CUP)?\s*CLEAN|ATOMIZER\s*CLEAN|도장기\s*세정\s*장치", Opt);
    private static readonly Regex Count = new(@"(\d{1,2})\s*(?:SET|세트|EA|대)\b|로봇\s*당\s*(\d)\s*(?:SET|세트|EA|대)?", Opt);

    // Panel: 사양서에 나온 판넬 종류를 영업부 사양정리 약어로 "1MCP/ 4RC/ 2AP/ 1OP / 1VSP"(과거 사양정리에서 가장 많이 쓴 표기·순서).
    // 수량은 사양서 줄에 적힌 것만 앞에 붙이고, 사양서 원문 줄은 기타 사항에. 분전반은 영업부 약어가 없어 그대로 쓴다.
    private static readonly (string kind, Regex re)[] PanelKinds =
    [
        ("MCP", new(@"PLC\s*판넬|PLC\s*제어반|공정\s*제어반|메인\s*제어반|주\s*제어반|MAIN\s*CONTROL\s*PANEL|\bMCP\b", Opt)),
        ("RC", new(@"로봇\s*컨트롤러|로봇\s*제어반|ROBOT\s*CONTROLLER|\bRC\s*(?:판넬|PANEL)", Opt)),
        ("AP", new(@"공압\s*판넬|공압\s*반|PNEUMATIC\s*PANEL|AIR\s*PANEL", Opt)),
        ("OP", new(@"OP\s*판넬(?:\s*\(HMI[^)]*\))?|OP\s*(?:DESK|PANEL)|조작\s*반", Opt)),
        ("분전반", new(@"분전\s*반|분전\s*함|배전\s*반|POWER\s*DISTRIBUTION\s*PANEL", Opt)),
        // 순간정전 보상장치는 분전함 1차측에 다는 한 면짜리 판넬 — 영업부 사양정리는 Panel 칸에 "1VSP"로 센다(과거 사양정리 19건)
        ("VSP", new(@"순간\s*정전\s*보상\s*장치(?:\s*\(?VSP\)?)?|(?<![A-Z])VSP(?![A-Z])|VOLTAGE\s*SAG", Opt)),
    ];
    // (Power Unit 칸은 2026-10-27 사용자 요청으로 표에서 뺐다 — AVR·UPS·TR은 초안에 넣지 않는다)
    private static readonly Regex Qty = new(@"(\d{1,2})\s*(?:대|EA|면|SET|세트)\b", Opt);
    // (판넬 방진패드·소화장치·에어컨 같은 부대 요구는 RowNotes가 기타 사항에 짧게)

    // 장치 설치 요구가 아닌 줄: 외함·박스 모양 설명("AVR-전면 개방, 제동저항-4면 개방"), 철거
    private static readonly Regex NotInstall = new(@"철거|반납|DISMANTL|개방|외함|BOX|박스", Opt);

    /// <param name="visibleLines">PDF를 보이는 줄 단위로 읽은 것(SpecExtractor.ExtractPdfLines). 표 인식이 여러 줄을 한 칸으로 합쳐 수량이 사라졌을 때 같은 줄의 수량을 찾는다.</param>
    public static void Fill(SummaryDraft d, IReadOnlyList<Block> blocks, IReadOnlyList<Block>? visibleLines = null)
    {
        var lines = Lines(blocks);
        var visible = visibleLines is { Count: > 0 } ? Lines(visibleLines) : null;
        FillColor(d, lines);
        FillCleaner(d, lines);
        FillKinds(d, lines, visible, "Panel", PanelKinds, withQty: true);
        foreach (var (key, re) in Verbatim) FillVerbatim(d, lines, key, re);
    }

    private static List<(Block b, string t)> Lines(IReadOnlyList<Block> blocks) =>
        blocks.Select(b => (b, t: Regex.Replace(b.Text, @"\s+", " "))).Where(x => x.t.Length > 0).ToList();

    // 규칙(숫자 뽑기)으로 못 채운 칸은 사양서 원문 줄을 그대로 — 사양서에 있는 내용이 과거 참고보다 먼저
    private static readonly (string key, Regex re)[] Verbatim =
    [
        ("Schedule", new(@"S\.?\s*O\.?\s*P|양산\s*(?:예정|개시|일정|시작)|셧\s*다운|하기\s*휴무|동기\s*휴무|본\s*공사|설치\s*(?:기간|일정)|START\s*OF\s*PRODUCTION|SHUT\s*-?\s*DOWN", Opt)),
        ("Stand by", new(@"양산\s*(?:대기|대응)|STAND\s*-?\s*BY|라인\s*대응|\d+\s*M\s*/\s*D", Opt)),
        ("Paint", new(@"도\s*료\s*(?:사양|종류)?\s*[:：]|유용성|수용성|SOLVENT\s*BORNE|WATER\s*BORNE", Opt)),
    ];

    private static void FillVerbatim(SummaryDraft d, List<(Block b, string t)> lines, string key, Regex re)
    {
        var row = d.Rows.First(r => r.Key == key);
        if (row.Value.Length > 0) return;
        // 목차·쪽 제목 같은 짧은 줄과 철거 문장은 뺀다
        var hits = lines.Where(x => re.IsMatch(x.t) && x.t.Length >= 8 && !Regex.IsMatch(x.t, "철거|목\\s*차|CONTENTS", RegexOptions.IgnoreCase))
            .DistinctBy(x => x.t).Take(2).ToList();
        if (hits.Count == 0) return;
        row.Value = string.Join("\n", hits.Select(h => Short(h.t)));
        row.AddRemark("사양서 원문 줄 그대로 (규칙으로 정리 못 함) — 확인");
        row.Pages = hits.Select(h => h.b.UnitIndex).Distinct().Order().ToList();
    }

    /// <summary>
    /// 사양서에 나온 장치 종류를 영업부 약어로 나열(값, 수량이 있으면 "2MCP"), 종류마다 처음 나온 문장은 원문 근거(Source)로.
    /// 사양서에 없으면 비워 둔다(지어내지 않음) — 비면 과거 사양정리 참고 제안(RowHints)이 붙는다.
    /// </summary>
    private static void FillKinds(SummaryDraft d, List<(Block b, string t)> lines, List<(Block b, string t)>? visible, string rowKey,
        (string kind, Regex re)[] kinds, bool withQty)
    {
        var row = d.Rows.First(r => r.Key == rowKey);
        if (row.Value.Length > 0) return;
        var parts = new List<string>();
        var notes = new List<string>();
        var source = new List<string>();
        var pages = new SortedSet<int>();
        // 수량: 이름 뒤 45자 안. 이름과 수량 사이에 다른 종류 이름이 끼면(표 인식이 "1) PLC 판넬 / 2) OP판넬 … | 2 SET"로 합침)
        // 어느 것의 수량인지 알 수 없어 붙이지 않는다. 괄호 안 설명("공정제어반(분전반은 별도설치)")은 보지 않는다.
        string QtyIn((Block b, string t) x, Regex re)
        {
            var m = re.Match(x.t);
            var rest = Regex.Replace(x.t[(m.Index + m.Length)..], @"\([^)]*\)", " ");
            if (Qty.Match(rest) is not { Success: true } q || q.Index >= 45) return "";
            var between = rest[..q.Index];
            return kinds.Any(k => k.re != re && k.re.IsMatch(between)) ? "" : q.Groups[1].Value;
        }
        foreach (var (kind, re) in kinds)
        {
            if (lines.FirstOrDefault(x => re.IsMatch(x.t) && !NotInstall.IsMatch(x.t)) is not { t: not null } hit) continue;
            var qty = "";
            if (withQty)
            {
                qty = QtyIn(hit, re);
                // 표 칸에서 못 찾으면 같은 쪽의 '보이는 줄'에서(사양서 p2 "3) 공압판넬 … 8 SET")
                if (qty.Length == 0 && visible is not null)
                    qty = visible.Where(x => x.b.UnitIndex == hit.b.UnitIndex && re.IsMatch(x.t) && !NotInstall.IsMatch(x.t))
                        .Select(x => QtyIn(x, re)).FirstOrDefault(q => q.Length > 0) ?? "";
            }
            parts.Add(qty + kind);
            source.Add($"{Short(hit.t)} (p{hit.b.UnitIndex})");
            pages.Add(hit.b.UnitIndex);
        }
        if (parts.Count == 0 && notes.Count == 0) return;
        row.Value = string.Join(" / ", parts.Distinct());
        row.Note = string.Join("\n", notes.Distinct());
        row.Source = source.Distinct().ToList();
        row.Pages = pages.ToList();
    }

    /// <summary>
    /// Panel 수량: 사양서에 수량이 없는 판넬은 로봇 대수로 센다(2026-10-27 사용자 — 판넬은 항상 수량과 함께 "1MCP / 3RC / 2AP / 1OP").
    /// 과거 사양정리(2023~2026) 규칙: RC = 전체 로봇(로봇마다 컨트롤러), AP(공압판넬) = 스프레이 로봇(오프너는 도장 공압 없음),
    /// MCP·OP = 공정(존)마다 1, VSP 1. 센 수량은 사양서 값이 아니므로 기타 사항에 계산 근거를 적는다. 분전반은 셀 근거가 없어 그대로.
    /// </summary>
    public static void PanelCounts(SummaryDraft d, int spray, int opener, int processes)
    {
        var row = d.Rows.First(r => r.Key == "Panel");
        if (row.Edited || row.Value.Length == 0 || spray + opener == 0) return;
        var zones = Math.Max(1, processes);
        var counted = new List<string>();
        var parts = row.Value.Split(" / ").Select(p =>
        {
            if (Regex.IsMatch(p, @"^\d")) return p;
            int? n = p switch { "RC" => spray + opener, "AP" => spray > 0 ? spray : null, "MCP" or "OP" => zones, "VSP" => 1, _ => null };
            if (n is null) return p;
            counted.Add($"{n}{p}");
            return $"{n}{p}";
        }).ToList();
        if (counted.Count == 0) return;
        row.Value = string.Join(" / ", parts);
        var note = $"수량은 사양서에 없어 로봇 대수로 셈({string.Join(", ", counted)}): RC = 전체 로봇 {spray + opener}, AP = 스프레이 로봇 {spray}, MCP·OP = 공정 {zones}";
        row.AddRemark(note);
    }

    private static string Short(string s) => s.Length <= 70 ? s.Trim() : s[..70].Trim() + "…";

    /// <summary>사양서에 벨컵 세정장치 요구가 있는지(없으면 세정장치 칸·모델 제안을 넣지 않는다)</summary>
    public static bool MentionsCleaner(IReadOnlyList<Block> blocks) => blocks.Any(b => Cleaner.IsMatch(b.Text));

    private static void FillColor(SummaryDraft d, List<(Block b, string t)> lines)
    {
        var dualLine = lines.FirstOrDefault(x => Ccv.IsMatch(x.t) && Dual.IsMatch(x.t));
        var dual = dualLine.t is not null ? "듀얼 " : "";
        var row = d.Rows.First(r => r.Key == "Color(CCV)");
        var notes = new List<string>();
        var pages = new List<int>();
        if (dualLine.t is not null) { notes.Add("**Dual Circuit** 적용"); pages.Add(dualLine.b.UnitIndex); }

        // 1) CCV 수량 문장(영문 SOR 등): "CCV number … (app. 20 ea)"
        if (lines.FirstOrDefault(x => CcvCount.IsMatch(x.t)) is { t: not null } q)
        {
            var n = CcvCount.Match(q.t).Groups[1].Value;
            var approx = Approx.IsMatch(q.t);
            row.Value = $"{dual}CCV : {{{{{(approx ? "약 " : "")}{n}개}}}}";
            if (approx) notes.Insert(0, "[[CCV 수량은 기술 협의로 결정]]");
            pages.Insert(0, q.b.UnitIndex);
            row.Note = string.Join("\n", notes);
            row.Pages = pages.Distinct().Order().ToList();
            return;
        }

        // 2) CCV 줄 중 칼라 수가 있는 줄, 그중 스페어까지 적힌 줄을 먼저
        var cand = lines.Where(x => Ccv.IsMatch(x.t) && Colors.IsMatch(x.t)).OrderByDescending(x => Spare.IsMatch(x.t)).ToList();
        if (cand.Count == 0) cand = lines.Where(x => Regex.IsMatch(x.t, "칼라|컬러|COLOR", RegexOptions.IgnoreCase) && Colors.IsMatch(x.t)).ToList();
        if (cand.Count == 0)
        {
            if (notes.Count > 0) { row.Note = string.Join("\n", notes); row.Pages = pages; }
            return;
        }
        var (b, t) = cand[0];
        var m = Colors.Match(t);
        var colors = int.Parse(new[] { m.Groups[1], m.Groups[2], m.Groups[3] }.First(g => g.Success).Value);
        var sm = Spare.Match(t);
        var spare = sm.Success ? int.Parse(sm.Groups[1].Success ? sm.Groups[1].Value : sm.Groups[2].Value) : 0;
        row.Value = spare > 0
            ? $"{dual}CCV : {{{{{colors + spare}개 ({colors}칼라 + 스페어 {spare})}}}}"
            : $"{dual}CCV : {{{{{colors}칼라}}}}";
        pages.Insert(0, b.UnitIndex);
        row.Note = string.Join("\n", notes);
        row.Pages = pages.Distinct().Order().ToList();
    }

    /// <summary>
    /// Bell Cleaner 칸(영업부 형식, 과거 사양정리 22건): 값 "크리스탈 CC-3000 : 4 set"(모델 : 수량), 기능·변경은 기타 사항,
    /// 사양서에 세정장치 요구가 없으면 "-"와 참고 사항 "사양서에 세정장치 요구 없음".
    /// 모델이 사양서에 없으면 값은 "세정장치 : 4 set"로 두고, 기본 사양(1순위 모델)을 채택하면 앞의 "세정장치"가 모델로 바뀐다(DraftEdit).
    /// </summary>
    private static void FillCleaner(SummaryDraft d, List<(Block b, string t)> lines)
    {
        var row = d.Rows.First(r => r.Key == "Bell Cleaner");
        var hits = lines.Where(x => Cleaner.IsMatch(x.t)).ToList();
        if (hits.Count == 0)
        {
            row.Value = "-";
            row.AddRemark("사양서에 세정장치 요구 없음");
            return;
        }
        // 수량: 세정장치 줄과 그 쪽의 '로봇당 n세트'·'n SET'
        string qty = "";
        foreach (var page in hits.Select(h => h.b.UnitIndex).Distinct())
        {
            var near = lines.Where(x => x.b.UnitIndex == page).Select(x => x.t);
            if (near.Select(t => Count.Match(t)).FirstOrDefault(m => m.Success && Regex.IsMatch(m.Value, "SET|세트|로봇\\s*당", RegexOptions.IgnoreCase)) is { } cm)
            {
                qty = cm.Groups[2].Success ? $"스프레이 로봇당 {cm.Groups[2].Value} set" : $"{cm.Groups[1].Value} set";
                break;
            }
        }
        // 사양서에 모델이 적혀 있으면 그대로(ModelPicker가 '사양서 지정'으로 둔다)
        var model = hits.Select(h => CleanerModelIn(h.t)).FirstOrDefault(m => m.Length > 0) ?? "";
        row.Value = (model.Length > 0 ? model : CleanerPlaceholder) + (qty.Length > 0 ? $" : {qty}" : "");
        var pages = hits.Select(h => h.b.UnitIndex).Distinct().Order().ToList();
        // 세정장치 쪽에 바이패스 요구가 있으면 기타 사항에
        if (lines.Any(x => pages.Contains(x.b.UnitIndex) && Regex.IsMatch(x.t, @"바이\s*패스|BY\s*-?\s*PASS", RegexOptions.IgnoreCase)))
            row.Note = "바이패스 기능";
        row.Pages = pages;
    }

    /// <summary>모델이 정해지기 전 Bell Cleaner 값 앞자리(채택하면 모델로 바뀐다)</summary>
    public const string CleanerPlaceholder = "세정장치";

    /// <summary>세정장치 모델 표기(영업부: "크리스탈 CC-3000"). 설비 이력 표기 "CC-3000 (크리스탈)"도 같은 꼴로.</summary>
    public static string CleanerLabel(string model)
    {
        var cc = Regex.Match(model, @"CC\s*-\s*\d{3,4}", RegexOptions.IgnoreCase) is { Success: true } c ? Regex.Replace(c.Value.ToUpperInvariant(), @"\s+", "") : "";
        var crystal = Regex.IsMatch(model, "크리스탈|CRYSTAL|CRISTAL", RegexOptions.IgnoreCase);
        return cc.Length > 0 ? (crystal ? $"크리스탈 {cc}" : cc) : crystal ? "크리스탈" : model.Trim();
    }

    private static readonly Regex CleanerModelRe = new(@"\bCC\s*-\s*\d{3,4}\b|크리스탈|CRYSTAL|CRISTAL", Opt);

    private static string CleanerModelIn(string t) =>
        CleanerModelRe.IsMatch(t) ? CleanerLabel(string.Join(" ", CleanerModelRe.Matches(t).Select(m => m.Value))) : "";
}
