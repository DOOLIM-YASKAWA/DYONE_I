using DyOne.Spec.Equipment;
using DyOne.Spec.Extraction;
using DyOne.Spec.Library;
using Microsoft.Extensions.Options;

namespace DyOne.Worker.Jobs;

/// <summary>
/// 사양서 분석용 NAS 색인을 매일 새로 만든다(기본 03:00). 순서가 중요하다: 라이브러리 → 설비 이력.
///  1) 사양서 라이브러리: NAS 프로젝트마다 고객 사양서·당사 사양정리 표·회의록 목록 (바뀌지 않은 파일은 다시 읽지 않음)
///  2) 설비 이력: 사양정리 표를 공정(열) 단위로(도료·하전·로봇·도장기·FGP) + 사양정리 없는 프로젝트는 도면 파일 제목
/// 결과는 API와 같은 캐시 파일(FileStorage:Root\_spec\library.json·equipment.json)에 쓰고, API는 파일이 바뀌면 다음 조회 때 다시 읽는다.
/// 만드는 코드는 API '다시 읽기' 버튼과 같다(DyOne.Spec LibraryBuilder·EquipmentBuilder). NAS는 읽기만 한다. DB는 쓰지 않는다.
/// 설정: Spec:LibraryRoots(기본 Q:\01_프로젝트), Spec:PythonPath(PDF 표 인식), FileStorage:Root 또는 Spec:LibraryCache·Spec:EquipmentCache.
/// </summary>
public class SpecIndexJob(IOptions<SpecIndexOptions> options, IConfiguration config, ILogger<SpecIndexJob> logger)
    : DailyJob(TimeOnly.Parse(options.Value.DailyAt), options.Value.TimeZone)
{
    private readonly SpecIndexOptions _opt = options.Value;

    public override string Name => "spec-index";
    public override bool RunOnStartup => _opt.RunOnStartup;

    public override async Task RunAsync(CancellationToken ct)
    {
        var roots = config.GetSection("Spec:LibraryRoots").Get<string[]>() is { Length: > 0 } r ? r : [@"Q:\01_프로젝트"];
        var root = config["FileStorage:Root"];
        var libraryPath = config["Spec:LibraryCache"] is { Length: > 0 } lp ? lp
            : root is { Length: > 0 } ? Path.Combine(root, "_spec", "library.json") : throw new InvalidOperationException("FileStorage:Root 또는 Spec:LibraryCache 설정이 없습니다.");
        var equipmentPath = config["Spec:EquipmentCache"] is { Length: > 0 } ep ? ep : Path.Combine(Path.GetDirectoryName(libraryPath)!, "equipment.json");
        if (!roots.Any(Directory.Exists))
        {
            logger.LogWarning("NAS 폴더에 접근할 수 없어 건너뜁니다: {Roots}", string.Join(", ", roots));
            return;
        }
        var extractor = new SpecExtractor(new SpecExtractorOptions { PythonPath = config["Spec:PythonPath"] });

        // 1) 라이브러리
        var started = DateTimeOffset.Now;
        var previous = LibraryBuilder.Load(libraryPath);
        var library = await LibraryBuilder.BuildAsync(extractor, roots, previous, ct: ct);
        logger.LogInformation("사양서 라이브러리 {Count}건 (사양정리 {Summary}, 고객 사양서 {Customer}) {Elapsed:0}초",
            library.Docs.Count, library.Docs.Count(d => d.HasSheet), library.Docs.Count(d => d.Customer is not null), (DateTimeOffset.Now - started).TotalSeconds);
        if (library.Docs.Count == 0 && previous is { Docs.Count: > 0 })
        {
            logger.LogWarning("라이브러리 0건 → 기존 캐시({Count}건)를 지키려고 저장하지 않습니다.", previous.Docs.Count);
            return;
        }
        if (!_opt.DryRun) await LibraryBuilder.SaveAsync(libraryPath, library);

        // 2) 설비 이력(라이브러리 결과를 그대로 씀)
        started = DateTimeOffset.Now;
        var equipment = await EquipmentBuilder.BuildAsync(extractor, roots, library.Docs, warn: w => logger.LogWarning("{Warning}", w), ct: ct);
        logger.LogInformation("설비 이력 {Count}건 (사양정리 공정 {Summary}, 도면 {Drawing}, 프로젝트 {Projects}) {Elapsed:0}초",
            equipment.Units.Count, equipment.Units.Count(u => u.SourceKind == "summary"), equipment.Units.Count(u => u.SourceKind == "drawing"),
            equipment.Projects, (DateTimeOffset.Now - started).TotalSeconds);
        if (_opt.DryRun) { logger.LogInformation("DryRun: 캐시 파일을 쓰지 않습니다."); return; }
        await EquipmentBuilder.SaveAsync(equipmentPath, equipment);
    }
}
