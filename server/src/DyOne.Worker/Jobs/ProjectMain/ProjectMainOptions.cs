namespace DyOne.Worker.Jobs.ProjectMain;

/// <summary>프로젝트 메인 매일 흐름 설정(appsettings "ProjectMain").</summary>
public sealed class ProjectMainOptions
{
    /// <summary>매일 실행 시각(브리핑이 출근 전에 준비되게 06:30).</summary>
    public string DailyAt { get; set; } = "06:30";
    public string TimeZone { get; set; } = "Korea Standard Time";
    public bool RunOnStartup { get; set; }
    /// <summary>true면 ERP·NAS를 읽고 점수까지 계산해 로그만 남긴다(DB 쓰기 없음).</summary>
    public bool DryRun { get; set; }

    /// <summary>NAS 프로젝트 폴더 뿌리(연도별). 그 아래 분류 폴더 › 'DLP-xx-xxx …' 폴더를 훑는다. 비면 NAS 단계 생략.</summary>
    public string[] NasRoots { get; set; } = [];

    /// <summary>회사 주요 후보로 저장할 개수(화면 후보 서랍은 상위 10건).</summary>
    public int SuggestionKeep { get; set; } = 30;
    /// <summary>사내 AI로 이유 문장을 쓰는 후보 수.</summary>
    public int AiReasonTop { get; set; } = 10;
    /// <summary>false면 사내 AI를 부르지 않고 규칙 문장만 쓴다.</summary>
    public bool UseAi { get; set; } = true;
}
