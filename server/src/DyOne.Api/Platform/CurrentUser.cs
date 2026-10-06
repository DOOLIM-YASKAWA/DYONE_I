using System.Security.Claims;

namespace DyOne.Api.Platform;

public record CurrentUser(string Id, string Name, string Role)
{
    public static CurrentUser From(ClaimsPrincipal p) => new(
        p.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("인증되지 않은 요청"),
        p.FindFirstValue(ClaimTypes.Name) ?? "",
        p.FindFirstValue(ClaimTypes.Role) ?? "");
}

public static class ClaimsPrincipalExtensions
{
    public static CurrentUser Current(this ClaimsPrincipal p) => CurrentUser.From(p);
}
