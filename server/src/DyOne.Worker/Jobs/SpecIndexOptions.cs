namespace DyOne.Worker.Jobs;

public class SpecIndexOptions
{
    /// <summary>매일 실행 시각 (HH:mm). 기본 03:00 — NAS를 오래 훑으므로 사람이 안 쓰는 새벽.</summary>
    public string DailyAt { get; set; } = "03:00";
    public string TimeZone { get; set; } = "Korea Standard Time";
    /// <summary>프로세스 시작 직후 1회 실행 여부(개발 확인용). 운영은 false 권장.</summary>
    public bool RunOnStartup { get; set; }
    /// <summary>true면 만들기만 하고 캐시 파일을 쓰지 않는다(검증용).</summary>
    public bool DryRun { get; set; }
}
