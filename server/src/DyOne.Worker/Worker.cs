using DyOne.Worker.Jobs;

namespace DyOne.Worker;

/// <summary>
/// 등록된 주기 작업을 각자 스케줄대로 돌린다.
/// 현재 작업: GW 프로젝트 동기화(매일 08:00). 추후: 모바일 푸시 발송(FCM) 등.
/// </summary>
public class Worker(IEnumerable<IWorkerJob> jobs, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(jobs.Select(job => RunLoopAsync(job, stoppingToken)));

    private async Task RunLoopAsync(IWorkerJob job, CancellationToken ct)
    {
        if (job.RunOnStartup)
            await SafeRunAsync(job, ct);

        while (!ct.IsCancellationRequested)
        {
            var delay = job.UntilNext(DateTimeOffset.UtcNow);
            logger.LogInformation("{Job} 다음 실행까지 {Delay}", job.Name, delay);
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SafeRunAsync(job, ct);
        }
    }

    private async Task SafeRunAsync(IWorkerJob job, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("{Job} 시작", job.Name);
            await job.RunAsync(ct);
            logger.LogInformation("{Job} 완료", job.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Job} 실패", job.Name);
        }
    }
}
