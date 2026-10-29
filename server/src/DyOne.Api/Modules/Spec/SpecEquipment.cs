using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;

namespace DyOne.Api.Modules.Spec;

/// <summary>
/// 설비 이력(공정 단위): 사양정리 표를 공정(열)마다 읽고, 사양정리가 없는 프로젝트는 NAS 도면 파일 제목으로 한 건씩.
/// 만드는 코드는 DyOne.Spec EquipmentBuilder(Worker 매일 새벽 작업과 같은 코드). Worker가 캐시 파일을 새로 쓰면 다음 조회 때 다시 읽는다.
/// 사양정리 표는 SpecLibrary 캐시의 파일 경로를 다시 열어 읽으므로 라이브러리를 먼저 읽어 두어야 한다.
/// 캐시: Spec:EquipmentCache(기본 FileStorage:Root\_spec\equipment.json). NAS는 읽기만 한다.
/// </summary>
public sealed class SpecEquipment
{
    private readonly SpecLibrary _library;
    private readonly SpecExtractor _extractor;
    private readonly ILogger<SpecEquipment> _log;
    private readonly string[] _roots;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private EquipmentCache _cache = new();
    private DateTime _loadedStamp;
    private Task? _running;
    private int _done;

    public SpecEquipment(IConfiguration config, SpecLibrary library, SpecExtractor extractor, ILogger<SpecEquipment> log)
    {
        _library = library;
        _extractor = extractor;
        _log = log;
        _roots = config.GetSection("Spec:LibraryRoots").Get<string[]>() is { Length: > 0 } r ? r : [@"Q:\01_프로젝트"];
        _cachePath = config["Spec:EquipmentCache"] is { Length: > 0 } c ? c
            : Path.Combine(config["FileStorage:Root"] ?? AppContext.BaseDirectory, "_spec", "equipment.json");
        ReloadIfChanged();
    }

    public IReadOnlyList<EquipUnit> Units { get { ReloadIfChanged(); return _cache.Units; } }
    public DateTimeOffset? BuiltAt { get { ReloadIfChanged(); return _cache.BuiltAt; } }
    public int Projects { get { ReloadIfChanged(); return _cache.Projects; } }
    public bool Running => _running is { IsCompleted: false };
    public int Done => _done;
    public int Total { get; private set; }
    public string? LastError { get; private set; }

    public EquipUnit? Find(string id) => Units.FirstOrDefault(u => u.Id == id);

    public bool StartRefresh()
    {
        lock (_gate)
        {
            if (Running) return false;
            LastError = null;
            _running = Task.Run(RefreshAsync);
            return true;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var next = await EquipmentBuilder.BuildAsync(_extractor, _roots, _library.Docs,
                (d, t) => { _done = d; Total = t; }, w => _log.LogWarning("{Warning}", w));
            await EquipmentBuilder.SaveAsync(_cachePath, next);
            _cache = next;
            _loadedStamp = File.GetLastWriteTimeUtc(_cachePath);
            _log.LogInformation("설비 이력 {Count}건 (사양정리 공정 {Summary}, 도면 {Drawing}, 프로젝트 {Projects})",
                next.Units.Count, next.Units.Count(u => u.SourceKind == "summary"), next.Units.Count(u => u.SourceKind == "drawing"), next.Projects);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _log.LogError(e, "설비 이력을 만들지 못했습니다");
        }
    }

    /// <summary>캐시 파일이 바뀌었으면(Worker가 새로 만듦) 다시 읽는다. 옛 형식(공정 단위가 아닌 파일)은 빈 이력으로 본다.</summary>
    private void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var stamp = File.GetLastWriteTimeUtc(_cachePath);
            if (stamp == _loadedStamp) return;
            lock (_gate)
            {
                if (stamp == _loadedStamp) return;
                _cache = EquipmentBuilder.Load(_cachePath) ?? new();
                _loadedStamp = stamp;
            }
        }
        catch (Exception e) { _log.LogWarning(e, "설비 이력 캐시를 읽지 못했습니다: {Path}", _cachePath); }
    }
}
