using DyOne.Data;
using Microsoft.EntityFrameworkCore;

namespace DyOne.Worker;

/// <summary>
/// 주기 작업 하나.
/// - SourceSyncJob: dyone DB의 원천 적재 상태(sync.state)를 확인한다. 프로젝트(applet285)·조직도 적재는 기존 ETL이 담당하고,
///   모듈라(376)·업무일지(296)·ES(281) 적재는 DY ONE 전용 스키마가 확정된 뒤 추가한다.
/// - PushDispatchJob: 알림 → FCM 발송 자리 (외부용 push-worker의 outbox·receipt 구조를 본뜸)
/// - (추후) AiIndexJob: NAS·Drive 문서 → pgvector 인덱싱
/// </summary>
public interface IScheduledJob
{
    string Name { get; }
    TimeSpan Interval { get; }
    Task RunAsync(DyOneDbContext db, CancellationToken ct);
}

public class SourceSyncJob(ILogger<SourceSyncJob> logger) : IScheduledJob
{
    public string Name => "source-sync";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);

    public async Task RunAsync(DyOneDbContext db, CancellationToken ct)
    {
        var projects = await db.Projects.CountAsync(p => p.IsActive, ct);
        var states = await db.SyncStates.OrderByDescending(s => s.UpdatedAt).ToListAsync(ct);
        logger.LogInformation("dyone DB 프로젝트 {Count}건 · 원천 적재 상태 {States}", projects,
            states.Count == 0 ? "기록 없음" : string.Join(", ", states.Select(s => $"{s.Source}/{s.Entity}@{s.UpdatedAt:yyyy-MM-dd HH:mm}")));
    }
}

public class PushDispatchJob(ILogger<PushDispatchJob> logger) : IScheduledJob
{
    public string Name => "push-dispatch";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public Task RunAsync(DyOneDbContext db, CancellationToken ct)
    {
        // FCM 연결 전: 모바일 푸시는 보내지 않는다. 웹·Windows 앱은 API의 SignalR로 이미 실시간 전달된다.
        logger.LogDebug("모바일 푸시 발송 대기 중(FCM 연결 전)");
        return Task.CompletedTask;
    }
}

public class Worker(IServiceScopeFactory scopes, IEnumerable<IScheduledJob> jobs, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(jobs.Select(job => RunLoopAsync(job, stoppingToken)));

    private async Task RunLoopAsync(IScheduledJob job, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(job.Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await job.RunAsync(scope.ServiceProvider.GetRequiredService<DyOneDbContext>(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "작업 실패: {Job}", job.Name);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
