namespace DyOne.Worker.Jobs;

/// <summary>주기 작업 공통 인터페이스. 다음 실행까지 남은 시간을 스스로 계산한다.</summary>
public interface IWorkerJob
{
    string Name { get; }

    /// <summary>프로세스 시작 직후 한 번 바로 실행할지(개발·배포 직후 확인용).</summary>
    bool RunOnStartup { get; }

    /// <summary>지금(UTC) 기준 다음 실행까지 남은 시간.</summary>
    TimeSpan UntilNext(DateTimeOffset nowUtc);

    Task RunAsync(CancellationToken ct);
}

/// <summary>매일 지정한 시각(지정 표준시)에 한 번 실행하는 작업.</summary>
public abstract class DailyJob(TimeOnly timeOfDay, string timeZoneId) : IWorkerJob
{
    private readonly TimeZoneInfo _tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

    public abstract string Name { get; }
    public virtual bool RunOnStartup => false;

    public TimeSpan UntilNext(DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var todayRun = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, timeOfDay.Hour, timeOfDay.Minute, 0, localNow.Offset);
        var nextRun = localNow < todayRun ? todayRun : todayRun.AddDays(1);
        var delay = nextRun - localNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public abstract Task RunAsync(CancellationToken ct);
}
