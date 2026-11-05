using Microsoft.Extensions.Options;
using Npgsql;

namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>
/// 프로젝트 메인 매일 흐름(기본 06:30, 출근 전 완료) — 설계 docs/roadmap/project-main-design.md 2-5절.
///  ① ERP 미러: 바뀐 프로젝트만(DTS_UPDATE) → sync.erp_project. 발주 집계 → ②와 함께 sync.project_trace
///  ② 흔적: NAS 프로젝트 폴더 최근 수정 파일(읽기 전용) + 모듈라/ES 기록
///  ③ 회사 주요 후보: 규칙 점수 → project.featured_suggestion, 상위 후보는 사내 AI 이유 문장(숫자 없음)
///  ④ 브리핑: ★ ∪ 회사 주요의 지금 상태 한 줄(사내 AI, 숫자 검사) → project.status_brief
///  ⑤ 일정 주의: 문서 일정 읽기(파이썬 + 사내 AI) → project.schedule_milestone · 판정 → project.schedule_alert
/// ERP·NAS는 읽기만 한다. 쓰기는 dyone(WriteTargets)만. 사내 AI가 없거나 실패해도 ①~③과 규칙 문장으로 화면은 나온다.
/// </summary>
public class ProjectMainJob(IOptions<ProjectMainOptions> options, IConfiguration config, ILogger<ProjectMainJob> logger)
    : DailyJob(TimeOnly.Parse(options.Value.DailyAt), options.Value.TimeZone)
{
    private readonly ProjectMainOptions _opt = options.Value;
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public override string Name => "project-main";
    public override bool RunOnStartup => _opt.RunOnStartup;

    public override async Task RunAsync(CancellationToken ct)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var targets = ConnBuilder.WriteTargets(config);
        if (targets.Count == 0) throw new InvalidOperationException("쓰기 대상 없음(WriteTargets 또는 ConnectionStrings:DyOneWrite 설정 필요)");

        // ① ERP (한 번 읽어 모든 대상에 쓴다). 증분 기준은 대상 중 가장 오래된 값.
        var erpConn = ErpMirror.Conn(config);
        List<ErpProjectRow>? erpRows = null;
        Dictionary<string, ErpPoRow>? po = null;
        if (erpConn is null) logger.LogWarning("ConnectionStrings:Erp 없음 → ERP 미러를 건너뛴다(지난 미러로 계산)");
        else
        {
            string? since = null;
            var first = true;
            foreach (var (_, conn) in targets)
            {
                await using var db = new NpgsqlConnection(conn);
                await db.OpenAsync(ct);
                var w = await ErpMirror.WatermarkAsync(db, ct);
                if (w is null) { since = null; break; }
                if (first || string.CompareOrdinal(w, since) < 0) since = w;
                first = false;
            }
            erpRows = await ErpMirror.ReadProjectsAsync(erpConn, since, ct);
            po = (await ErpMirror.ReadPoAsync(erpConn, today, ct)).GroupBy(p => p.Code).ToDictionary(g => g.Key, g => g.First());
            var recent = await ErpMirror.ReadPoRecentAsync(erpConn, today, ct);
            foreach (var (code, json) in recent) if (po.TryGetValue(code, out var p0)) po[code] = p0 with { RecentJson = json };
            logger.LogInformation("ERP 읽기: 바뀐 프로젝트 {N}건(기준 {Since}), 발주 집계 {P}건", erpRows.Count, since ?? "전체", po.Count);
        }

        // ② NAS
        var nas = _opt.NasRoots.Length > 0 ? TraceCollector.ScanNas(_opt.NasRoots, nowLocal, logger, ct) : [];

        var llm = _opt.UseAi ? new WorkerLlm(config, logger) : null;
        if (llm is { IsConfigured: false }) { logger.LogWarning("사내 AI 키(Llm:ApiKey) 없음 → 규칙 문장만 쓴다"); llm = null; }

        if (_opt.DryRun)
        {
            logger.LogInformation("DryRun: DB 쓰기 생략(ERP {E}건 · NAS {N}건)", erpRows?.Count, nas.Count);
            return;
        }

        Exception? firstError = null;
        foreach (var (name, conn) in targets)
        {
            try
            {
                await using var db = new NpgsqlConnection(conn);
                await db.OpenAsync(ct);
                if (erpRows is not null) logger.LogInformation("[{T}] ERP 미러 {N}건 반영", name, await ErpMirror.UpsertAsync(db, erpRows, ct));
                // 발주를 못 읽었으면(ERP 연결 없음) 지난 발주 값을 그대로 쓴다
                var poMap = po ?? await LastPoAsync(db, ct);
                logger.LogInformation("[{T}] 흔적 {N}건 계산", name, await TraceCollector.WriteAsync(db, poMap, nas, ct));

                var data = await MainData.LoadAsync(db, ct);
                var rows = FeaturedScorer.Score(data, today);
                var saved = await FeaturedScorer.SaveAsync(db, rows, today, _opt.SuggestionKeep, _opt.AiReasonTop, llm, logger, ct);
                logger.LogInformation("[{T}] 회사 주요 후보 {N}건(대상 {All}건) · 1위 {Top}", name, saved, rows.Count, rows.FirstOrDefault()?.Name);

                var (made, ai) = await StatusBriefWriter.RunAsync(db, data, today, 10, llm, logger, ct);
                logger.LogInformation("[{T}] 브리핑 새로 쓴 것 {N}건(사내 AI {Ai}건)", name, made, ai);

                // ⑤ 일정 주의: 사양정리·수행계획서·사양서 일정(NAS 2·3칸, 읽기 전용) → 지연 의심·일정 임박·양산 후 작업 중
                if (_opt.NasRoots.Length > 0)
                {
                    var (read, alerts) = await ScheduleWriter.RunAsync(db, data, today, _opt.NasRoots, config["Spec:PythonPath"] ?? "python", llm, logger, ct);
                    logger.LogInformation("[{T}] 일정 다시 읽은 프로젝트 {R}건 · 일정 주의 판정 {A}건", name, read, alerts);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
                logger.LogError(ex, "[{T}] 프로젝트 메인 계산 실패", name);
            }
        }
        if (firstError is not null) throw firstError;
    }

    private static async Task<Dictionary<string, ErpPoRow>> LastPoAsync(NpgsqlConnection db, CancellationToken ct)
    {
        var d = new Dictionary<string, ErpPoRow>();
        await using var cmd = new NpgsqlCommand("select code, po_30d, po_90d, po_total, last_po_date, po_31_60d, po_61_90d, po_recent::text from sync.project_trace where po_total > 0", db);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetString(0)] = new ErpPoRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)), r.GetInt32(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7));
        return d;
    }
}
