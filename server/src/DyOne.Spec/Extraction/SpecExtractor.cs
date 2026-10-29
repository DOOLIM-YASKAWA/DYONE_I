using System.Text;
using ClosedXML.Excel;
using ExcelDataReader;

namespace DyOne.Spec.Extraction;

public sealed class SpecExtractorOptions
{
    /// <summary>PDF 표 인식에 쓸 Python 실행 파일(예: "python"). 비우면 .NET(PdfPig) 대체 방식만 쓴다.</summary>
    public string? PythonPath { get; set; }
    public int MaxRows { get; set; } = 5000;
}

/// <summary>
/// 사양서 파일 → Block 목록. dyone-pilot server/spec_extract.py 의 extract_bytes 를 옮겼다.
/// 형식별 규칙(숨김 시트 제외, 시트당 5000행, 병합 셀 처리, 문단 다음 표 순서 등)을 Python 쪽과 똑같이 맞춘다.
/// 원본은 읽기만 한다.
/// </summary>
public sealed class SpecExtractor(SpecExtractorOptions? options = null)
{
    public static readonly string[] Supported = [".pdf", ".pptx", ".xlsx", ".xlsm", ".xls", ".docx", ".txt"];
    private readonly SpecExtractorOptions _options = options ?? new();

    static SpecExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// PDF를 표 인식 없이 '보이는 줄' 단위로(같은 높이 단어를 한 줄로). 표 인식이 여러 줄을 한 칸으로 합쳐
    /// 줄마다의 수량이 사라질 때(예: "3) 공압판넬 … 8 SET") 같은 줄의 값을 찾는 데 쓴다. PDF가 아니면 빈 목록.
    /// </summary>
    public List<Block> ExtractPdfLines(byte[] data, string fileName)
    {
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return [];
        try { return PdfExtract.PdfPigLines(data).Item2; }
        catch (Exception) { return []; }
    }

    public Extraction Extract(byte[] data, string fileName)
    {
        var suffix = Path.GetExtension(fileName).ToLowerInvariant();
        var name = Path.GetFileName(fileName);
        switch (suffix)
        {
            case ".pdf":
                var (units, blocks, engine) = PdfExtract.Read(data, _options.PythonPath);
                return new Extraction(name, suffix, units, blocks) { PdfEngine = engine };
            case ".pptx": { var (u, b) = OfficeExtract.Pptx(data); return new Extraction(name, suffix, u, b); }
            case ".docx": { var (u, b) = OfficeExtract.Docx(data); return new Extraction(name, suffix, u, b); }
            case ".xlsx" or ".xlsm": { var (u, b) = Xlsx(data, _options.MaxRows); return new Extraction(name, suffix, u, b); }
            case ".xls": { var (u, b) = Xls(data, _options.MaxRows); return new Extraction(name, suffix, u, b); }
            case ".txt": { var (u, b) = Txt(data); return new Extraction(name, suffix, u, b); }
            default:
                throw new NotSupportedException(
                    $"지원하지 않는 형식입니다 ({(suffix.Length > 0 ? suffix : "확장자 없음")}). 지원: {string.Join(", ", Supported)}");
        }
    }

    private static void PopTrailingEmpty(List<string> cells)
    {
        while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
    }

    // ---- xlsx: openpyxl(read_only, data_only) 와 같은 값 표기
    private static (int, List<Block>) Xlsx(byte[] data, int maxRows)
    {
        using var stream = new MemoryStream(data);
        using var wb = new XLWorkbook(stream);
        var blocks = new List<Block>();
        var sheets = wb.Worksheets.ToList();
        for (var s = 0; s < sheets.Count; s++)
        {
            var ws = sheets[s];
            if (ws.Visibility != XLWorksheetVisibility.Visible) continue;
            var label = Trunc(ws.Name, 80);
            foreach (var row in ws.RowsUsed(XLCellsUsedOptions.Contents))
            {
                var rowNo = row.RowNumber();
                if (rowNo > maxRows) break;
                var last = row.LastCellUsed(XLCellsUsedOptions.Contents)?.Address.ColumnNumber ?? 0;
                var cells = new List<string>(last);
                for (var c = 1; c <= last; c++) cells.Add(SpecText.Clean(XlsxValue(row.Cell(c))));
                PopTrailingEmpty(cells);
                if (cells.Any(x => x.Length > 0)) blocks.Add(new Block("sheet", s + 1, label, rowNo, 1, cells));
            }
        }
        return (sheets.Count, blocks);
    }

