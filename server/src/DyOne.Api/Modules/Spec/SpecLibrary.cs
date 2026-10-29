using DyOne.Spec.Extraction;
using DyOne.Spec.Library;

namespace DyOne.Api.Modules.Spec;

/// <summary>
/// 사양서 라이브러리: NAS 프로젝트 폴더마다 고객 사양서(특징·값)와 사양정리(당사 내부 정리본) 항목 표·회의록 목록을 캐시 파일(JSON)에 둔다.
/// 만드는 코드는 DyOne.Spec LibraryBuilder(Worker 매일 새벽 작업과 같은 코드). Worker가 캐시 파일을 새로 쓰면 다음 조회 때 다시 읽는다.
/// 위치: Spec:LibraryRoots(기본 Q:\01_프로젝트), 캐시: Spec:LibraryCache(기본 FileStorage:Root\_spec\library.json).
/// </summary>
public sealed class SpecLibrary
{
    private readonly SpecExtractor _extractor;
    private readonly ILogger<SpecLibrary> _log;
    private readonly string[] _roots;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private LibraryCache _cache = new();
    private DateTime _loadedStamp;
    private Task? _running;
    private int _done;

    public int Done => _done;
    public int Total { get; private set; }
    public string? LastError { get; private set; }

    public SpecLibrary(IConfiguration config, SpecExtractor extractor, ILogger<SpecLibrary> log)
    {
        _extractor = extractor;
        _log = log;
        _roots = config.GetSection("Spec:LibraryRoots").Get<string[]>() is { Length: > 0 } r ? r : [@"Q:\01_프로젝트"];
        _cachePath = config["Spec:LibraryCache"] is { Length: > 0 } c ? c
            : Path.Combine(config["FileStorage:Root"] ?? AppContext.BaseDirectory, "_spec", "library.json");
        ReloadIfChanged();
    }

    public IReadOnlyList<LibraryDoc> Docs { get { ReloadIfChanged(); return _cache.Docs; } }
    public DateTimeOffset? BuiltAt { get { ReloadIfChanged(); return _cache.BuiltAt; } }
    public bool Running => _running is { IsCompleted: false };
    public IReadOnlyList<string> Roots => _roots;

    public LibraryDoc? Find(string id) => Docs.FirstOrDefault(d => d.Id == id);

    /// <summary>백그라운드로 다시 만든다. 이미 도는 중이면 그대로 둔다.</summary>
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
            var next = await LibraryBuilder.BuildAsync(_extractor, _roots, _cache, (d, t) => { _done = d; Total = t; });
            await LibraryBuilder.SaveAsync(_cachePath, next);
            _cache = next;
            _loadedStamp = File.GetLastWriteTimeUtc(_cachePath);
            _log.LogInformation("사양서 라이브러리 {Count}건 (사양정리 {Summary}, 고객 사양서 {Customer})", next.Docs.Count, next.Docs.Count(d => d.HasSheet), next.Docs.Count(d => d.Customer is not null));
        }
        catch (Exception e)
        {
            LastError = e.Message;
            _log.LogError(e, "사양서 라이브러리를 만들지 못했습니다");
        }
    }

    /// <summary>캐시 파일이 바뀌었으면(Worker가 새로 만듦) 다시 읽는다. 파일 수정 시각만 비교해서 가볍다.</summary>
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
                _cache = LibraryBuilder.Load(_cachePath) ?? new();
                _loadedStamp = stamp;
            }
        }
        catch (Exception e) { _log.LogWarning(e, "사양서 라이브러리 캐시를 읽지 못했습니다: {Path}", _cachePath); }
    }
}
