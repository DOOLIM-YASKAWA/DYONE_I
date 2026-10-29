using System.Text.RegularExpressions;
using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Draft;

/// <summary>사양서에서 읽은 조건(모델을 정하는 기준). 공장·공정·외/내판·부위는 사양서 파일 이름 → 프로젝트 이름 → 사양서 첫 쪽 순서.</summary>
public sealed record SpecConditions(
    string Plant, string Process, string Part, string Area, string Paint, string PaintNote, string Charge, string ChargeNote, string[] Roles);

/// <summary>모델 후보 하나: 모델, 같은 묶음에서 쓴 건수, 사례(최근 순). Note = 후보 이유(예: "최근 추세: 2022년 이후 중도·내판 11/25건")</summary>
public sealed record PickOption(string Model, int Count, int Total, List<EquipUnit> Examples, string? Note = null);

/// <summary>
/// 한 자리(스프레이 로봇·오프너·도장기·FGP)의 결정.
/// How: 사양서 지정 | 기설치 이력 | 공장 이력 | 경향 규칙 | 선정 필요. Options[0]이 고른 모델(사양서 지정·선정 필요면 비어 있음).
/// </summary>
public sealed record ModelPick(string Kind, string Role, string Label, string Model, string How, string Why,
    int? SpecPage, string SpecText, List<PickOption> Options);

