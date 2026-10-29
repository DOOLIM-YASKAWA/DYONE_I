using System.Collections.Concurrent;
using System.Text.Json;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;

namespace DyOne.Spec.Equipment;

/// <summary>설비 이력 캐시(JSON 파일): 공정 단위 설비 모델</summary>
public sealed class EquipmentCache
{
    public DateTimeOffset? BuiltAt { get; set; }
    public int Projects { get; set; }
    public List<EquipUnit> Units { get; set; } = [];
}

/// <summary>
/// 설비 이력 만들기·저장·읽기. API(SpecEquipment, '다시 읽기' 버튼)와 Worker(매일 새벽 작업)가 같은 코드·같은 캐시 파일을 쓴다.
///  1) 라이브러리의 당사 사양정리 표를 공정(열)마다 읽는다(도료·하전·로봇·도장기·FGP).
///  2) 사양정리를 읽지 못한 프로젝트는 NAS 도면 파일 제목으로 한 건씩.
/// NAS는 읽기만 한다(도면은 파일 이름만).
/// </summary>
public static class EquipmentBuilder
{
    public static async Task<EquipmentCache> BuildAsync(SpecExtractor extractor, IEnumerable<string> roots, IReadOnlyList<LibraryDoc> docs,
        Action<int, int>? progress = null, Action<string>? warn = null, CancellationToken ct = default)
    {
        var projects = EquipmentIndex.DiscoverProjects(roots);
        var total = projects.Count + docs.Count;
        var done = 0;
        progress?.Invoke(0, total);
        var paintsByDir = docs.GroupBy(d => d.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)(g.First().CustomerFeatures?.Paints ?? []), StringComparer.OrdinalIgnoreCase);
        var bag = new ConcurrentBag<EquipUnit>();

        var summaryDirs = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (d, token) =>
        {
            try
            {
                if (d.HasSheet && d.Summary is { } s && File.Exists(s.Path) && Path.GetExtension(s.Path).ToLowerInvariant() is ".pptx" or ".xlsx")
                {
                    var ex = extractor.Extract(await File.ReadAllBytesAsync(s.Path, token), s.FileName);
                    var units = EquipmentUnits.FromSummary(d, ex.Blocks, paintsByDir.GetValueOrDefault(d.ProjectDir) ?? []);
                    foreach (var u in units) bag.Add(u);
                    if (units.Count > 0) summaryDirs[d.ProjectDir] = true;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { warn?.Invoke($"사양정리 표를 읽지 못했습니다 {d.Summary?.FileName}: {e.Message}"); }
            progress?.Invoke(Interlocked.Increment(ref done), total);
        });

        await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, (p, _) =>
        {
            if (!summaryDirs.ContainsKey(p.dir))
            {
                var recs = EquipmentIndex.FromDrawings(p.dir, p.year, p.customer);
                if (EquipmentUnits.FromDrawings(p.dir, p.year, p.customer, recs, paintsByDir.GetValueOrDefault(p.dir) ?? []) is { } u) bag.Add(u);
            }
            progress?.Invoke(Interlocked.Increment(ref done), total);
            return ValueTask.CompletedTask;
        });

        return new EquipmentCache
        {
            BuiltAt = DateTimeOffset.Now,
            Projects = projects.Count,
            Units = bag.OrderByDescending(u => u.Year).ThenBy(u => u.ProjectFolder).ThenBy(u => u.Unit).ToList(),
        };
    }

    public static EquipmentCache? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<EquipmentCache>(File.ReadAllText(path), LibraryBuilder.Json) : null;

    public static Task SaveAsync(string path, EquipmentCache cache) => LibraryBuilder.SaveJsonAsync(path, cache);
}
