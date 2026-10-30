namespace DyOne.Worker.Jobs;

public class SudamMenuOptions
{
    /// <summary>수담푸드(구내식당 위탁 업체)가 매주 식단 이미지를 올리는 네이버 QR '메뉴판' 페이지.</summary>
    public string SourceUrl { get; set; } = "https://m.site.naver.com/25qXx";
    /// <summary>하루 중 확인 시각들 (HH:mm). 기본 하루 한 번 출근 전 — 업체가 늦게 올리거나 고쳐도 다음 날 아침 반영.</summary>
    public string[] Times { get; set; } = ["06:30"];
    public string TimeZone { get; set; } = "Korea Standard Time";
    /// <summary>프로세스 시작 직후 1회 실행 여부(개발 확인용).</summary>
    public bool RunOnStartup { get; set; }
}
