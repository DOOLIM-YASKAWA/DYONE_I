using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;

namespace DyOne.Spec.Equipment;

/// <summary>
/// 공정 단위 설비 이력 한 건. 사양정리 표에 공정이 여러 열이면(조지아: 중도 외판 / 베이스 외판 / 클리어 외판 …) 열마다 한 건,
/// 공정이 하나뿐이거나 도면만 있는 프로젝트는 프로젝트 한 건.
/// Paint: 유성|수성|"" , Charge: 내부하전|외부하전|비정전|"" — 도장기 칸·프로젝트 이름·고객 사양서에서 읽는다(PaintSrc·ChargeSrc).
/// </summary>
public sealed record EquipUnit(
    string Id, string Year, string ProjectFolder, string ProjectDir, string? ProjectCode, string Unit,
    string Site, string Process, string Part, string Paint, string PaintSrc, string Charge, string ChargeSrc,
    string[] Spray, string[] Opener, string[] RobotAny, string[] Atomizer, string[] Fgp,
    string SourceKind, string FilePath, string FileName, string[]? Cleaner = null)
{
    public int YearNo => int.TryParse(Year, out var y) ? y : 0;
    public string Label => $"{Year} {ProjectFolder}{(Unit.Length > 0 ? " · " + Regex.Replace(Unit, @"\s+", " ") : "")}";
}