/// <summary>
/// 사양서 → 로봇·오프너·도장기·FGP 모델 정하기(spec-draft/분석/pick_models.py 와 같은 규칙). 칸마다 처음 걸리는 것:
///  1) 사양서 지정  2) 기설치 이력("기설치와 동일" → 같은 공장·공정·외/내판 최근)
///  3) 기본 규칙(영업 기준 2026-10-27): 스프레이 MPX3500(SPC·임베디드 언급 시 MPX3600), 오프너 도어 MPX2600·후드/트렁크 MPX3500, FGP PSMP 5CC FGP
///     / 경향 규칙: 도장기 = 도료·하전, 세정장치 = 같은 공장 최근 → 2022년 이후 보통  4) 선정 필요
/// 같은 공장 최근 모델·최근 추세(다른 공장 2022년 이후) 모델은 아래 후보로 함께 보여 준다.
/// 이번 프로젝트 자신의 기록(프로젝트 번호가 같음)과 범퍼·부품 업체 기록은 근거에서 뺀다.
/// </summary>
public static class ModelPicker
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly (string name, Regex re)[] Processes =
        [("중도", new(@"중\s*도|PRIMER|SURFACER", Opt)), ("클리어", new(@"클리어|CLEAR\s*COAT|\bCLEAR\b", Opt)), ("베이스", new(@"베이스(?!\s*프레임)|BASE\s*COAT|\bBC\b|상\s*도", Opt))];
    private static readonly Regex Water = new(@"수\s*성|수용성|WATER\s*-?\s*BORNE|\bWB\b", Opt);
    private static readonly Regex Solvent = new(@"유\s*성|유용성|SO?L?VENT\s*-?\s*BORNE", Opt);
    private static readonly Regex Either = new(@"(?:유\s*성|SO?L?VENT\s*-?\s*BORNE)\s*(?:or|또는|/)\s*(?:수\s*성|WATER\s*-?\s*BORNE)|(?:수\s*성|WATER\s*-?\s*BORNE)\s*(?:or|또는|/)\s*(?:유\s*성|SO?L?VENT\s*-?\s*BORNE)", Opt);
    private static readonly Regex Convert = new(@"conversion\s*to\s*wat|수성\s*(?:도료\s*)?전환", Opt);
    private static readonly Regex Internal = new(@"내부\s*하전|INTERNAL\s*CHARG|직접\s*하전|DIRECT\s*CHARG", Opt);
    private static readonly Regex External = new(@"외부\s*하전|EXTERNAL\s*CHARG|간접\s*하전|INDIRECT\s*CHARG", Opt);
    private static readonly Regex NonEs = new(@"비\s*정전|NON\s*-?\s*ELECTROSTATIC|AIR\s*SPRAY", Opt);
    private static readonly Regex PaintLine = new(@"도\s*료|도장기|PAINT|ATOMIZER|BELL|벨", Opt);
    private static readonly Regex RobotTopic = new(@"로\s*봇|로\s*보\s*트|ROBOT", Opt);
    private static readonly Regex RobotModel = new(@"\b(?:MPX|EPX)\s*-?\s*\d{4}\b|\bIRB\s*\d{4}", Opt);
    private static readonly Regex AtomTopic = new(@"도장기|ATOMIZER|BELL|벨", Opt);
    private static readonly Regex AtomModel = new(@"PPH\s*-?\s*707\s*(?:SB|EXT|HI\s*-?\s*TE|AIR\s*SPR?AY)?|ACCU\s*BELL[^\n|]{0,8}|ECO\s*BELL\s*\d*|NANO\s*BELL\s*\d*", Opt);
    private static readonly Regex FgpTopic = new(@"FGP|정량\s*펌프|기어\s*펌프|GEAR\s*PUMP|도징\s*펌프|DOSING", Opt);
    // FGP 용량(1회전당 cc). 분당 토출량("분당 900CC", "900cc/min")은 용량이 아니다 — 두 자리까지만, 분당·/min 제외
    private static readonly Regex FgpModel = new(@"(?<!분\s*당\s*)(?<![\d.])\b(\d{1,2}(?:\.\d+)?)\s*cc\b(?!\s*/\s*(?:min|분|M\b))", Opt);
    private static readonly Regex Demolish = new(@"철거|반납|기존\s*로봇|DISMANTL|REMOV", Opt);
    private static readonly Regex Existing = new(@"((?:기\s*설치|기존|EXISTING)[^|\n]{0,30}?)(?:과|와)?\s*(?:동일|같은|SAME\s*AS)\s*(?:한\s*)?(?:모델|기종|제품|메이커|MAKER|MODEL)", Opt);
    private static readonly Regex Hood = new(@"후드|HOOD|H\s*/\s*T|\bHT\b", Opt);
    private static readonly Regex CleanerTopic = new(@"세정\s*장치|CLEAN", Opt);
    private static readonly Regex CleanerModel = new(@"\bCC\s*-\s*\d{3,4}\b|크리스탈|CRYSTAL|CRISTAL", Opt);
    private static readonly Regex Trunk = new(@"트렁크|TRUNK", Opt);
    private static readonly HashSet<string> SmallRobots = ["MPX2600", "EPX2050", "PX2050", "MPX1950"];

    public static SpecConditions ReadConditions(IReadOnlyList<Block> blocks, string projectName, string fileName, IReadOnlyList<EquipUnit> units, string? excludeCode, IReadOnlySet<string>? excludeDirs = null)
    {
        // 분석 대상은 사양서이므로 사양서 파일 이름 → DY ONE 프로젝트 이름 → 사양서 첫 쪽 순서로 본다
        var first = string.Join("\n", blocks.Where(b => b.UnitIndex == 1).Select(b => b.Text));
        var body = string.Join("\n", blocks.Select(b => b.Text));
        string Pick(Func<string, string> f) =>
            f(fileName) is { Length: > 0 } a ? a : f(projectName) is { Length: > 0 } b ? b : f(first);

        var plant = Pick(EquipmentUnits.SiteOf);
        var process = Pick(t => Processes.FirstOrDefault(p => p.re.IsMatch(t)).name ?? "");
        var part = Pick(t => string.Join("/", new[] { ("외판", @"외\s*판|EXTERIOR|\bEXT\b|OUTER"), ("내판", @"내\s*판|INTERIOR|INNER|도어\s*내|후드|트렁크|H\s*/\s*T") }
            .Where(x => Regex.IsMatch(t, x.Item2, RegexOptions.IgnoreCase)).Select(x => x.Item1)));
        var area = Pick(t => string.Join("/", new[] { ("도어", @"도어|DOOR"), ("후드", Hood.ToString()), ("트렁크", @"트렁크|TRUNK|H\s*/\s*T|\bHT\b"), ("테일게이트", @"T\s*/\s*G|테일\s*게이트|TAIL\s*GATE") }
            .Where(x => Regex.IsMatch(t, x.Item2, RegexOptions.IgnoreCase)).Select(x => x.Item1)));

        // 도료: 도료·도장기 문장에서 많이 나온 쪽. 둘 다 나오거나·'A 또는 B'·수성 전환 대비·근거가 적으면 같은 공장 최근 이력
        var paintCtx = string.Join("\n", blocks.Select(b => b.Text).Where(t => PaintLine.IsMatch(t)));
        int w = Water.Matches(paintCtx).Count, s = Solvent.Matches(paintCtx).Count;
        var paint = w > s ? "수성" : s > w ? "유성" : "";
        var note = $"사양서 도료 문장: 수성 {w}회 / 유성 {s}회";
        var either = Either.IsMatch(body);
        var convert = Convert.IsMatch(body);
        if ((w > 0 && s > 0) || w + s < 2 || either || convert)
        {
            var hist = units.Where(u => plant.Length > 0 && u.Site == plant && u.Paint is "유성" or "수성" && Usable(u, excludeCode, excludeDirs))
                .OrderByDescending(u => u.YearNo).FirstOrDefault();
            if (hist is not null) { paint = hist.Paint; note += $" → 사양서가 애매해 같은 공장 최근 이력 사용 ({hist.Label})"; }
            else note += " → 애매함, 확인 필요";
        }
        if (either) note += " / 사양서에 '유성 또는 수성'";
        if (convert) note += " / 수성 전환 대비 문구 있음";

        int ci = Internal.Matches(body).Count, ce = External.Matches(body).Count, cn = NonEs.Matches(body).Count;
        var charge = ci > ce ? "내부하전" : ce > ci ? "외부하전" : cn > 0 && ci == 0 ? "비정전" : "";
        string[] roles = Regex.IsMatch(body, "오프너|OPENER", RegexOptions.IgnoreCase) ? ["스프레이", "오프너"] : ["스프레이"];
        return new SpecConditions(plant, process, part, area, paint, note, charge, $"내부 {ci} / 외부 {ce} / 비정전 {cn}", roles);
    }

    public static List<ModelPick> Pick(IReadOnlyList<Block> blocks, SpecConditions c, IReadOnlyList<EquipUnit> allUnits, string? excludeCode, IReadOnlySet<string>? excludeDirs = null)
    {
        var units = allUnits.Where(u => Usable(u, excludeCode, excludeDirs)).ToList();
        var recent = units.Where(u => u.YearNo >= 2022).ToList();
        var part = c.Part.Split('/')[0];
        IEnumerable<EquipUnit> SamePlant(IEnumerable<EquipUnit> us) => us.Where(u => c.Plant.Length > 0 && u.Site == c.Plant);
        IEnumerable<EquipUnit> SamePart(IEnumerable<EquipUnit> us) => us.Where(u => part.Length > 0 && u.Part.Contains(part));
        IEnumerable<EquipUnit> SameProc(IEnumerable<EquipUnit> us) => us.Where(u => c.Process.Length > 0 && u.Process == c.Process);
        // 최근 추세 후보: 같은 공장이 아니어도 2022년 이후 같은 공정·외/내판(없으면 외/내판 → 전체) 기록의 모델별 건수.
        // 같은 공장 이력으로 고른 제안은 그대로 두고, 그 묶음에 없던 모델을 '최근 추세' 후보로 뒤에 붙인다(예: 아산은 MPX3500뿐 → 중도·내판 MPX3600도 후보).
        List<PickOption> Trend(Func<EquipUnit, string[]> g, IEnumerable<EquipUnit>? only = null)
        {
            var pool = (only ?? recent).Where(u => u.YearNo >= 2022 && g(u).Length > 0).ToList();
            var procPart = string.Join("·", new[] { c.Process, part }.Where(s => s.Length > 0));
            foreach (var (sub, what) in new[] { (SameProc(SamePart(pool)).ToList(), procPart), (SamePart(pool).ToList(), part), (pool, "") })
                if (sub.Count > 0)
                    return Options(sub, g).Select(o => o with { Note = $"최근 추세: 2022년 이후 {(what.Length > 0 ? what + " " : "")}{o.Count}/{o.Total}건" }).ToList();
            return [];
        }
        ModelPick WithTrend(ModelPick p, List<PickOption> trend) =>
            p.How is "사양서 지정" or "선정 필요" ? p : p with
            {
                // 고른 모델의 최근 추세 건수도 이유에 덧붙인다(같은 공장 이력으로 골랐어도 요즘 다른 공장에서 얼마나 쓰는지 보이게)
                Why = trend.FirstOrDefault(t => t.Model == p.Model) is { Note: { } n } ? $"{p.Why} / {n}" : p.Why,
                // 이미 후보인 모델에도 최근 추세 건수를 덧붙이고(예: GM MPX3600 '같은 묶음 1/4건 / 최근 추세 … 6/7건'), 없던 모델은 뒤에 붙인다
                Options = [.. p.Options.Select((o, i) => i > 0 && trend.FirstOrDefault(t => t.Model == o.Model) is { Note: { } tn }
                    ? o with { Note = $"{(o.Total > 1 ? $"같은 묶음 {o.Count}/{o.Total}건" : "다른 후보")} / {tn}" } : o),
                    .. trend.Where(t => p.Options.All(o => o.Model != t.Model))],
            };
        // 기본 규칙(영업 기준)으로 정한 모델: 그 모델을 쓴 최근 사례를 근거로(같은 공정·외/내판 → 외/내판 → 전체),
        // 같은 공장 최근 모델이 다르면 다음 후보로 붙인다
        ModelPick RulePick(string kind, string role, string label, string model, string why, (int page, string line)? spec,
            List<EquipUnit> pool, Func<EquipUnit, string[]> g, List<EquipUnit> plantHist)
        {
            var withModel = pool.Where(u => g(u).Contains(model)).ToList();
            var recentWith = withModel.Where(u => u.YearNo >= 2022).ToList();
            var ex = SameProc(SamePart(recentWith)).ToList();
            if (ex.Count == 0) ex = SamePart(recentWith).ToList();
            if (ex.Count == 0) ex = recentWith;
            if (ex.Count == 0) ex = withModel;
            if (ex.Count == 0) ex = units.Where(u => g(u).Contains(model)).ToList();
            var opts = new List<PickOption>();
            if (ex.Count > 0) opts.Add(new PickOption(model, 1, 1, ex.OrderByDescending(u => u.YearNo).DistinctBy(u => u.ProjectDir).Take(3).ToList()));
            if (Latest(plantHist, g) is { } lt && !g(lt).Contains(model))
                opts.Add(new PickOption(g(lt).OrderBy(m => m).First(), 1, 1, [lt], $"같은 공장 최근 ({lt.Year})"));
            return new ModelPick(kind, role, label, model, "기본 규칙", why, spec?.page, spec?.line ?? "", opts);
        }
        var picks = new List<ModelPick>();

        // ---------- 로봇 ----------
        // 영업 기준(2026-10-27 사용자): 도장 라인(방폭)은 MPX 계열.
        //  - 스프레이: 기본 MPX3500, 사양서에 SPC·임베디드 시스템 언급이 있으면 MPX3600
        //  - 오프너: 도어 = MPX2600, 후드·트렁크 = MPX3500(가반하중이 큼 — 모델 뒤 숫자가 가반하중 등급)
        // 순서: 사양서 지정 → 기설치 이력('기설치와 동일') → 기본 규칙. 같은 공장 최근·최근 추세 모델은 아래 후보로 함께.
        var robotGiven = Given(blocks, RobotTopic, RobotModel);
        var exist = ExistingPhrase(blocks, RobotTopic);
        var spc = Mention(blocks, Spc);
        foreach (var role in c.Roles)
        {
            var label = role == "오프너" ? "오프너 로봇" : "스프레이 로봇";
            Func<EquipUnit, string[]> g = role == "오프너" ? Opener : Spray;
            if (robotGiven is { } rg)
            {
                picks.Add(new ModelPick("robot", role, label, Regex.Replace(rg.model.ToUpperInvariant(), @"[\s-]", ""), "사양서 지정", $"p{rg.page}: {rg.line}", rg.page, rg.line, []));
                continue;
            }
            var hist = units.Where(u => g(u).Length > 0).ToList();
            var plantHist = SamePlant(hist).Where(u => u.YearNo >= 2014).ToList();
            if (role == "오프너")
            {
                var kind = c.Area.Contains("후드") ? "후드" : c.Area.Contains("트렁크") ? "트렁크" : "도어";
                string T(EquipUnit u) => $"{u.ProjectFolder} {u.Unit}";
                var cand = hist.Where(u => u.YearNo >= 2018 && (kind switch
                {
                    "후드" => Hood.IsMatch(T(u)),
                    "트렁크" => Trunk.IsMatch(T(u)) && !Hood.IsMatch(T(u)),
                    _ => u.Part.Contains("내판") && !Hood.IsMatch(T(u)) && !Trunk.IsMatch(T(u)),
                })).ToList();
                if (exist is { } ex && Latest(SamePlant(cand), g) is { } lt)
                {
                    picks.Add(WithTrend(Make("robot", role, label, "기설치 이력", $"사양서 p{ex.page} '기설치와 동일' → 같은 공장 최근 {kind} 오프너", ex, lt, cand, g), Trend(g, cand)));
                    continue;
                }
                var model = kind == "도어" ? "MPX2600" : "MPX3500";
                var why = kind == "도어" ? "도어 오프너 = MPX2600 (도어는 가볍다 — 영업 기준)" : $"{kind} 오프너 = MPX3500 (후드·트렁크는 가반하중이 커서 큰 기종 — 영업 기준)";
                picks.Add(WithTrend(RulePick("robot", role, label, model, why, null, cand.Count > 0 ? cand : hist, g, plantHist), Trend(g, cand)));
                continue;
            }
            // 스프레이
            if (exist is { } ex2)
            {
                ModelPick? ep = null;
                foreach (var (pool, why) in new[] { (SameProc(SamePart(plantHist)), "같은 공장·같은 공정·같은 외/내판 최근"), (SamePart(plantHist), "같은 공장·같은 외/내판 최근"), (plantHist, "같은 공장 최근") })
                    if (Latest(pool, g) is { } lt) { ep = Make("robot", role, label, "기설치 이력", $"사양서 p{ex2.page} '기설치와 동일' → {why}", ex2, lt, plantHist, g); break; }
                if (ep is not null) { picks.Add(WithTrend(ep, Trend(g))); continue; }
            }
            var sprayModel = spc is null ? "MPX3500" : "MPX3600";
            var sprayWhy = spc is { } sp
                ? $"사양서 p{sp.page}에 SPC·임베디드 시스템 언급 → MPX3600 (영업 기준: 임베디드면 보통 MPX3600)"
                : "도장 라인 스프레이 기본 = MPX3500 (사양서에 SPC·임베디드 언급 없음 — 영업 기준)";
            picks.Add(WithTrend(RulePick("robot", role, label, sprayModel, sprayWhy, spc, hist, g, plantHist), Trend(g)));
        }

        // ---------- 도장기: 사양서 지정 → 도료·하전 규칙 ----------
        if (Given(blocks, AtomTopic, AtomModel) is { } ag)
            picks.Add(new ModelPick("atomizer", "", "도장기", EquipmentUnits.NormAtom(ag.model), "사양서 지정", $"p{ag.page}: {ag.line}", ag.page, ag.line, []));
        else
        {
            (List<EquipUnit> pool, string why) rule =
                c.Paint == "유성" || (c.Paint.Length == 0 && c.Charge == "내부하전") ? (recent.Where(u => u.Paint == "유성" && u.Charge is "내부하전" or "").ToList(), "유성(내부하전)")
                : c.Paint == "수성" && c.Charge == "외부하전" ? (units.Where(u => u.Paint == "수성" && u.Charge == "외부하전").ToList(), "수성·외부하전")
                : c.Paint == "수성" && c.Charge == "내부하전" ? (units.Where(u => u.Paint == "수성" && u.Charge == "내부하전").ToList(), "수성·내부하전(절연형)")
                : c.Paint == "수성" ? (units.Where(u => u.Paint == "수성" && (part.Length == 0 || u.Part.Contains(part))).ToList(), $"수성·{(part.Length > 0 ? part : "외/내판 미상")} (하전 미기재)")
                : c.Charge == "비정전" ? (units.Where(u => u.Charge == "비정전").ToList(), "비정전")
                : (SamePart(recent).ToList(), $"도료 미확인 → {(part.Length > 0 ? part : "?")} 2022년 이후 보통");
            var opts = Options(rule.pool, Atoms);
            picks.Add(opts.Count > 0
                ? new ModelPick("atomizer", "", "도장기", opts[0].Model, "경향 규칙", $"{rule.why}: {opts[0].Count}/{opts[0].Total}건이 이 모델", null, "", opts)
                : new ModelPick("atomizer", "", "도장기", "", "선정 필요", $"{rule.why}: 이력 없음", null, "", []));
        }

        // ---------- FGP: 사양서 지정 → 기본 PSMP 5CC FGP(영업 기준, 2026-10-27). 같은 공장 최근·최근 추세는 아래 후보로 ----------
        // 사양서의 특이 사항(분당 토출량·경화제·듀얼 FGP·압력센서 등)은 RowNotes가 FGP 기타 사항에 근거와 함께 적는다.
        if (Given(blocks, FgpTopic, FgpModel) is { } fg)
            picks.Add(new ModelPick("fgp", "", "FGP", EquipmentUnits.FgpOf(fg.line) ?? Regex.Replace(fg.model, @"\s+", "").ToUpperInvariant() + " FGP", "사양서 지정", $"p{fg.page}: {fg.line}", fg.page, fg.line, []));
        else
        {
            var hist = units.Where(u => u.Fgp.Length > 0).ToList();
            var plantHist = SamePlant(hist).Where(u => u.YearNo >= 2018).ToList();
            picks.Add(WithTrend(RulePick("fgp", "", "FGP", "PSMP 5CC FGP", "FGP 기본 = PSMP 5CC FGP (영업 기준 — 사양서에 FGP 용량·제조사 지정 없음)", null, hist, Fgp, plantHist), Trend(Fgp)));
        }

        // ---------- 벨컵 세정장치: 사양서에 요구가 있을 때만(없으면 넣지 않는다). 사양서 지정 → 같은 공장 최근 → 2022년 이후 보통 ----------
        if (EquipmentRows.MentionsCleaner(blocks))
        {
            if (Given(blocks, CleanerTopic, CleanerModel) is { } cg)
                picks.Add(new ModelPick("cleaner", "", "벨컵 세정장치", cg.model, "사양서 지정", $"p{cg.page}: {cg.line}", cg.page, cg.line, []));
            else
            {
                var hist = units.Where(u => Cleaner(u).Length > 0).ToList();
                var plantHist = SamePlant(hist).Where(u => u.YearNo >= 2018).ToList();
                if (Latest(plantHist, Cleaner) is { } lt)
                    picks.Add(WithTrend(Make("cleaner", "", "벨컵 세정장치", "공장 이력", "같은 공장 최근", null, lt, plantHist, Cleaner), Trend(Cleaner)));
                else
                {
                    var opts = Options(recent.Where(u => Cleaner(u).Length > 0).ToList(), Cleaner);
                    picks.Add(opts.Count > 0
                        ? new ModelPick("cleaner", "", "벨컵 세정장치", opts[0].Model, "경향 규칙", $"사양서에 벨컵 세정장치 요구, 모델 없음 → 2022년 이후 {opts[0].Count}/{opts[0].Total}건이 이 모델", null, "", opts)
                        : new ModelPick("cleaner", "", "벨컵 세정장치", "", "선정 필요", "사양서에 벨컵 세정장치 요구, 과거 이력 없음", null, "", []));
                }
            }
        }
        return picks;
    }

    public static string[] Cleaner(EquipUnit u) => u.Cleaner ?? [];

    public static string[] Spray(EquipUnit u) => u.Spray.Length > 0 ? u.Spray : u.RobotAny.Where(m => !SmallRobots.Contains(m)).ToArray();
    public static string[] Opener(EquipUnit u) => u.Opener.Length > 0 ? u.Opener : u.RobotAny.Where(m => m is "MPX2600" or "EPX2050").ToArray();
    public static string[] Atoms(EquipUnit u) => u.Atomizer;
    public static string[] Fgp(EquipUnit u) => u.Fgp;

    /// <summary>근거로 쓸 수 있는 기록: 이번 프로젝트 자신(번호가 같거나, 올린 사양서와 같은 파일이 있는 NAS 폴더)과 범퍼·부품 업체는 뺀다</summary>
    private static bool Usable(EquipUnit u, string? excludeCode, IReadOnlySet<string>? excludeDirs) =>
        !(excludeDirs?.Contains(u.ProjectDir) ?? false) &&
        !(excludeCode is not null && ((u.ProjectCode ?? "").Equals(excludeCode, StringComparison.OrdinalIgnoreCase) || u.ProjectFolder.Contains(excludeCode, StringComparison.OrdinalIgnoreCase)))
        && !EquipmentUnits.IsPartsMaker(u.ProjectFolder);

    private static EquipUnit? Latest(IEnumerable<EquipUnit> pool, Func<EquipUnit, string[]> g) =>
        pool.Where(u => g(u).Length > 0).OrderByDescending(u => u.YearNo).ThenBy(u => u.SourceKind == "summary" ? 0 : 1).FirstOrDefault();

    /// <summary>
    /// 묶음 안의 모델별 건수(많은 순), 모델마다 최근 사례 3건.
    /// 건수는 프로젝트 단위로 센다 — 한 사양정리에 공정이 여러 열이어도(조지아 등) 한 프로젝트는 한 표.
    /// </summary>
    private static List<PickOption> Options(IReadOnlyCollection<EquipUnit> pool, Func<EquipUnit, string[]> g)
    {
        var withModel = pool.Where(u => g(u).Length > 0).ToList();
        var projects = withModel.Select(u => u.ProjectDir).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return withModel.SelectMany(u => g(u).Distinct().Select(m => (m, u))).GroupBy(x => x.m)
            .Select(gr => new PickOption(gr.Key, gr.Select(x => x.u.ProjectDir).Distinct(StringComparer.OrdinalIgnoreCase).Count(), projects,
                gr.Select(x => x.u).OrderByDescending(u => u.YearNo).DistinctBy(u => u.ProjectDir).Take(3).ToList()))
            .OrderByDescending(o => o.Count).ThenByDescending(o => o.Examples[0].YearNo).Take(4).ToList();
    }

    /// <summary>이력에서 고른 결정: 고른 기록의 모델을 첫 후보로, 같은 묶음의 다른 모델을 다음 후보로</summary>
    private static ModelPick Make(string kind, string role, string label, string how, string why, (int page, string text)? spec,
        EquipUnit picked, IEnumerable<EquipUnit> pool, Func<EquipUnit, string[]> g)
    {
        var model = g(picked).OrderBy(m => m).First();
        var others = Options(pool.ToList(), g).Where(o => o.Model != model).ToList();
        var first = new PickOption(model, 1, 1, [picked]);
        return new ModelPick(kind, role, label, model, how, why, spec?.page, spec?.text ?? "", [first, .. others]);
    }

    // SPC·임베디드 시스템(도장 제어를 로봇 쪽에 넣는 방식) — 있으면 스프레이 MPX3600(영업 기준)
    private static readonly Regex Spc = new(@"\bSPC\b|임베디드|EMBEDDED", Opt);

    /// <summary>사양서에서 처음 나온 줄(쪽·짧은 원문)</summary>
    private static (int page, string line)? Mention(IReadOnlyList<Block> blocks, Regex re) =>
        blocks.FirstOrDefault(b => re.IsMatch(b.Text)) is { } b ? (b.UnitIndex, Short(b.Text)) : null;

    private static (string model, int page, string line)? Given(IReadOnlyList<Block> blocks, Regex topic, Regex model)
    {
        foreach (var b in blocks)
        {
            var t = b.Text;
            if (topic.IsMatch(t) && model.Match(t) is { Success: true } m && !Demolish.IsMatch(t))
                return (m.Value.Trim(), b.UnitIndex, Short(t));
        }
        return null;
    }

    private static (int page, string text)? ExistingPhrase(IReadOnlyList<Block> blocks, Regex topic)
    {
        foreach (var b in blocks)
            foreach (Match m in Existing.Matches(b.Text.Replace('\n', ' ')))
                if (topic.IsMatch(m.Groups[1].Value)) return (b.UnitIndex, Short(b.Text));
        return null;
    }

    private static string Short(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
