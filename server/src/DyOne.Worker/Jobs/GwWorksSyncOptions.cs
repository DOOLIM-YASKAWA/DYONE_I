namespace DyOne.Worker.Jobs;

public class GwWorksSyncOptions
{
    /// <summary>
    /// DY ONE으로 옮길 그룹웨어 WORKS 앱 번호(설정 GwWorksSync:Applets). 앱을 늘릴 때 번호만 더한다. (376 모듈라는 GwModularSyncJob)
    /// 기본값을 두지 않는다 — 설정 바인딩은 배열을 기본값 뒤에 덧붙여 같은 앱이 두 번 들어간다.
    /// </summary>
    public long[] Applets { get; set; } = [];
    /// <summary>변경 확인 간격(분). 그룹웨어 변경 이력 피드(GwChangeFeed)를 확인해 이력이 생긴 문서만 다시 읽는다. 0이면 DailyAt 하루 한 번.</summary>
    public int IntervalMinutes { get; set; } = 1;
    /// <summary>변경이 없어도 이 시간마다 한 번은 앱 전체를 비교한다(누락·삭제 확인). 프로세스 시작 직후에도 한 번.</summary>
    public int FullSyncHours { get; set; } = 24;
    public string DailyAt { get; set; } = "08:00";
    public string TimeZone { get; set; } = "Korea Standard Time";
    public bool RunOnStartup { get; set; }
    public bool DryRun { get; set; }
}
