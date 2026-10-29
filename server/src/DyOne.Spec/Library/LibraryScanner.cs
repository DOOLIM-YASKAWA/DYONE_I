using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Library;

/// <summary>NAS 파일 한 개(경로·이름·수정 날짜·크기)</summary>
public sealed class LibFile
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime Modified { get; set; }
    public long Size { get; set; }
    /// <summary>같은 종류 파일 수(사양정리 rev1~rev7이면 7)</summary>
    public int Revisions { get; set; } = 1;

    public static LibFile Of(FileInfo f, int revisions = 1) =>
        new() { Path = f.FullName, FileName = f.Name, Modified = f.LastWriteTimeUtc, Size = f.Length, Revisions = revisions };

    public bool SameAs(FileInfo f) =>
        string.Equals(Path, f.FullName, StringComparison.OrdinalIgnoreCase) && Size == f.Length && Modified == f.LastWriteTimeUtc;
}

/// <summary>
/// NAS 과거 프로젝트 한 건: 고객 사양서(고객이 준 것) + 사양정리(당사 내부 정리본) + 회의록 목록.
/// 고객 사양서끼리 비교해 비슷한 프로젝트를 찾고, 그 프로젝트의 사양정리를 본보기로 새 사양정리 초안을 만든다.
/// </summary>
public sealed class LibraryDoc
{
    public string Id { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string Year { get; set; } = "";
    public string CustomerFolder { get; set; } = "";
    public string ProjectFolder { get; set; } = "";
    public string? ProjectCode { get; set; }

    /// <summary>사양정리(마지막 판). 없으면 null</summary>
    public LibFile? Summary { get; set; }
    public string Title { get; set; } = "";
    public List<SheetRow> Rows { get; set; } = [];
    public SpecFeatures Features { get; set; } = new();
    public string? SummaryError { get; set; }

    /// <summary>고객 사양서(사양서 폴더의 사양정리가 아닌 문서 중 글자가 있는 가장 최근 것). 없으면 null</summary>
    public LibFile? Customer { get; set; }
    public SpecFeatures? CustomerFeatures { get; set; }
    public Dictionary<string, SpecValue> CustomerValues { get; set; } = [];
    public string? CustomerError { get; set; }

    /// <summary>회의록(프로젝트 폴더의 '회의' 폴더 + 사양서 폴더의 회의록 파일), 최근 것부터 최대 10개</summary>
    public List<LibFile> Minutes { get; set; } = [];

    public bool HasSheet => Rows.Count > 0 && Rows.Count(r => r.Value.Length > 0) >= 3;
}

/// <summary>찾은 프로젝트 폴더와 그 안의 후보 파일들</summary>
public sealed record LibraryCandidate(
    string ProjectDir, string Year, string Customer,
    FileInfo? Summary, int SummaryRevisions, List<FileInfo> CustomerFiles, List<FileInfo> Minutes);

/// <summary>
/// NAS 프로젝트 폴더에서 고객 사양서·사양정리·회의록을 찾고 읽는다. 읽기만 한다.
/// 폴더 구조: {연도}_PROJECT \ {고객} \ {프로젝트} \ (이름에 '사양'이 든 폴더) \ …
/// </summary>
public static class LibraryScanner
{
    private static readonly string[] SummaryExts = [".pptx", ".xlsx", ".xls", ".pdf"];
    private static readonly string[] CustomerExts = [".pdf", ".docx", ".xlsx", ".xls", ".pptx"];
    private static readonly string[] MinuteExts = [".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".hwp", ".txt", ".msg"];
    private static readonly Regex SummaryName = new(@"사양\s*정리", RegexOptions.Compiled);
    private static readonly Regex MinuteName = new(@"회의|미팅|meeting|킥\s*오프|kick\s*-?\s*off", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // 고객 사양서로 보이는 이름(있으면 우선) / 고객 사양서가 아닌 것(설명회 자료·견적·도면 등은 뒤로)
    private static readonly Regex SpecLike = new(@"사양서|spec|시방|구매\s*요구|SOW|requirement", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotSpec = new(@"설명회|회의|견적|도면|layout|레이아웃|일정|schedule|비교|검토|답변|질의|Q&A", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearDir = new(@"^\d{4}", RegexOptions.Compiled);
    private static readonly Regex CodeRe = new(@"\b[A-Z]{2,4}-\d{2}-\d{3}\b", RegexOptions.Compiled);
    private const long MaxBytes = 60L * 1024 * 1024;
    private const int MinTextChars = 800; // 이보다 글자가 적으면 스캔 PDF로 보고 다음 후보를 읽는다

    public static List<LibraryCandidate> Discover(IEnumerable<string> roots)
    {
        var result = new List<LibraryCandidate>();
        foreach (var root in roots.Where(Directory.Exists))
        foreach (var year in SafeDirs(root).Where(d => YearDir.IsMatch(d.Name)))
        foreach (var customer in SafeDirs(year.FullName))
        foreach (var project in SafeDirs(customer.FullName))
        {
            var subDirs = SafeDirs(project.FullName).ToList();
            var specFiles = subDirs.Where(d => d.Name.Contains("사양"))
                .SelectMany(d => SafeFiles(d.FullName))
                .Where(f => !f.Name.StartsWith("~$") && f.Length > 0)
                .ToList();
            if (specFiles.Count == 0) continue;

            string Rel(FileInfo f) => System.IO.Path.GetRelativePath(project.FullName, f.FullName);
            // 사양정리: pptx·엑셀을 pdf보다 먼저(같은 내용을 pdf로도 저장하는 경우가 많고, 표 구조는 원본이 정확함)
            var summaries = specFiles.Where(f => SummaryExts.Contains(f.Extension.ToLowerInvariant()) && SummaryName.IsMatch(Rel(f))).ToList();
            var summary = summaries.OrderBy(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            var revisions = summary is null ? 0 : summaries.Count(f => f.Extension.Equals(summary.Extension, StringComparison.OrdinalIgnoreCase));

            // 고객 사양서 후보: 사양정리·회의록이 아닌 문서. 사양서 같은 이름 → pdf → 최근 순
            var customerFiles = specFiles
                .Where(f => CustomerExts.Contains(f.Extension.ToLowerInvariant()) && f.Length <= MaxBytes
                            && !SummaryName.IsMatch(Rel(f)) && !MinuteName.IsMatch(f.Name))
                .OrderBy(f => SpecLike.IsMatch(f.Name) && !NotSpec.IsMatch(f.Name) ? 0 : NotSpec.IsMatch(f.Name) ? 2 : 1)
                .ThenBy(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .Take(3).ToList();

            var minutes = subDirs.Where(d => MinuteName.IsMatch(d.Name)).SelectMany(d => SafeFiles(d.FullName))
                .Concat(specFiles.Where(f => MinuteName.IsMatch(f.Name)))
                .Where(f => MinuteExts.Contains(f.Extension.ToLowerInvariant()) && !f.Name.StartsWith("~$"))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToList();

            if (summary is null && customerFiles.Count == 0) continue;
            result.Add(new LibraryCandidate(project.FullName, year.Name, customer.Name, summary, revisions, customerFiles, minutes));
        }
        return result;
    }

    /// <summary>
    /// 한 프로젝트를 읽는다. prev가 있으면 바뀌지 않은 파일(경로·크기·수정 날짜 같음)은 다시 읽지 않고 이전 결과를 쓴다.
    /// </summary>
    public static async Task<LibraryDoc> ReadAsync(SpecExtractor extractor, LibraryCandidate c, LibraryDoc? prev, CancellationToken ct = default)
    {
        var projectFolder = System.IO.Path.GetFileName(c.ProjectDir);
        var doc = new LibraryDoc
        {
            Id = IdOf(c.ProjectDir),
            ProjectDir = c.ProjectDir,
            Year = c.Year,
            CustomerFolder = c.Customer,
            ProjectFolder = projectFolder,
            ProjectCode = CodeRe.Match(projectFolder) is { Success: true } m ? m.Value
                : c.Summary is not null && CodeRe.Match(c.Summary.Name) is { Success: true } m2 ? m2.Value : null,
            Minutes = c.Minutes.Select(f => LibFile.Of(f)).ToList(),
        };

        // 사양정리
        if (c.Summary is { } s)
        {
            if (prev?.Summary is { } ps && ps.SameAs(s) && prev.SummaryError is null)
            {
                doc.Summary = ps; doc.Rows = prev.Rows; doc.Title = prev.Title; doc.Features = prev.Features;
            }
            else
            {
                doc.Summary = LibFile.Of(s);
                try
                {
                    var ex = extractor.Extract(await File.ReadAllBytesAsync(s.FullName, ct), s.Name);
                    doc.Rows = SpecItems.ReadSheet(ex.Blocks);
                    doc.Title = string.Join(" ", ex.Blocks.Where(b => b.UnitIndex == 1).Select(b => b.Text).Take(3)).Trim();
                    var process = doc.Rows.FirstOrDefault(r => r.Key == "process")?.Value ?? "";
                    doc.Features = SpecFeatures.From($"{c.Customer} {projectFolder} {s.Name} {doc.Title} {process}",
                        string.Join("\n", ex.Blocks.Select(b => b.Text)));
                }
                catch (Exception e) { doc.SummaryError = e.Message; }
            }
            doc.Summary.Revisions = c.SummaryRevisions;
        }

        // 고객 사양서: 후보를 차례로 읽어 글자가 충분한 첫 문서(스캔 PDF는 건너뜀)
        if (prev?.Customer is { } pc && c.CustomerFiles.FirstOrDefault(f => pc.SameAs(f)) is not null && prev.CustomerError is null)
        {
            doc.Customer = pc; doc.CustomerFeatures = prev.CustomerFeatures; doc.CustomerValues = prev.CustomerValues;
        }
        else
        {
            foreach (var f in c.CustomerFiles)
            {
                try
                {
                    var ex = extractor.Extract(await File.ReadAllBytesAsync(f.FullName, ct), f.Name);
                    var body = string.Join("\n", ex.Blocks.Select(b => b.Text));
                    if (body.Length < MinTextChars) { doc.CustomerError = $"글자가 거의 없음(스캔 문서): {f.Name}"; continue; }
                    var firstPages = string.Join(" ", ex.Blocks.Where(b => b.UnitIndex <= 2).Select(b => b.Text).Take(40));
                    doc.Customer = LibFile.Of(f);
                    doc.CustomerFeatures = SpecFeatures.From($"{c.Customer} {projectFolder} {f.Name} {firstPages}", body);
                    doc.CustomerValues = SpecValues.Extract(ex.Blocks);
                    doc.CustomerError = null;
                    break;
                }
                catch (Exception e) { doc.CustomerError = $"{f.Name}: {e.Message}"; }
            }
        }
        return doc;
    }

    public static string IdOf(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..12].ToLowerInvariant();

    private static IEnumerable<DirectoryInfo> SafeDirs(string path)
    {
        try { return new DirectoryInfo(path).EnumerateDirectories().ToList(); }
        catch (Exception) { return []; }
    }

    private static IEnumerable<FileInfo> SafeFiles(string path)
    {
        try { return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList(); }
        catch (Exception) { return []; }
    }
}
