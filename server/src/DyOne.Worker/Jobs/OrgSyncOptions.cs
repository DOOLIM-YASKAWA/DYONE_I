namespace DyOne.Worker.Jobs;

public class OrgSyncOptions
{
    /// <summary>매일 실행 시각 (HH:mm). 기본 08:00.</summary>
    public string DailyAt { get; set; } = "08:00";
    public string TimeZone { get; set; } = "Korea Standard Time";
    /// <summary>프로세스 시작 직후 1회 실행 여부(개발 확인용). 운영은 false 권장.</summary>
    public bool RunOnStartup { get; set; }
    /// <summary>true면 조회·집계만 하고 DB에 쓰지 않는다(검증용).</summary>
    public bool DryRun { get; set; }
}
