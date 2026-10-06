using System.Security.Claims;
using DyOne.Api.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DyOne.Api.Modules.Notifications;

/// <summary>
/// 실시간 알림 허브(서버 1대 기준, Redis backplane 없음). Clients.User(userId)로 보낸다.
/// 알림함(저장)은 dyone DB의 알림 테이블이 정해진 뒤 붙인다. platform.notification은 "폐기 예정"이라 쓰지 않는다.
/// </summary>
[Authorize]
public class NotificationHub : Hub;

public record NotificationDto(long Id, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public class NotificationService(IHubContext<NotificationHub> hub)
{
    public Task PushAsync(string userId, string title, string body, string? link, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("notification",
            new NotificationDto(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), title, body, link, DateTimeOffset.UtcNow, null), ct);
}

public static class NotificationsModule
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").WithTags("Notifications");

        // 알림함 저장소가 정해지기 전이라 빈 목록을 돌려준다(화면은 실시간 수신분만 표시).
        g.MapGet("/", () => Array.Empty<NotificationDto>());

        // 개발용: 내게 테스트 알림을 보낸다(웹 화면 안 알림 · Windows 토스트 확인용).
        g.MapPost("/test", async (ClaimsPrincipal p, NotificationService notifications, CancellationToken ct) =>
        {
            await notifications.PushAsync(p.Current().Id, "테스트 알림", "실시간 알림(SignalR)이 정상 동작합니다.", null, ct);
            return Results.NoContent();
        });
    }
}
