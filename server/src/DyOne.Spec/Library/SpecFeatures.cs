using System.Text.RegularExpressions;

namespace DyOne.Spec.Library;

/// <summary>
/// 비슷한 프로젝트를 찾기 위한 특징 값. 글에서 규칙(정규식)으로 뽑는다(AI 없이).
/// 고객·공장·공정 단어, 로봇 모델, 도장기, PLC, 도료 방식, UPH.
/// </summary>
public sealed class SpecFeatures
{
    public SortedSet<string> Customers { get; init; } = [];
    public SortedSet<string> Plants { get; init; } = [];
    public SortedSet<string> Processes { get; init; } = [];
    public SortedSet<string> Robots { get; init; } = [];
    public SortedSet<string> Atomizers { get; init; } = [];
    public SortedSet<string> Plcs { get; init; } = [];
    public SortedSet<string> Paints { get; init; } = [];
    public int? Uph { get; set; }

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // (정규식, 대표 이름)
    private static readonly (Regex re, string name)[] CustomerRules =
    [
        (new(@"현대|HMC|HYUNDAI|H/KMC|HMMA|HMGMA", Opt), "현대"),
        (new(@"기아|KIA|KMC|H/KMC|KaGA|KMMG|KMS\b", Opt), "기아"),
        (new(@"한국\s*지엠|GMK|\bGM\b|쉐보레", Opt), "GM"),
        (new(@"르노|RENAULT|RSM", Opt), "르노"),
        (new(@"쌍용|KGM|KG\s*모빌리티", Opt), "KGM"),
        (new(@"테슬라|TESLA", Opt), "테슬라"),
        (new(@"에코\s*플라스틱", Opt), "에코플라스틱"),
        (new(@"서연\s*이화", Opt), "서연이화"),
        (new(@"현대\s*모비스|MOBIS", Opt), "현대모비스"),
        (new(@"동희|DONGHEE", Opt), "동희"),
        (new(@"태화", Opt), "태화"),
        (new(@"LG", RegexOptions.Compiled), "LG"),
    ];

    private static readonly string[] PlantWords =
    [
        "아산", "울산", "전주", "화성", "광주", "소하", "광명", "부평", "창원", "서산", "군산", "평택", "천안", "당진", "오토랜드",
        "앨라배마", "조지아", "몽고메리", "체코", "슬로바키아", "인도", "첸나이", "아난타푸르", "멕시코", "브라질", "러시아", "터키",
        "중국", "북경", "베이징", "염성", "미국", "베트남", "인도네시아", "싱가포르", "HMGMA", "메타플랜트",
    ];

    private static readonly (Regex re, string name)[] ProcessRules =
    [
        (new(@"중\s*도", Opt), "중도"), (new(@"상\s*도|베이스(?!\s*프레임)|BASE\s*COAT", Opt), "베이스"),
        (new(@"클리어|CLEAR", Opt), "클리어"), (new(@"하\s*도|프라이머|PRIMER", Opt), "하도"),
        (new(@"내\s*판|INTERIOR|INNER", Opt), "내판"), (new(@"외\s*판|EXTERIOR|OUTER", Opt), "외판"),
        (new(@"도어|DOOR", Opt), "도어"), (new(@"후드|트렁크|H/T|HOOD", Opt), "후드·트렁크"),
        (new(@"범퍼|BUMPER", Opt), "범퍼"), (new(@"실러|SEALER|PVC|언더\s*코트|UBC", Opt), "실러"),
        (new(@"데드너|DEADENER|LASD", Opt), "데드너"), (new(@"전착|\bED\b", Opt), "전착"),
        (new(@"휠|WHEEL", Opt), "휠"), (new(@"가니[쉬시]|GARNISH", Opt), "가니쉬"),
        (new(@"투\s*톤|TWO\s*-?\s*TONE", Opt), "투톤"), (new(@"후\s*보정|보\s*수|REPAIR", Opt), "보수·후보정"),
        (new(@"로딩|언로딩|LOADING", Opt), "로딩"), (new(@"검\s*사|INSPECTION|비\s*전|VISION", Opt), "검사"),
        (new(@"샌\s*딩|SANDING|연\s*마", Opt), "샌딩"), (new(@"하드\s*코팅|HARD\s*COAT", Opt), "하드코팅"), (new(@"노후\s*교체|로봇\s*교체|디그레이드|개\s*조", Opt), "교체·개조"),
        (new(@"신\s*공장|신\s*설|신\s*규\s*라인", Opt), "신설"),
    ];

    private static readonly Regex RobotRe = new(@"\b(MPX|EPX|MPL|MPK|PX|GP|HP|MH|AR|SDA|MS|ES|IRB)\s?-?\s?(\d{2,4})([A-Z]{0,3})\b", Opt);

