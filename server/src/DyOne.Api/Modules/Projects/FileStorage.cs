namespace DyOne.Api.Modules.Projects;

/// <summary>
/// 업로드 파일 실물 저장소. 운영은 사내 NAS 공유 폴더(UNC, 예: \\nas\dyone-files), 개발은 로컬 폴더(FileStorage:Root).
/// 레이아웃(A안): <Root>\projects\<프로젝트코드 이름>\<분류>\<원본파일명>
///   - 사람이 탐색기로도 바로 찾을 수 있게 **사람이 읽는 폴더/파일명**을 쓴다.
///   - 폴더 기준은 프로젝트 code(안정·고유), 가독성 위해 이름을 덧붙인다.
///   - 같은 폴더에 같은 이름이 있으면 " (2)", " (3)" … 으로 중복 회피(덮어쓰기 안 함).
/// DB(project_file.stored_name)에는 Root 기준 **상대경로**(슬래시 구분)를 저장한다.
/// </summary>
public class FileStorage(IConfiguration config)
{
    private readonly string _root = config["FileStorage:Root"]
        ?? throw new InvalidOperationException("FileStorage:Root 설정이 없습니다(개발: appsettings.Development.json, 운영: NAS UNC 경로).");

    private const string Uncategorized = "_미분류";

    /// <summary>저장소 최상위(운영 NAS UNC). MotoSim처럼 자체 폴더 규칙을 쓰는 모듈용.</summary>
    public string Root => _root;

    /// <summary>폴더 이름 한 칸: 금지문자는 '_'로 바꾸고 끝 공백·점을 지운다.</summary>
    public static string SafeSegment(string s) => SafeDir(string.IsNullOrWhiteSpace(s) ? "_" : s);

    /// <summary>파일 이름: 금지문자는 '_'로 바꾼다.</summary>
    public static string SafeFileName(string s) => SafeFile(s);

    /// <summary>다 받은 임시 파일을 Root 기준 상대 폴더로 옮긴다(같은 이름이 있으면 " (2)"). 반환값은 상대경로.</summary>
    public string MoveInto(string sourceAbs, string dirRel, string fileName)
    {
        var dirAbs = Path.Combine(_root, dirRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dirAbs);
        var name = UniqueName(dirAbs, SafeFile(fileName));
        File.Move(sourceAbs, Path.Combine(dirAbs, name));
        return ToRel(Path.Combine(dirRel, name));
    }

    public string FullPath(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string relativePath) => File.Exists(FullPath(relativePath));

    /// <summary>원본 파일명을 유지해 저장. 반환값은 Root 기준 상대경로(DB stored_name).</summary>
    public async Task<string> SaveAsync(string projectFolder, string? category, string originalName, Stream content, CancellationToken ct)
    {
        var dirRel = Path.Combine("projects", SafeDir(projectFolder), SafeDir(category ?? Uncategorized));
        Directory.CreateDirectory(Path.Combine(_root, dirRel));
        var fileName = UniqueName(Path.Combine(_root, dirRel), SafeFile(originalName));
        await using var fs = new FileStream(Path.Combine(_root, dirRel, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, ct);
        return ToRel(Path.Combine(dirRel, fileName));
    }

    /// <summary>분류 변경 시 실물 파일을 새 분류 폴더로 이동. 반환값은 새 상대경로.</summary>
    public string MoveToCategory(string currentRelPath, string projectFolder, string? newCategory)
    {
        var src = FullPath(currentRelPath);
        var dirRel = Path.Combine("projects", SafeDir(projectFolder), SafeDir(newCategory ?? Uncategorized));
        Directory.CreateDirectory(Path.Combine(_root, dirRel));
        var destName = UniqueName(Path.Combine(_root, dirRel), Path.GetFileName(currentRelPath));
        var dest = Path.Combine(_root, dirRel, destName);
        if (File.Exists(src)) File.Move(src, dest);
        return ToRel(Path.Combine(dirRel, destName));
    }

    // 같은 폴더에 같은 이름이 있으면 " (2)", " (3)" … 접미
    private static string UniqueName(string dirAbs, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var i = 2; File.Exists(Path.Combine(dirAbs, candidate)); i++)
            candidate = $"{baseName} ({i}){ext}";
        return candidate;
    }

    private static string ToRel(string path) => path.Replace('\\', '/');

    // 폴더/파일명에서 파일시스템 금지문자만 치환(한글·공백·괄호는 유지). 길이 제한.
    private static string SafeDir(string s) => Trim(ReplaceInvalid(s, Path.GetInvalidFileNameChars()), 120);
    private static string SafeFile(string s)
    {
        var name = ReplaceInvalid(Path.GetFileName(s), Path.GetInvalidFileNameChars());
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        return Trim(name, 160);
    }

    private static string ReplaceInvalid(string s, char[] invalid)
    {
        foreach (var c in invalid) s = s.Replace(c, '_');
        return s.Trim().TrimEnd('.'); // 끝 공백/점 제거(Windows 폴더 규칙)
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
