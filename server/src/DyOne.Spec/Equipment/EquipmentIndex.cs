using System.Text.RegularExpressions;
using DyOne.Spec.Library;

namespace DyOne.Spec.Equipment;

/// <summary>
/// 과거 프로젝트에서 찾은 설비 모델 한 건(로봇·도장기·FGP·CCV).
/// Role: 스프레이·오프너 등(사양정리 표에 적혀 있을 때만, 도면 제목에는 역할이 없어 빈 값).
/// SourceKind: summary(당사 사양정리 표) | drawing(도면 파일 제목). 사양정리가 더 믿을 만하다.
/// </summary>
public sealed record EquipRecord(
    string Id, string Kind, string Model, string Role,
    string Plant, string Process, string[] Parts,
    string Year, string ProjectFolder, string? ProjectCode, string ProjectDir,
    string SourceKind, string FilePath, string FileName, string Excerpt);

/// <summary>프로젝트(또는 이번 사양서)가 어느 공장·공정·부위인지</summary>
public sealed record ProjectContext(string Plant, string Process, string[] Parts)
{
    public string Label => string.Join(" ", new[] { Plant, Process, string.Join("·", Parts) }.Where(s => s.Length > 0));
}

/// <summary>
/// 설비 이력 색인: NAS 프로젝트 폴더의 도면 파일 제목(ROBOT MANIPULATOR FOR MPX-3500 …)과
/// 사양정리 표(Robot·Atomizer·FGP·Color 줄)에서 설비 모델을 모은다. 파일 내용은 열지 않는다(제목만). 읽기만 한다.
/// 사양서가 "기설치와 동일" 또는 모델을 정하지 않은 항목에, 비슷한 과거 프로젝트 모델을 근거와 함께 제안하는 데 쓴다.
/// </summary>
public static class EquipmentIndex
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // 공장: "광주 2공장", "광)2도장", "화성3공장" → 광주2 / 화성3. 번호가 없으면 지명만.
    private static readonly string[] PlantNames =
        ["광주", "화성", "아산", "울산", "전주", "소하", "광명", "부평", "창원", "서산", "군산", "평택", "천안", "당진", "앨라배마", "조지아", "체코", "슬로바키아", "인도", "멕시코", "브라질"];
    private static readonly Regex PlantNo = new(@"(광주|화성|아산|울산|전주|소하|광명|부평|창원|서산|군산|평택)\s*(?:공장)?\s*(\d)\s*(?:공장|도장|라인|PLANT)?", Opt);
    private static readonly Regex PlantAbbr = new(@"(광|화|아|울)\)\s*(\d)", Opt);
    private static readonly Dictionary<string, string> AbbrName = new() { ["광"] = "광주", ["화"] = "화성", ["아"] = "아산", ["울"] = "울산" };

    private static readonly (Regex re, string name)[] ProcessRules =
    [
        (new(@"중\s*도|PRIMER|SURFACER", Opt), "중도"),
        (new(@"클리어|CLEAR", Opt), "클리어"),
        (new(@"베이스(?!\s*프레임)|BASE\s*COAT|상\s*도", Opt), "베이스"),
        (new(@"하\s*도|전\s*착", Opt), "하도"),
        (new(@"실러|SEALER|PVC|UBC|언더\s*코트", Opt), "실러"),
        (new(@"범퍼|BUMPER", Opt), "범퍼"),
    ];
    private static readonly (Regex re, string name)[] PartRules =
    [
        (new(@"내\s*판|INTERIOR|INNER", Opt), "내판"), (new(@"외\s*판|EXTERIOR|OUTER", Opt), "외판"),
        (new(@"도어|DOOR", Opt), "도어"), (new(@"후드|HOOD|H/T|트렁크", Opt), "후드"),
        (new(@"T\s*/\s*G|T-?GATE|테일\s*게이트|TAIL\s*GATE", Opt), "테일게이트"),
    ];

    // 모델 규칙
    private static readonly Regex RobotModel = new(@"\b((?:MPX|EPX|MPL|PX)\s*-?\s*\d{3,4}[A-Z]?)\b", Opt);
    private static readonly Regex DrawingRobot = new(
        @"MANIPULATOR(?:\s*FOR)?\s*-?\s*((?:MPX|EPX|PX)\s*-?\s*\d{3,4}[A-Z]?)|((?:MPX|EPX|PX)\s*-?\s*\d{3,4}[A-Z]?)\s*MANIPULATOR", Opt);
    private static readonly Regex AtomizerModel = new(@"((?:SAMES\s*)?PPH\s*-?\s*\d{3}(?:\s*(?:SB|HI\s*-?\s*TE|SR))?|NANO\s*BELL\s*\d*|ECO\s*BELL\s*\d*)", Opt);
    private static readonly Regex DrawingFgp = new(@"GEAR\s*PUMP\s*FOR\s*(\d+(?:\.\d+)?)\s*CC", Opt);
    private static readonly Regex SheetFgp = new(@"(\d+(?:\.\d+)?)\s*CC\b(?!\s*/\s*min)", Opt);
    private static readonly Regex DrawingCcv = new(@"COLOR\s*CHANGE\s*VALVE\s*-?\s*(\d{1,2})\s*COLOR", Opt);
    private static readonly Regex SheetCcv = new(@"(\d{1,2})\s*(?:칼라|컬러|COLORS?)\b", Opt);
    private static readonly Regex RoleWord = new(@"(오프너|OPENER|스프레이|SPRAY|도장\s*로봇|도장)", Opt);

    private static readonly Regex YearDir = new(@"^\d{4}", Opt);
    private static readonly Regex CodeDigits = new(@"(?:Y?DLP?|DL)[-\s]?(\d{2})-?(\d{3})", Opt);
    private static readonly Regex CodeRe = new(@"\b[A-Z]{2,4}-\d{2}-\d{3}\b", Opt);
    private static readonly Regex ReferenceDir = new(@"참고|기존|이전|\bold\b|백업|backup", Opt);
    private static readonly string[] DrawingExts = [".pdf", ".dwg", ".xls", ".xlsx"];

    public static ProjectContext ContextOf(string text)
    {
        var plant = "";
        if (PlantNo.Match(text) is { Success: true } m) plant = m.Groups[1].Value + m.Groups[2].Value;
        else if (PlantAbbr.Match(text) is { Success: true } a) plant = AbbrName[a.Groups[1].Value] + a.Groups[2].Value;
        else plant = PlantNames.FirstOrDefault(p => text.Contains(p)) ?? "";
        var process = ProcessRules.FirstOrDefault(r => r.re.IsMatch(text)).name ?? "";
        var parts = PartRules.Where(r => r.re.IsMatch(text)).Select(r => r.name).ToArray();
        return new ProjectContext(plant, process, parts);
    }

    /// <summary>NAS 프로젝트 폴더 목록: {연도}\{고객 또는 분류}\{프로젝트}</summary>
    public static List<(string dir, string year, string customer)> DiscoverProjects(IEnumerable<string> roots)
    {
        var list = new List<(string, string, string)>();
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var year in SafeDirs(root).Where(d => YearDir.IsMatch(d.Name)))
        foreach (var customer in SafeDirs(year.FullName))
        foreach (var project in SafeDirs(customer.FullName))
            list.Add((project.FullName, year.Name[..4], customer.Name));
        return list;
    }

    /// <summary>프로젝트 한 건의 도면 파일 제목에서 설비 모델을 모은다. 다른 프로젝트 번호가 붙은 파일·참고/기존 폴더는 뺀다.</summary>
    public static List<EquipRecord> FromDrawings(string projectDir, string year, string customer)
    {
        var folder = Path.GetFileName(projectDir);
        var ctx = ContextOf($"{customer} {folder}");
        var code = CodeRe.Match(folder) is { Success: true } c ? c.Value : null;
        var digits = CodeDigits.Match(folder) is { Success: true } d ? d.Groups[1].Value + d.Groups[2].Value : null;
        var result = new List<EquipRecord>();
        foreach (var f in SafeFiles(projectDir))
        {
            if (!DrawingExts.Contains(f.Extension.ToLowerInvariant()) || f.Name.StartsWith("~$")) continue;
            var rel = Path.GetRelativePath(projectDir, f.FullName);
            if (ReferenceDir.IsMatch(Path.GetDirectoryName(rel) ?? "")) continue;
            if (digits is not null && CodeDigits.Match(f.Name) is { Success: true } fc && fc.Groups[1].Value + fc.Groups[2].Value != digits) continue;

            var name = Path.GetFileNameWithoutExtension(f.Name);
            void Add(string kind, string model) => result.Add(new EquipRecord("", kind, model, "", ctx.Plant, ctx.Process, ctx.Parts,
                year, folder, code, projectDir, "drawing", f.FullName, f.Name, name));
            if (DrawingRobot.Match(name) is { Success: true } r) Add("robot", NormRobot(r.Groups[1].Success ? r.Groups[1].Value : r.Groups[2].Value));
            if (Regex.IsMatch(name, "BELL", RegexOptions.IgnoreCase) && AtomizerModel.Match(name) is { Success: true } at) Add("atomizer", NormAtomizer(at.Value));
            if (DrawingFgp.Match(name) is { Success: true } && EquipmentUnits.FgpOf(name) is { } fgpName) Add("fgp", fgpName);
            if (DrawingCcv.Match(name) is { Success: true } cv) Add("ccv", $"{int.Parse(cv.Groups[1].Value)}칼라");
        }
        return Dedupe(result);
    }

    /// <summary>사양정리 표(라이브러리)에서 설비 모델을 모은다. 로봇 줄은 '스프레이: MPX3500 * 2ea, 오프너: MPX3500 * 1ea'처럼 역할을 함께 읽는다.</summary>
    public static List<EquipRecord> FromSummary(LibraryDoc doc)
    {
        if (doc.Summary is null || !doc.HasSheet) return [];
        var ctx = ContextOf($"{doc.CustomerFolder} {doc.ProjectFolder} {doc.Summary.FileName} {doc.Rows.FirstOrDefault(r => r.Key == "process")?.Value}");
        var result = new List<EquipRecord>();
        void Add(string kind, string model, string role, string excerpt) => result.Add(new EquipRecord("", kind, model, role, ctx.Plant, ctx.Process, ctx.Parts,
            doc.Year.Length >= 4 ? doc.Year[..4] : doc.Year, doc.ProjectFolder, doc.ProjectCode, doc.ProjectDir,
            "summary", doc.Summary.Path, doc.Summary.FileName, Short(excerpt)));

        foreach (var row in doc.Rows)
        {
            var text = row.Value;
            switch (row.Key)
            {
                case "robot":
                    foreach (Match m in RobotModel.Matches(text))
                    {
                        // 모델 앞 30자 안의 마지막 역할 단어(도장로봇: MPX3500 * 2ea, 오프너: MPX3500 * 1ea)
                        var before = text[Math.Max(0, m.Index - 30)..m.Index];
                        var roles = RoleWord.Matches(before);
                        var role = roles.Count == 0 ? "" : Role(roles[^1].Value);
                        Add("robot", NormRobot(m.Value), role, text);
                    }
                    break;
                case "atomizer":
                    foreach (Match m in AtomizerModel.Matches(text)) Add("atomizer", NormAtomizer(m.Value), "", text);
                    break;
                case "fgp":
                    if (SheetFgp.Match(text) is { Success: true } && EquipmentUnits.FgpOf(text) is { } fgpName) Add("fgp", fgpName, "", text);
                    break;
                case "color":
                    if (SheetCcv.Match(text) is { Success: true } cv)
                        Add("ccv", $"{int.Parse(cv.Groups[1].Value)}칼라" + (Regex.IsMatch(text, "듀얼|dual|double", RegexOptions.IgnoreCase) ? " 듀얼" : ""), "", text);
                    break;
            }
        }
        return Dedupe(result);
    }

    public static string Role(string word) =>
        Regex.IsMatch(word, "오프너|OPENER", RegexOptions.IgnoreCase) ? "오프너" : "스프레이";

    private static string NormRobot(string s) => Regex.Replace(s.ToUpperInvariant(), @"[\s-]", "");
    private static string NormAtomizer(string s)
    {
        var t = Regex.Replace(s.ToUpperInvariant(), @"\s+", " ").Trim();
        t = Regex.Replace(t, @"PPH\s*-?\s*(\d{3})", "PPH$1");
        return Regex.Replace(t, @"HI\s*-?\s*TE", "HI-TE");
    }
    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + "…";

    private static List<EquipRecord> Dedupe(List<EquipRecord> list) =>
        list.GroupBy(r => (r.Kind, r.Model, r.Role, r.SourceKind))
            .Select(g => g.OrderByDescending(r => r.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).First())
            .Select(r => r with { Id = LibraryScanner.IdOf($"{r.FilePath}|{r.Kind}|{r.Model}|{r.Role}") })
            .ToList();

    private static IEnumerable<DirectoryInfo> SafeDirs(string path)
    {
        try { return new DirectoryInfo(path).EnumerateDirectories().ToList(); }
        catch (Exception) { return []; }
    }

    private static IEnumerable<FileInfo> SafeFiles(string path)
    {
        try { return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (Exception) { return []; }
    }
}