    private static readonly (Regex re, string name)[] AtomizerRules =
    [
        (new(@"PPH\s*-?\s*(\d{3})", Opt), "PPH$1"), (new(@"NANO\s*BELL\s*(\d{3})?", Opt), "NANOBELL"),
        (new(@"\bRB\s*-?\s*(\d{3,4})\b", Opt), "ABB RB$1"), (new(@"ECO\s*BELL|에코\s*벨", Opt), "ECOBELL"),
        (new(@"AERO\s*BELL", Opt), "AEROBELL"), (new(@"G\d{2}\s*Bell|GA\s*\d{3}", Opt), "ABB GA"),
        (new(@"AIR\s*SPRAY|에어\s*스프레이|스프레이\s*건|SPRAY\s*GUN", Opt), "에어스프레이"),
        (new(@"정전\s*벨|ELECTROSTATIC", Opt), "정전벨"),
    ];

    private static readonly (Regex re, string name)[] PlcRules =
    [
        (new(@"MITSUBISHI|미쓰비시|MELSEC", Opt), "미쓰비시"), (new(@"SIEMENS|지멘스|S7-\d", Opt), "지멘스"),
        (new(@"\bLS\s*(PLC|산전|ELECTRIC)|XGT|XGK", Opt), "LS"), (new(@"ALLEN|ROCKWELL|CONTROL\s*LOGIX|\bAB\s*PLC", Opt), "AB"),
        (new(@"OMRON|옴론", Opt), "옴론"),
    ];

    private static readonly (Regex re, string name)[] PaintRules =
    [
        (new(@"수\s*성|WATER\s*BORNE|WB\b", Opt), "수성"), (new(@"유\s*성|유용성|SOLVENT|\bSB\b", Opt), "유성"),
        (new(@"2\s*K\b|경화제", Opt), "2K"), (new(@"내부\s*하전|INTERNAL\s*CHARGE", Opt), "내부하전"),
        (new(@"외부\s*하전|EXTERNAL\s*CHARGE", Opt), "외부하전"), (new(@"비\s*정전", Opt), "비정전"),
    ];

    private static readonly Regex UphRe = new(@"(\d{2,3})\s*UPH|UPH\s*[:：]?\s*\(?\s*(?:MAX\s*[:：]?\s*)?(\d{2,3})", Opt);

    /// <summary>
    /// head: 제목·파일명·프로젝트 이름처럼 '무엇에 관한 사양인가'를 말하는 짧은 글 — 고객·공장·공정은 여기서 우선 뽑는다.
    /// body: 본문 전체 — 로봇·도장기·PLC·도료·UPH는 본문에서 뽑는다. 공정 단어는 head에서 못 찾았을 때만 본문 앞부분에서 찾는다.
    /// </summary>
    public static SpecFeatures From(string head, string body)
    {
        var f = new SpecFeatures();
        foreach (var (re, name) in CustomerRules) if (re.IsMatch(head)) f.Customers.Add(name);
        foreach (var p in PlantWords) if (head.Contains(p, StringComparison.OrdinalIgnoreCase)) f.Plants.Add(p);
        foreach (var (re, name) in ProcessRules) if (re.IsMatch(head)) f.Processes.Add(name);
        if (f.Processes.Count == 0)
        {
            var front = body.Length > 3000 ? body[..3000] : body;
            foreach (var (re, name) in ProcessRules) if (re.IsMatch(front)) f.Processes.Add(name);
        }
        var all = head + "\n" + body;
        foreach (Match m in RobotRe.Matches(all))
        {
            var family = m.Groups[1].Value.ToUpperInvariant();
            // GP·HP·MS 등은 일반 숫자와 헷갈리기 쉬워 3자리 이상 모델만 인정(GP25 같은 핸들링 로봇은 두 자리도 흔함 → GP/HP는 2자리 허용)
            if (m.Groups[2].Value.Length < 3 && family is not ("GP" or "HP")) continue;
            f.Robots.Add(family + m.Groups[2].Value + m.Groups[3].Value.ToUpperInvariant());
        }
        foreach (var (re, name) in AtomizerRules)
            foreach (Match m in re.Matches(all)) f.Atomizers.Add(m.Result(name).Trim());
        foreach (var (re, name) in PlcRules) if (re.IsMatch(all)) f.Plcs.Add(name);
        foreach (var (re, name) in PaintRules) if (re.IsMatch(all)) f.Paints.Add(name);
        var uphs = UphRe.Matches(all).Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value))
            .Where(v => v is >= 5 and <= 120).ToList();
        if (uphs.Count > 0) f.Uph = uphs.Max();
        return f;
    }
}
