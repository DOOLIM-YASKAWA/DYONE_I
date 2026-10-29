using System.Collections.Concurrent;
using System.Text.Json;
using DyOne.Spec.Extraction;

namespace DyOne.Spec.Library;

/// <summary>사양서 라이브러리 캐시(JSON 파일): NAS 프로젝트마다 고객 사양서·사양정리 표·회의록 목록</summary>
public sealed class LibraryCache
{
    /// <summary>읽기 규칙 판(LibraryBuilder.CurrentVersion). 다르면 바뀌지 않은 파일도 다시 읽는다.</summary>
    public int Version { get; set; }
    public DateTimeOffset? BuiltAt { get; set; }
    public List<LibraryDoc> Docs { get; set; } = [];
}

/// <summary>
/// 사양서 라이브러리 만들기·저장·읽기. API(SpecLibrary, '다시 읽기' 버튼)와 Worker(매일 새벽 작업)가 같은 코드·같은 캐시 파일을 쓴다.
/// 이전 캐시와 경로·크기·수정 날짜가 같은 파일은 다시 읽지 않는다. NAS는 읽기만 한다.
/// </summary>
public static class LibraryBuilder
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 읽기 규칙 판. 사양정리 표 항목(SpecItems)·특징 규칙을 바꾸면 올린다 → 다음 빌드 때 모든 파일을 다시 읽는다.
    /// 2: 사양정리 표에 Bell Cleaner·Paint·Power Unit 줄 추가(2026-10-26)
    /// </summary>
    public const int CurrentVersion = 2;

    public static async Task<LibraryCache> BuildAsync(SpecExtractor extractor, IEnumerable<string> roots, LibraryCache? previous,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        if (previous?.Version != CurrentVersion) previous = null;
        var picked = LibraryScanner.Discover(roots);
        var old = (previous?.Docs ?? []).Where(d => d.ProjectDir.Length > 0)
            .GroupBy(d => d.ProjectDir, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bag = new ConcurrentBag<LibraryDoc>();
        var done = 0;
        progress?.Invoke(0, picked.Count);
        await Parallel.ForEachAsync(picked, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (c, token) =>
        {
            bag.Add(await LibraryScanner.ReadAsync(extractor, c, old.GetValueOrDefault(c.ProjectDir), token));
            progress?.Invoke(Interlocked.Increment(ref done), picked.Count);
        });
        return new LibraryCache
        {
            Version = CurrentVersion,
            BuiltAt = DateTimeOffset.Now,
            Docs = bag.OrderByDescending(d => d.Year).ThenBy(d => d.CustomerFolder).ThenBy(d => d.ProjectFolder).ToList(),
        };
    }

    public static LibraryCache? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LibraryCache>(File.ReadAllText(path), Json) : null;

    public static Task SaveAsync(string path, LibraryCache cache) => SaveJsonAsync(path, cache);

    /// <summary>임시 파일에 쓰고 바꿔 넣는다(읽는 쪽이 반쯤 쓴 파일을 보지 않게)</summary>
    public static async Task SaveJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