/// <summary>
/// 공정 단위 설비 이력 만들기(scripts: spec-draft/분석/model_units.py 와 같은 규칙).
/// 범퍼·부품 업체 이력은 차체 도장 판단에서 빼도록 IsPartsMaker로 거른다.
/// </summary>
public static class EquipmentUnits
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Robot = new(@"\b((?:MPX|EPX|MPL|PX)\s*-?\s*\d{3,4}[A-Z]?)\b", Opt);
    private static readonly Regex Atom = new(@"((?:SAMES\s*)?PPH\s*-?\s*\d{3}(?:\s*(?:SB|HI\s*-?\s*TE|SR|EXT|E\b|AIR\s*SPR?AY))?|ACCU\s*BELL\s*(?:EVO)?\s*\d*|NANO\s*BELL\s*\d*|ECO\s*BELL\s*\d*)", Opt);
    // FGP 용량(1회전당 토출량, 보통 1.5~10cc). 분당 토출량("최대 900cc/min", "분당 900CC")은 용량이 아니라 뺀다(두 자리까지만)
    private static readonly Regex Cc = new(@"(?<!분\s*당\s*)(?<![\d.])(\d{1,2}(?:\.\d+)?)\s*CC\b(?!\s*/\s*(?:min|분|M\b))", Opt);
    private static readonly Regex RoleWord = new(@"(오프너|OPENER|스프레이|SPRAY|도장\s*로봇|도장)", Opt);
    // 벨컵 세정장치 모델: 사양정리에 적힌 그대로 — "CC-3000 (크리스탈)", "Crystal", "CRISTAL BELL CLEANER"(철자 다름), "Local 세정장치"(현대 전주)
    private static readonly Regex CleanerModel = new(@"\bCC\s*-\s*\d{3,4}\b|크리스탈|CRYSTAL|CRISTAL|LOCAL\s*세정|로컬\s*세정|세정\s*장치\s*\(\s*로컬", Opt);
    private static readonly Regex PlantRe = new(@"(광주|화성|아산|울산|전주|소하|광명|부평|창원|서산|군산|평택|당진)\s*(?:공장)?\s*(\d)?", Opt);
    private static readonly Regex PlantAbbr = new(@"(광|화|아|울)\)\s*(\d)", Opt);
    private static readonly (Regex re, string name)[] Sites =
    [
        (new(@"조지아|KaGA", Opt), "KaGA(조지아)"), (new(@"슬로바키아|KaSK", Opt), "KaSK(슬로바키아)"), (new(@"HMMA|앨라배마|알라바마", Opt), "HMMA(앨라배마)"),
        (new(@"사우디", Opt), "사우디"), (new(@"터키|HMMT", Opt), "터키"), (new(@"인도네시아|HMMI", Opt), "인도네시아"), (new(@"북경|BHMC", Opt), "북경"),
        (new(@"염성|KCN", Opt), "KCN(염성)"), (new(@"\bKIN\b", Opt), "KIN"), (new(@"\bPCZ\b", Opt), "PCZ"), (new(@"\bHMI\b", Opt), "HMI"),
        (new(@"\bRKM\b|르노", Opt), "RKM"), (new(@"GMK|\bGM\b|부평", Opt), "부평1"),
    ];
    private static readonly Regex PartsMaker = new(@"범퍼|가니쉬|에코\s*플라스틱|플라스틱|서연|대성|자강|진성|프라코|아이아|태화|모비스", Opt);
    private static readonly (string key, Regex re)[] Heads =
    [
        ("process", new(@"^\s*(?:Process|공정\s*명?|구\s*분)", Opt)), ("robot", new(@"^\s*(?:Robot|로봇)", Opt)),
        ("atomizer", new(@"^\s*(?:Atomizer|도장기)", Opt)), ("fgp", new(@"^\s*(?:FGP|정량\s*펌프|기어\s*펌프)", Opt)),
        ("color", new(@"^\s*(?:Color|CCV|칼라|컬러)", Opt)), ("paint", new(@"^\s*(?:Paint|도료)", Opt)),
        ("cleaner", new(@"^\s*(?:Bell\s*Cleaner|벨\s*컵?\s*세정|세정\s*장치|Cleaner)", Opt)),
    ];
    private static readonly Regex NoteHead = new(@"^\s*(기타\s*사항|비\s*고|remark|note)", Opt);
    private static readonly Regex CodeRe = new(@"\b[A-Z]{2,4}-\d{2}-\d{3}\b", Opt);

    public static bool IsPartsMaker(string text) => PartsMaker.IsMatch(text);

    public static string SiteOf(string text)
    {
        if (PlantRe.Match(text) is { Success: true } m) return m.Groups[1].Value + m.Groups[2].Value;
        if (PlantAbbr.Match(text) is { Success: true } a)
            return (a.Groups[1].Value switch { "광" => "광주", "화" => "화성", "아" => "아산", _ => "울산" }) + a.Groups[2].Value;
        return Sites.FirstOrDefault(s => s.re.IsMatch(text)).name ?? "";
    }

    public static string ProcessOf(string t)
    {
        foreach (var (name, pat) in new[] { ("중도", @"중\s*도|PRIMER|SURFACER"), ("클리어", @"클리어|CLEAR"), ("베이스", @"베이스(?!\s*프레임)|BASE\s*COAT|\bBC\b"),
                     ("하도", @"하\s*도|전\s*착"), ("실러", @"실러|SEALER|PVC|UBC"), ("범퍼", @"범퍼|BUMPER") })
            if (Regex.IsMatch(t, pat, RegexOptions.IgnoreCase)) return name;
        return Regex.IsMatch(t, @"상\s*도") ? "베이스" : ""; // '상도'만 있으면 보통 베이스(클리어는 따로 적음)
    }

    public static string PartOf(string t)
    {
        var p = new List<string>();
        if (Regex.IsMatch(t, @"외\s*판|\bEXT(?:ERIOR)?\b|OUTER", RegexOptions.IgnoreCase)) p.Add("외판");
        if (Regex.IsMatch(t, @"내\s*판|\bINT(?:ERIOR)?\b|INNER|도어\s*내|H\s*/\s*T|후드|트렁크", RegexOptions.IgnoreCase)) p.Add("내판");
        return string.Join("/", p);
    }

    public static string PaintOf(string t)
    {
        var w = Regex.IsMatch(t, @"수\s*성|WATER\s*-?\s*BORNE|\bWB\b", RegexOptions.IgnoreCase);
        var s = Regex.IsMatch(t, @"유\s*성|유용성|SO?L?VENT\s*-?\s*BORNE", RegexOptions.IgnoreCase);
        return w && !s ? "수성" : s && !w ? "유성" : w && s ? "혼재" : "";
    }

    public static string ChargeOf(string t)
    {
        if (Regex.IsMatch(t, @"비\s*정전|AIR\s*SPR?AY", RegexOptions.IgnoreCase)) return "비정전";
        var i = Regex.IsMatch(t, @"내부\s*하전|INTERNAL\s*CHARG|직접\s*하전", RegexOptions.IgnoreCase);
        var e = Regex.IsMatch(t, @"외부\s*하전|EXTERNAL\s*CHARG|간접\s*하전|\bEXT\b", RegexOptions.IgnoreCase);
        return i && !e ? "내부하전" : e && !i ? "외부하전" : i && e ? "혼재" : "";
    }

    public static string NormAtom(string s)
    {
        var t = Regex.Replace(s.ToUpperInvariant(), @"\s+", " ").Trim();
        t = Regex.Replace(t, @"PPH\s*-?\s*(\d{3})", "PPH$1");
        t = Regex.Replace(t, @"HI\s*-?\s*TE", "HI-TE");
        t = Regex.Replace(t, @"AIR\s*SPR?AY", "AIRSPRAY");
        t = Regex.Replace(t, @"ACCU\s*BELL\s*(EVO)?\s*", m => m.Groups[1].Success ? "ACCUBELL EVO " : "ACCUBELL ").Trim();
        return t.Replace("SAMES ", "").Replace("NANO BELL", "NANOBELL");
    }

    /// <summary>세정장치 모델: CC-3000과 크리스탈이 함께 있으면 "CC-3000 (크리스탈)"</summary>
    private static string[] CleanerOf(string text)
    {
        var ms = CleanerModel.Matches(text).Select(m => Regex.Replace(m.Value.ToUpperInvariant(), @"\s+", "")).Distinct().ToList();
        if (ms.Count == 0) return [];
        if (ms.All(m => m.Contains("로컬") || m.Contains("LOCAL"))) return ["로컬(Local) 세정장치"];
        var cc = ms.FirstOrDefault(m => m.StartsWith("CC"));
        var crystal = ms.Any(m => m is "크리스탈" or "CRYSTAL" or "CRISTAL");
        return [cc is not null ? cc + (crystal ? " (크리스탈)" : "") : "크리스탈"];
    }

    private static string NormRobot(string s) => Regex.Replace(s.ToUpperInvariant(), @"[\s-]", "");

    /// <summary>
    /// 사양정리 한 건 → 공정 단위. 표마다 항목 줄(첫 칸 Robot·도장기 …)을 모으고, 머리 칸이 빈 줄은 위 항목의 이어짐(도장기 모델·벨컵 줄).
    /// 공정 줄에 값이 두 열 이상이면 열마다 한 건, 아니면 표 전체가 한 건.
    /// </summary>
    public static List<EquipUnit> FromSummary(LibraryDoc doc, IReadOnlyList<Block> blocks, IReadOnlyCollection<string> customerPaints)
    {
        var raw = new List<Dictionary<string, string>>();
        foreach (var table in blocks.Where(b => b.Table > 0).GroupBy(b => (b.UnitIndex, b.Table)))
        {
            var rows = new Dictionary<string, List<List<string>>>();
            string? last = null;
            int? noteCol = null;
            var hc = 0;
            foreach (var b in table)
            {
                var r = b.Cells;
                if (r.Count == 0) continue;
                var k = Head(r[0]) ?? (r.Count > 1 && r[0].Length == 0 ? Head(r[1]) : null);
                if (k is not null) hc = Head(r[0]) is not null ? 0 : 1;
                for (var j = hc + 1; j < r.Count; j++) if (NoteHead.IsMatch(r[j])) noteCol = j;
                if (k is not null)
                {
                    last = k;
                    (rows.TryGetValue(k, out var l) ? l : rows[k] = []).Add(r.Skip(hc + 1).ToList());
                }
                else if (last is not null && r[0].Trim().Length == 0 && r.Skip(1).Any(c => c.Length > 0))
                    rows[last].Add(r.Skip(hc + 1).ToList());
            }
            if (!rows.ContainsKey("robot") && !rows.ContainsKey("atomizer")) continue;
            var width = rows.Values.SelectMany(v => v).Max(x => x.Count);
            var end = noteCol is { } nc ? nc - hc - 1 : width;
            var procCols = Enumerable.Range(0, Math.Max(0, end))
                .Where(j => rows.GetValueOrDefault("process")?.Any(x => j < x.Count && x[j].Length > 0) == true).ToList();
            var cols = procCols.Count >= 2 ? procCols.Select(j => (int?)j).ToList() : [null];
            foreach (var j in cols)
            {
                string Val(string key)
                {
                    var lines = rows.GetValueOrDefault(key) ?? [];
                    // 공정이 하나뿐인 표는 '기타 사항' 열까지 읽는다(예: FGP 칸 "4세트", 기타 사항 "SAMES 6CC FGP")
                    return j is null
                        ? string.Join(" / ", lines.SelectMany(x => x).Where(c => c.Length > 0).Distinct())
                        : string.Join(" / ", lines.Where(x => j < x.Count && x[j.Value].Length > 0).Select(x => x[j.Value]));
                }
                var u = new Dictionary<string, string>();
                foreach (var key in new[] { "process", "robot", "atomizer", "fgp", "color", "paint", "cleaner" }) u[key] = Val(key);
                if (u["robot"].Length > 0 || u["atomizer"].Length > 0) raw.Add(u);
            }
        }

        var result = new List<EquipUnit>();
        var folderProc = ProcessOf(doc.ProjectFolder);
        var folderPart = PartOf(doc.ProjectFolder);
        foreach (var r in raw)
        {
            var multi = raw.Count > 1;
            var label = multi ? r["process"] : "";
            // 여러 공정 표: 열 이름이 기준(없으면 비움). 공정 하나: 프로젝트 이름이 기준, 없을 때만 표의 공정 칸.
            var process = multi ? ProcessOf(r["process"]) : folderProc.Length > 0 ? folderProc : ProcessOf(r["process"]);
            var part = multi ? PartOf(r["process"]) : folderPart.Length > 0 ? folderPart : PartOf(r["process"]);
            string paint = "", paintSrc = "", charge = "", chargeSrc = "";
            var pt = PaintOf($"{r["atomizer"]} {r["paint"]} {r["color"]}");
            if (pt is "유성" or "수성") (paint, paintSrc) = (pt, "사양정리");
            var ch = ChargeOf(r["atomizer"]);
            if (ch.Length > 0) (charge, chargeSrc) = (ch, "사양정리");
            if (!multi) FillFromProject(doc.ProjectFolder, customerPaints, ref paint, ref paintSrc, ref charge, ref chargeSrc);
            var (spray, opener, any) = Robots(r["robot"]);
            var atoms = Atom.Matches(r["atomizer"]).Select(m => NormAtom(m.Value)).Distinct().ToArray();
            string[] fgp = FgpOf(r["fgp"]) is { } fgpName ? [fgpName] : [];
            // 세정장치: 세정장치 줄 → 없으면 같은 공정의 CCV 줄(영업부가 "Crystal 세정장치 적용"을 Color 칸에 적기도 함)
            var cleaner = CleanerOf(r["cleaner"]) is { Length: > 0 } cl ? cl : CleanerOf(r["color"]);
            if (spray.Length + opener.Length + any.Length + atoms.Length + fgp.Length + cleaner.Length == 0) continue; // 모델이 없는 표(업무 범위 등)
            result.Add(new EquipUnit(
                LibraryScanner.IdOf($"{doc.Summary!.Path}|{label}"), Year4(doc.Year), doc.ProjectFolder, doc.ProjectDir, doc.ProjectCode, label,
                SiteOf($"{doc.CustomerFolder} {doc.ProjectFolder}"), process, part, paint, paintSrc, charge, chargeSrc,
                spray, opener, any, atoms, fgp,
                "summary", doc.Summary.Path, doc.Summary.FileName, cleaner));
        }
        return result;
    }

    /// <summary>사양정리를 읽지 못한 프로젝트: 도면 제목 모델 + 프로젝트 이름·고객 사양서의 도료·하전으로 한 건</summary>
    public static EquipUnit? FromDrawings(string projectDir, string year, string customer, IReadOnlyList<EquipRecord> recs, IReadOnlyCollection<string> customerPaints)
    {
        if (recs.Count == 0) return null;
        var folder = Path.GetFileName(projectDir);
        string paint = "", paintSrc = "", charge = "", chargeSrc = "";
        FillFromProject(folder, customerPaints, ref paint, ref paintSrc, ref charge, ref chargeSrc);
        var atoms = recs.Where(r => r.Kind == "atomizer").Select(r => NormAtom(r.Model)).Distinct().ToArray();
        if (charge.Length == 0 && atoms.Any(a => a.Contains("EXT"))) (charge, chargeSrc) = ("외부하전", "도장기 이름");
        var first = recs.FirstOrDefault(r => r.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) ?? recs[0];
        return new EquipUnit(
            LibraryScanner.IdOf($"{projectDir}|drawings"), Year4(year), folder, projectDir,
            CodeRe.Match(folder) is { Success: true } c ? c.Value : null, "",
            SiteOf($"{customer} {folder}"), ProcessOf(folder), PartOf(folder), paint, paintSrc, charge, chargeSrc,
            [], [], recs.Where(r => r.Kind == "robot").Select(r => r.Model).Distinct().ToArray(), atoms,
            recs.Where(r => r.Kind == "fgp").Select(r => r.Model).Distinct().ToArray(),
            "drawing", first.FilePath, first.FileName);
    }

    private static void FillFromProject(string folder, IReadOnlyCollection<string> cust, ref string paint, ref string paintSrc, ref string charge, ref string chargeSrc)
    {
        if (paint.Length == 0 && PaintOf(folder) is "유성" or "수성") (paint, paintSrc) = (PaintOf(folder), "프로젝트 이름");
        var ps = cust.Where(x => x is "유성" or "수성").ToList();
        if (paint.Length == 0 && ps.Count == 1) (paint, paintSrc) = (ps[0], "고객 사양서");
        var cs = cust.Where(x => x is "내부하전" or "외부하전").ToList();
        if (charge.Length == 0 && cs.Count == 1) (charge, chargeSrc) = (cs[0], "고객 사양서");
    }

    private static (string[] spray, string[] opener, string[] any) Robots(string text)
    {
        List<string> s = [], o = [], a = [];
        foreach (Match m in Robot.Matches(text))
        {
            var roles = RoleWord.Matches(text[Math.Max(0, m.Index - 30)..m.Index]);
            var model = NormRobot(m.Groups[1].Value);
            (roles.Count == 0 ? a : Regex.IsMatch(roles[^1].Value, "오프너|OPENER", RegexOptions.IgnoreCase) ? o : s).Add(model);
        }
        return (s.Distinct().ToArray(), o.Distinct().ToArray(), a.Distinct().ToArray());
    }

    private static string? Head(string cell)
    {
        var c = cell.Trim();
        if (c.Length == 0 || c.Length > 30) return null;
        return Heads.FirstOrDefault(h => h.re.IsMatch(c)).key;
    }


    // FGP 제조사(과거 사양정리 표기: "PSMP 5cc FGP", "SAMES 6CC", "두림 FGP 5cc", "다산 1.5cc"). Graco는 유량계라 뺀다.
    private static readonly (string name, Regex re)[] FgpMakers =
    [
        ("PSMP", new(@"\bPSMP\b", Opt)), ("SAMES", new(@"\bSAMES\b|사메스", Opt)),
        ("두림", new(@"두림|DOOLIM", Opt)), ("다산", new(@"다산|DASAN", Opt)),
    ];

    /// <summary>
    /// FGP 표기: 영업부 사양정리처럼 "제조사 용량 FGP"(예: "SAMES 6CC FGP"). 5cc·6cc는 모델이 아니라 1회전당 토출량이라
    /// 제조사를 함께 쓴다. 용량 바로 앞(같은 조각)의 제조사를 먼저, 없으면 글 전체에서. 제조사가 없으면 "6CC FGP".
    /// 주제·경화제가 함께 적히면("주제 : PSMP 5cc / 경화제 : 다산 1.5cc") 첫 용량(주제)만.
    /// </summary>
    public static string? FgpOf(string text)
    {
        if (Cc.Match(text) is not { Success: true } m) return null;
        var n = (double.TryParse(m.Groups[1].Value, out var v) ? v.ToString("0.#") : m.Groups[1].Value) + "CC";
        var piece = text[..m.Index];
        var cut = piece.LastIndexOfAny(['/', '|', ';', '\n']);
        if (cut >= 0) piece = piece[(cut + 1)..];
        var maker = FgpMakers.FirstOrDefault(k => k.re.IsMatch(piece)).name ?? FgpMakers.FirstOrDefault(k => k.re.IsMatch(text)).name;
        return maker is null ? $"{n} FGP" : $"{maker} {n} FGP";
    }

    private static string Year4(string y) => y.Length >= 4 ? y[..4] : y;
}
