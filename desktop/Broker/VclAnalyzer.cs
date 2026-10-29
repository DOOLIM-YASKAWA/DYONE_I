using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DyOne.Desktop.Broker;

/// <summary>
/// .vcl 분석(원본 런처 desktop/electron/main.js 1661-2136 규칙 이식). PC 파일은 읽기만 한다.
/// 결과는 업로드 zip을 만들 때만 쓴다: 백업 파일·안 쓰는 로봇 폴더는 zip에서 빼고, 안 쓰는 MODELGROUP을 지운 .vcl은 zip 안에만 넣는다.
/// (원본 런처는 이것들을 PC에서 실제로 지웠다 — 2026-10-21 결정으로 DY ONE은 PC 폴더를 건드리지 않는다.)
/// </summary>
public static partial class VclAnalyzer
{
    public static readonly Encoding Latin1 = Encoding.Latin1;

    public sealed record Plan(
        string VclPath,
        string CellFolder,
        string? MotosimVersion,
        string CleanedVclText,
        bool VclChanged,
        IReadOnlyList<string> RemovedModelGroups,
        IReadOnlyList<string> ExcludedFolders,   // Cell 폴더 기준 최상위 폴더 이름
        IReadOnlyList<string> ExcludedBackupFiles, // Cell 폴더 기준 상대경로
        int FileCount,
        long TotalBytes,
        int IncludedFileCount,
        long IncludedBytes);

    /// <summary>백업 파일(이름 기준). *.bak 전체는 절대 아님 — system/OVERCMOS/*.BAK는 티칭 조건이다.</summary>
    public static bool IsBackupArtifact(string fileName) =>
        VclBak().IsMatch(fileName) || VrcBinBak().IsMatch(fileName) || BkZip().IsMatch(fileName);

    public static string? ReadVersion(string text)
    {
        var m = VersionLine().Match(text);
        if (!m.Success) return null;
        return m.Groups[1].Value switch { "15" => "2024", "16" => "2025", _ => null };
    }

    public static Plan Analyze(string vclPath)
    {
        if (!File.Exists(vclPath) || !vclPath.EndsWith(".vcl", StringComparison.OrdinalIgnoreCase))
            throw new BrokerException(".vcl 파일을 찾을 수 없습니다.");
        var cellFolder = Path.GetDirectoryName(Path.GetFullPath(vclPath))!;
        var text = File.ReadAllText(vclPath, Latin1);

        var controllerBodies = Blocks(text, ControllerOpen()).Select(b => b.Body).ToList();
        var controllerFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var body in controllerBodies)
        {
            foreach (var (key, value) in TopLevelAssignments(body, "PATH", "NAME"))
                if (FolderNameFromValue(value, cellFolder) is { Length: > 0 } f) controllerFolders.Add(f);
            // 원본은 줄 맨 앞의 PATH|NAME|ROBOT만 봤다. 한 줄에 여러 개(R01{ NAME=...; })도 참조로 본다 — 더 많이 남기는 쪽이라 안전하다.
            foreach (Match m in AnyRef().Matches(body))
            {
                var v = m.Groups[1].Value.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                    ? FolderNameFromValue(m.Groups[2].Value, cellFolder)
                    : Sanitize(StripQuotes(m.Groups[2].Value.Trim()));
                if (v.Length > 0) referenced.Add(v);
            }
        }

