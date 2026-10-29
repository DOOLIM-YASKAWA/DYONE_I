namespace DyOne.Worker.Jobs;

public class GwNotiMirrorOptions
{
    /// <summary>
    /// 그룹웨어 알림을 그대로 받을 WORKS 앱 번호(설정 GwNotiMirror:Applets). 데이터를 옮긴 앱(376 · 281)은 자체 알림이 있으므로 넣지 않는다.
    /// 기본값을 두지 않는다 — 설정 바인딩은 배열을 기본값 뒤에 덧붙인다.
    /// </summary>
    public long[] Applets { get; set; } = [];
    /// <summary>
    /// 데이터를 DY ONE으로 옮긴 앱(376 모듈라 → modular, 그 밖은 works:{번호}). 등록·칸 변경·상태 변경은 동기화 작업이 직접 알리므로,
    /// 그 밖의 그룹웨어 알림(활동기록·댓글 등)만 받아 오고 누르면 DY ONE 문서가 열린다.
    /// </summary>
    public long[] SyncedApplets { get; set; } = [];
    /// <summary>확인 간격(분). go_noti_messages 번호 색인으로 새 알림만 읽는다.</summary>
    public int IntervalMinutes { get; set; } = 1;
    /// <summary>알림을 눌렀을 때 열 그룹웨어 주소</summary>
    public string GwBaseUrl { get; set; } = "https://gw.doolim-yaskawa.com";
    public bool RunOnStartup { get; set; }
    public bool DryRun { get; set; }
}
