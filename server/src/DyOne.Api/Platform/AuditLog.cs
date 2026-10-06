namespace DyOne.Api.Platform;

/// <summary>
/// 감사 기록. dyone DB에 DY ONE 전용 스키마가 확정되기 전까지는 서버 로그에만 남긴다.
/// (스키마 확정 후 audit 테이블로 저장하도록 이 클래스만 바꾼다)
/// </summary>
public class AuditLog(ILogger<AuditLog> logger, IHttpContextAccessor http)
{
    public void Record(string action, string entity, string entityId, string summary)
    {
        var user = http.HttpContext?.User is { Identity.IsAuthenticated: true } p ? p.Current() : null;
        logger.LogInformation("AUDIT {Action} {Entity}:{EntityId} by {UserId}({UserName}) · {Summary}",
            action, entity, entityId, user?.Id ?? "system", user?.Name ?? "system", summary);
    }
}
