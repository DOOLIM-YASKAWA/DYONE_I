using System.Globalization;
using DyOne.Data.Entities;

namespace DyOne.Data.Modular;

/// <summary>
/// 모듈라 건의 '비교할 칸' 목록과 값 표기 규칙. 그룹웨어 동기화(Worker)와 DY ONE 수정(API)이 같은 규칙을 쓴다.
///
/// 두 벌 관리 규칙:
///   modular.build_gw.values = 마지막으로 읽은 그룹웨어 값,  modular.build = DY ONE 값.
///   동기화 때 '새 그룹웨어 값 ≠ 지난번 그룹웨어 값'인 칸만 DY ONE 값에 덮어쓴다(그룹웨어에서 안 바뀐 칸은 DY ONE 값 유지).
/// 값은 모두 문자열(없으면 null)로 비교한다: 날짜 yyyy-MM-dd, 복수 선택은 선택지 코드를 오름차순으로 ','로 이은 것.
/// </summary>
public static class ModularFieldSet
{
    public enum Kind { Text, Int, Date, Option, Member }

    /// <param name="Key">칸 키. 선택 목록은 opt:{list_key}, 담당자는 member:{role}</param>
    public record Field(string Key, string Label, Kind Kind);

    public static readonly IReadOnlyList<Field> Fields =
    [
        new("year", "연도", Kind.Int),
        new("project_code", "프로젝트 코드", Kind.Text),
        new("project_name", "프로젝트명", Kind.Text),
        new("status_id", "상태", Kind.Int),
        new("install_start", "모듈라 설치 시작", Kind.Date),
        new("commissioning_start", "모듈라 시운전 시작", Kind.Date),
        new("external_inspection", "외부검수", Kind.Text),
        new("ship_due", "출하 예정일", Kind.Date),
        new("ship_month", "출하 월", Kind.Text),
        new("origin_tool_receipt", "원점툴 입고 내역", Kind.Text),
        new("note", "비고", Kind.Text),
        new("customer", "고객사", Kind.Text),
        new("plant", "공장", Kind.Text),
        new("process", "공정", Kind.Text),
        new("process_detail", "공정세부", Kind.Text),
        new("robot_count", "로봇 수", Kind.Text),
        new("category_code", "구분", Kind.Int),
        new("region_code", "국내/해외", Kind.Int),
        new("main_issue", "주요이슈", Kind.Text),
        new("opt:install_location", "모듈라 설치위치", Kind.Option),
        new("opt:robot_model", "로봇기종", Kind.Option),
        new("opt:cable_robot_box", "로봇 케이블 길이 (로봇~BOX)", Kind.Option),
        new("opt:cable_box_rc", "로봇 케이블 길이 (BOX~RC)", Kind.Option),
        new("opt:comm", "통신 사양", Kind.Option),
        new("opt:applicator", "도장기 사양", Kind.Option),
        new("member:pm", "PM", Kind.Member),
        new("member:pl", "PL", Kind.Member),
        new("member:control", "제어", Kind.Member),
        new("member:design", "설계", Kind.Member),
        new("member:teach", "티쳐", Kind.Member),
    ];

    public static readonly IReadOnlyDictionary<string, Field> ByKey = Fields.ToDictionary(f => f.Key);

    public static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    public static string? Int(long? v) => v?.ToString(CultureInfo.InvariantCulture);
    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Codes(IEnumerable<int> codes)
    {
        var list = codes.Distinct().Order().ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    public static int[] ParseCodes(string? v) =>
        string.IsNullOrEmpty(v) ? [] : [.. v.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture))];

    public static DateOnly? ParseDate(string? v) =>
        DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static long? ParseLong(string? v) => long.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>로봇 수 원문(예: "4", "MPX3500 : 2 / MPX2600 : 2")에서 앞쪽 숫자만 정수로.</summary>
    public static int? LeadingInt(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var digits = new string([.. v.Trim().TakeWhile(char.IsAsciiDigit)]);
        return digits.Length is > 0 and <= 6 ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>DY ONE 값(modular.build + 선택값 + 담당자)을 비교용 값으로.</summary>
    public static Dictionary<string, string?> FromBuild(ModularBuild b, IEnumerable<ModularBuildOption> options, IEnumerable<ModularBuildMember> members)
    {
        var v = new Dictionary<string, string?>
        {
            ["year"] = Int(b.Year),
            ["project_code"] = Text(b.ProjectCode),
            ["project_name"] = Text(b.ProjectName),
            ["status_id"] = Int(b.StatusId),
            // 날짜로 못 바꾼 그룹웨어 원문이 있으면 원문으로 비교한다
            ["install_start"] = Date(b.InstallStart) ?? Text(b.InstallStartRaw),
            ["commissioning_start"] = Date(b.CommissioningStart) ?? Text(b.CommissioningStartRaw),
            ["external_inspection"] = Text(b.ExternalInspection),
            ["ship_due"] = Date(b.ShipDue),
            ["ship_month"] = Text(b.ShipMonth),
            ["origin_tool_receipt"] = Text(b.OriginToolReceipt),
            ["note"] = Text(b.Note),
            ["customer"] = Text(b.Customer),
            ["plant"] = Text(b.Plant),
            ["process"] = Text(b.Process),
            ["process_detail"] = Text(b.ProcessDetail),
            ["robot_count"] = Text(b.RobotCountRaw) ?? Int(b.RobotCount),
            ["category_code"] = Int(b.CategoryCode),
            ["region_code"] = Int(b.RegionCode),
            ["main_issue"] = Text(b.MainIssue),
        };
        var opts = options.Where(o => o.BuildId == b.Id).ToList();
        foreach (var f in Fields.Where(f => f.Kind == Kind.Option))
            v[f.Key] = Codes(opts.Where(o => o.ListKey == f.Key[4..]).Select(o => o.Code));
        var mem = members.Where(m => m.BuildId == b.Id).ToList();
        foreach (var f in Fields.Where(f => f.Kind == Kind.Member))
            v[f.Key] = Text(mem.FirstOrDefault(m => m.Role == f.Key[7..])?.NameRaw);
        return v;
    }

    /// <summary>두 값 묶음에서 값이 다른 칸 키(Fields 순서).</summary>
    public static List<string> Changed(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after) =>
        [.. Fields.Select(f => f.Key).Where(k => Get(before, k) != Get(after, k))];

    public static string? Get(IReadOnlyDictionary<string, string?> values, string key) => values.TryGetValue(key, out var v) ? v : null;
}