        // 안 쓰는 MODELGROUP 블록과 그 로봇 폴더
        var modelGroups = Blocks(text, ModelGroupOpen()).ToList();
        var modelGroupRobotFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removeRanges = new List<(int Start, int End)>();
        var removedNames = new List<string>();
        var orphanFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mg in modelGroups)
        {
            var name = Sanitize(StripQuotes(TopLevelAssignments(mg.Body, "NAME").Select(x => x.Value).FirstOrDefault() ?? ""));
            var robotFolder = RobotFolderOf(name, controllerFolders);
            if (robotFolder.Length > 0) modelGroupRobotFolders.Add(robotFolder);
            if (name.Length > 0 && referenced.Contains(name)) continue;
            if (robotFolder.Length == 0) continue;
            if (controllerFolders.Count > 0 && controllerFolders.Contains(robotFolder)) continue;

            var start = text.LastIndexOf('\n', Math.Max(0, mg.OpenIndex - 1)) + 1;
            var end = mg.CloseIndex + 1;
            if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end += 2;
            else if (end < text.Length && (text[end] == '\n' || text[end] == '\r')) end += 1;
            removeRanges.Add((start, end));
            removedNames.Add(name);
            orphanFolders.Add(robotFolder);
        }
        var cleaned = text;
        foreach (var (s, e) in removeRanges.OrderByDescending(r => r.Start)) cleaned = cleaned.Remove(s, e - s);

        // 남은 로봇 폴더(컨트롤러가 안 쓰는 로봇 이름 모양 폴더). 두 집합이 다 있어야 판단한다.
        var topDirs = Directory.EnumerateDirectories(cellFolder).Select(Path.GetFileName).OfType<string>().ToList();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in topDirs) if (orphanFolders.Contains(d)) excluded.Add(d);
        if (controllerFolders.Count > 0 && modelGroupRobotFolders.Count > 0)
        {
            var all = new HashSet<string>(controllerFolders.Concat(modelGroupRobotFolders), StringComparer.OrdinalIgnoreCase);
            var prefixes = all.Where(n => n.IndexOf('-') > 0).Select(n => n[..(n.IndexOf('-') + 1)]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var d in topDirs)
            {
                if (d.StartsWith("CONVEYOR", StringComparison.OrdinalIgnoreCase)) continue; // 컨베이어 폴더는 업로드에서 빼지 않는다
                var likely = all.Contains(d) || prefixes.Any(p => d.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || RobotFolderShape().IsMatch(d);
                if (likely && !controllerFolders.Contains(d)) excluded.Add(d);
            }
        }

        // 크기·백업 파일 집계
        int fileCount = 0, included = 0; long total = 0, includedBytes = 0;
        var backups = new List<string>();
        foreach (var f in Directory.EnumerateFiles(cellFolder, "*", SearchOption.AllDirectories))
        {
            var len = new FileInfo(f).Length;
            fileCount++; total += len;
            var rel = Path.GetRelativePath(cellFolder, f);
            if (IsBackupArtifact(Path.GetFileName(f))) { backups.Add(rel); continue; }
            if (excluded.Contains(rel.Split(Path.DirectorySeparatorChar)[0]) && rel.Contains(Path.DirectorySeparatorChar)) continue;
            included++; includedBytes += len;
        }

        return new Plan(Path.GetFullPath(vclPath), cellFolder, ReadVersion(text), cleaned, removeRanges.Count > 0,
            removedNames, excluded.OrderBy(x => x).ToList(), backups, fileCount, total, included, includedBytes);
    }

    /// <summary>폴더 안에서 쓸 .vcl 고르기: 이름 = Cell 이름 → 이름 = 폴더 이름 → 가장 짧은 경로</summary>
    public static string? FindBestVcl(string folder, string? cellName)
    {
        if (!Directory.Exists(folder)) return null;
        var all = Directory.EnumerateFiles(folder, "*.vcl", SearchOption.AllDirectories).ToList();
        if (all.Count == 0) return null;
        bool Stem(string p, string? n) => n is { Length: > 0 } && Path.GetFileNameWithoutExtension(p).Equals(n.Trim(), StringComparison.OrdinalIgnoreCase);
        return all.FirstOrDefault(p => Stem(p, cellName))
            ?? all.FirstOrDefault(p => Stem(p, Path.GetFileName(folder.TrimEnd('\\', '/'))))
            ?? all.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Create(new System.Globalization.CultureInfo("ko-KR"), false)).First();
    }

    public static string Sanitize(string value, string fallback = "")
    {
        var s = InvalidPathChars().Replace(value.Trim(), "_");
        s = TrailingDots().Replace(s, "").Trim();
        return s.Length == 0 ? fallback : s;
    }

    private static string RobotFolderOf(string name, HashSet<string> controllerFolders)
    {
        if (name.Length == 0 || name.StartsWith("CONVEYOR", StringComparison.OrdinalIgnoreCase)) return "";
        if (controllerFolders.Contains(name)) return name;
        var m = RobotSuffix().Match(name);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string FolderNameFromValue(string raw, string cellFolder)
    {
        var v = StripQuotes(raw.Trim());
        string seg;
        var idx = v.IndexOf("%CELPATH%", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = v.Remove(idx, "%CELPATH%".Length).TrimStart('\\', '/');
            seg = rest.Split('\\', '/')[0];
        }
        else
        {
            string? full = null;
            try { full = Path.GetFullPath(Path.Combine(cellFolder, v)); } catch (Exception) { }
            if (full is not null && full.StartsWith(cellFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                seg = Path.GetRelativePath(cellFolder, full).Split(Path.DirectorySeparatorChar)[0];
            else
                seg = Path.GetFileName(v.TrimEnd('\\', '/'));
        }
        return Sanitize(seg);
    }

    private static string StripQuotes(string s) => Quotes().Replace(s, "");

    /// <summary>블록 본문의 depth 0 대입값(줄마다 { } 개수로 깊이를 센다)</summary>
    private static IEnumerable<(string Key, string Value)> TopLevelAssignments(string body, params string[] keys)
    {
        var depth = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (depth == 0)
                foreach (var key in keys)
                {
                    var m = Regex.Match(line, $@"^{key}\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
                    if (m.Success) { yield return (key, StripQuotes(m.Groups[1].Value.Trim())); break; }
                }
            depth = Math.Max(0, depth + line.Count(c => c == '{') - line.Count(c => c == '}'));
        }
    }

    private sealed record Block(int OpenIndex, int BraceIndex, int CloseIndex, string Body);

    private static IEnumerable<Block> Blocks(string text, Regex open)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var m = open.Match(text, pos);
            if (!m.Success) yield break;
            var brace = m.Index + m.Length - 1;
            var close = MatchingBrace(text, brace);
            if (close < 0) yield break;
            yield return new Block(m.Index, brace, close, text.Substring(brace + 1, close - brace - 1));
            pos = close + 1;
        }
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    [GeneratedRegex(@"\.vcl\.bak$", RegexOptions.IgnoreCase)] private static partial Regex VclBak();
    [GeneratedRegex(@"^VRC\.bin\.bak$", RegexOptions.IgnoreCase)] private static partial Regex VrcBinBak();
    [GeneratedRegex(@"_bk_\d{8}_\d{6}\.zip$", RegexOptions.IgnoreCase)] private static partial Regex BkZip();
    [GeneratedRegex(@"\bVERSION\s*=\s*(\d+)\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+", RegexOptions.IgnoreCase)] private static partial Regex VersionLine();
    [GeneratedRegex(@"\bCONTROLLER\s*\([^)]*\)\s*\{", RegexOptions.IgnoreCase)] private static partial Regex ControllerOpen();
    [GeneratedRegex(@"\bMODELGROUP\s*\{", RegexOptions.IgnoreCase)] private static partial Regex ModelGroupOpen();
    [GeneratedRegex(@"(?:^|(?<=[\s{;]))(PATH|NAME|ROBOT)\s*=\s*([^;\r\n}]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)] private static partial Regex AnyRef();
    [GeneratedRegex(@"^(.+)-R\d+(?:$|_)", RegexOptions.IgnoreCase)] private static partial Regex RobotSuffix();
    [GeneratedRegex(@"^(?:[A-Za-z]{1,8}\d{1,4}(?:[-_][A-Za-z0-9]+)*|\d{1,8}[-_][A-Za-z0-9]+(?:[-_][A-Za-z0-9]+)*)$", RegexOptions.IgnoreCase)] private static partial Regex RobotFolderShape();
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]")] private static partial Regex InvalidPathChars();
    [GeneratedRegex(@"\.+$")] private static partial Regex TrailingDots();
    [GeneratedRegex(@"^[""']|[""']$")] private static partial Regex Quotes();
}
