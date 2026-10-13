namespace DyOne.Worker.Jobs;

public class GwModularSyncOptions
{
    /// <summary>
    /// 변경 확인 간격(분). 0보다 크면 이 간격으로 그룹웨어 변경 이력 피드(GwChangeFeed)를 확인하고,
    /// 이력이 생긴 376 문서만 다시 읽는다(확인 조회는 1ms 안팎). 0이면 DailyAt에 하루 한 번 전체 비교.
    /// </summary>
    public int IntervalMinutes { get; set; } = 1;
    /// <summary>변경이 없어도 이 시간(시간)마다 한 번은 전체를 다시 적재한다(누락 보정).</summary>
    public int FullSyncHours { get; set; } = 24;
    /// <summary>IntervalMinutes가 0일 때 매일 실행 시각 (HH:mm).</summary>
    public string DailyAt { get; set; } = "08:00";
    public string TimeZone { get; set; } = "Korea Standard Time";
    /// <summary>프로세스 시작 직후 1회 실행 여부(개발 확인용).</summary>
    public bool RunOnStartup { get; set; }
    /// <summary>true면 조회·집계만 하고 DB에 쓰지 않는다(검증용).</summary>
    public bool DryRun { get; set; }
}
