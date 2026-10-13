using System.Globalization;

namespace DyOne.Data.Works;

/// <summary>
/// 그룹웨어 앱(works.*) 칸 값 규칙 — Worker(그룹웨어 → 비교용 값)와 API(DY ONE 입력 → 비교용 값)가 같은 규칙을 쓴다.
/// 비교용 값은 문자열(없으면 null): 날짜 yyyy-MM-dd, 일시 yyyy-MM-dd HH:mm, 숫자는 불필요한 0 없이,
/// 선택은 선택지 코드, 복수 선택·사용자·참조 문서·파일은 번호를 오름차순 ','로 이은 것.
/// 화면에는 display(선택지 이름, 사용자 이름, 참조 문서 제목)를 보여 준다.
/// </summary>
public static class WorksValues
{
    /// <summary>값이 아니라 문서 정보로 다루는 칸(등록일·수정일·수정자)</summary>
    public static readonly HashSet<string> MetaCids = ["create_date", "update_date", "updater"];

    /// <summary>폼 배치용 칸(값이 없음)</summary>
    public static readonly HashSet<string> LayoutTypes = ["canvas", "label", "columns", "column", "hr"];

    /// <summary>DY ONE에서 직접 고칠 수 있는 형식. 사용자·참조 문서·참조 값·파일은 그룹웨어 값 그대로(읽기 전용).</summary>
    public static bool IsEditable(string fieldType, string? valueType) =>
        fieldType is not ("field_mapping" or "creator" or "org" or "applet_docs" or "file")
        && valueType is "STEXT" or "TEXT" or "DATE" or "DATETIME" or "NUMBER" or "SELECT" or "SELECTS";

    public static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? DateTimeText(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public static string? Number(double? v) => v?.ToString("0.############", CultureInfo.InvariantCulture);
    public static string? Codes(IEnumerable<long> codes)
    {
        var list = codes.Distinct().Order().ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    public static long[] ParseCodes(string? v) =>
        string.IsNullOrWhiteSpace(v) ? [] : [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.Parse(x.Trim(), CultureInfo.InvariantCulture))];

    /// <summary>
    /// DY ONE 입력 → 비교용 값. 형식이 맞지 않으면 error에 이유를 돌려준다.
    /// options = 이 필드의 선택지 코드(선택·복수 선택만)
    /// </summary>
    public static string? FromInput(string? valueType, string? input, IReadOnlyCollection<long> options, out string? error)
    {
        error = null;
        var raw = Text(input);
        if (raw is null) return null;
        switch (valueType)
        {
            case "DATE":
                if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Date(d);
                error = "날짜 형식(yyyy-MM-dd)이 아닙니다."; return null;
            case "DATETIME":
                if (System.DateTime.TryParse(raw.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return DateTimeText(dt);
                error = "일시 형식(yyyy-MM-dd HH:mm)이 아닙니다."; return null;
            case "NUMBER":
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return Number(n);
                error = "숫자가 아닙니다."; return null;
            case "SELECT":
                if (long.TryParse(raw, CultureInfo.InvariantCulture, out var code) && options.Contains(code)) return code.ToString(CultureInfo.InvariantCulture);
                error = "선택지에 없는 값입니다."; return null;
            case "SELECTS":
                try
                {
                    var codes = ParseCodes(raw);
                    if (codes.All(options.Contains)) return Codes(codes);
                }
                catch (FormatException) { }
                error = "선택지에 없는 값이 있습니다."; return null;
            default:
                return raw.Length > 4000 ? raw[..4000] : raw;
        }
    }

    /// <summary>선택·복수 선택 값의 표시 글자</summary>
    public static string? OptionDisplay(string? valueType, string? value, IReadOnlyDictionary<long, string> labels)
    {
        if (value is null) return null;
        return valueType switch
        {
            "SELECT" or "SELECTS" => string.Join(", ", ParseCodes(value).Select(c => labels.TryGetValue(c, out var l) ? l : c.ToString(CultureInfo.InvariantCulture))),
            _ => value,
        };
    }
}