    private static string? XlsxValue(IXLCell cell)
    {
        var v = cell.HasFormula ? cell.CachedValue : cell.Value;
        return v.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Text => v.GetText(),
            XLDataType.Number => SpecText.OpenpyxlNumber(v.GetNumber()),
            XLDataType.Boolean => v.GetBoolean() ? "True" : "False",
            XLDataType.DateTime => SpecText.PyDateTime(v.GetDateTime()),
            XLDataType.TimeSpan => PyTime(v.GetTimeSpan()),
            XLDataType.Error => ErrorText(v.GetError()),
            _ => v.ToString(),
        };
    }

    private static string PyTime(TimeSpan t) =>
        $"{(int)t.TotalHours % 24:00}:{t.Minutes:00}:{t.Seconds:00}" + (t.Milliseconds > 0 ? $".{t.Milliseconds * 1000:000000}" : "");

    private static string ErrorText(XLError e) => e switch
    {
        XLError.NullValue => "#NULL!",
        XLError.DivisionByZero => "#DIV/0!",
        XLError.IncompatibleValue => "#VALUE!",
        XLError.CellReference => "#REF!",
        XLError.NameNotRecognized => "#NAME?",
        XLError.NumberInvalid => "#NUM!",
        XLError.NoValueAvailable => "#N/A",
        _ => "#ERR",
    };

    // ---- xls: xlrd(formatting_info=False) 와 같은 값 표기 — 숫자·날짜는 float(“3.0”, 날짜는 일련번호)
    private static (int, List<Block>) Xls(byte[] data, int maxRows)
    {
        using var stream = new MemoryStream(data);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        var blocks = new List<Block>();
        var sheetNo = 0;
        do
        {
            sheetNo++;
            var visible = string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase);
            var label = Trunc(reader.Name ?? "", 80);
            var r = 0;
            while (reader.Read())
            {
                r++;
                if (!visible || r > maxRows) continue;
                var cells = new List<string>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++) cells.Add(SpecText.Clean(XlsValue(reader.GetValue(i))));
                PopTrailingEmpty(cells);
                if (cells.Any(x => x.Length > 0)) blocks.Add(new Block("sheet", sheetNo, label, r, 1, cells));
            }
        } while (reader.NextResult());
        return (reader.ResultsCount, blocks);
    }

    private static string? XlsValue(object? v) => v switch
    {
        null => null,
        string s => s,
        double d => SpecText.PyFloat(d),
        float f => SpecText.PyFloat(f),
        int i => SpecText.PyFloat(i),
        long l => SpecText.PyFloat(l),
        bool b => b ? "1" : "0",
        DateTime dt => SpecText.PyFloat(dt.ToOADate()),
        TimeSpan ts => SpecText.PyFloat(ts.TotalDays),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    // ---- txt: utf-8 → cp949 → utf-16 순서로 시도
    private static (int, List<Block>) Txt(byte[] data)
    {
        string? text = null;
        foreach (var enc in new[] { new UTF8Encoding(false, true), Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), (Encoding)new UnicodeEncoding(false, true, true) })
        {
            try { text = enc.GetString(data); break; } catch (DecoderFallbackException) { }
        }
        text ??= Encoding.UTF8.GetString(data);
        var lines = SpecText.Clean(text).Split('\n').Where(l => l.Length > 0).ToList();
        return (1, lines.Select((l, i) => new Block("section", 1, "", i + 1, 0, [l])).ToList());
    }

    internal static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
