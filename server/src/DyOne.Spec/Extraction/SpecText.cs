using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DyOne.Spec.Extraction;

/// <summary>
/// 셀/줄 텍스트 정리. dyone-pilot spec_extract.py 의 repair_mojibake·clean 과 같은 규칙.
/// - cp949 문서를 latin-1 로 잘못 읽어 깨진 한글(ÇÑ±Û…)을 되살린다.
/// - 줄바꿈 통일, 줄마다 연속 공백 1칸, 빈 줄 제거.
/// </summary>
public static class SpecText
{
    static SpecText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Regex Whitespace = new("[ \t 　]+", RegexOptions.Compiled);

    private static readonly Encoding Latin1Strict =
        Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static Encoding Cp949Strict =>
        Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private static bool IsHangul(char c) => c >= '가' && c <= '힣';
    private static bool IsLatinSupplement(char c) => c >= '¡' && c <= 'ÿ';

    public static string RepairMojibake(string text)
    {
        if (text.Length == 0 || (double)text.Count(IsLatinSupplement) / text.Length < 0.10)
            return text;
        string candidate;
        try
        {
            candidate = Cp949Strict.GetString(Latin1Strict.GetBytes(text));
        }
        catch (Exception e) when (e is EncoderFallbackException or DecoderFallbackException or ArgumentException)
        {
            return text;
        }
        if (candidate.Length == 0 || (double)candidate.Count(IsHangul) / candidate.Length < 0.20)
            return text;
        return candidate;
    }

    /// <summary>Python 의 str.strip() 처럼 앞뒤 유니코드 공백 제거.</summary>
    public static string PyStrip(string s) => s.Trim();

    public static string Clean(string? value)
    {
        if (value is null) return "";
        var text = RepairMojibake(value);
        text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\v", "\n");
        var lines = text.Split('\n').Select(l => PyStrip(Whitespace.Replace(l, " "))).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    /// <summary>Python str(float) 와 같은 표기(3.0, 0.1, 1e-05, 1e+16).</summary>
    public static string PyFloat(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e16)
            return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        if (r.Contains('E'))
        {
            // .NET "1.5E-05" → Python "1.5e-05", "1E+16" → "1e+16"
            var parts = r.Split('E');
            var exp = int.Parse(parts[1], CultureInfo.InvariantCulture);
            r = parts[0] + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        return r;
    }

    /// <summary>openpyxl 숫자: 정수로 쓰인 값은 int(“3”), 아니면 float 표기.</summary>
    public static string OpenpyxlNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e16 ? ((long)d).ToString(CultureInfo.InvariantCulture) : PyFloat(d);

    /// <summary>Python str(datetime) 표기: 2024-03-01 00:00:00(.ffffff)</summary>
    public static string PyDateTime(DateTime dt)
    {
        var s = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var micro = (int)(dt.Ticks % TimeSpan.TicksPerSecond / 10);
        return micro == 0 ? s : s + "." + micro.ToString("000000", CultureInfo.InvariantCulture);
    }
}
