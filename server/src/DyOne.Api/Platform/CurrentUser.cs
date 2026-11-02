using System.Security.Claims;

namespace DyOne.Api.Platform;

/// <param name="Kind">staff(직원) · partner(협력사). 클레임이 없는 옛 토큰은 직원으로 본다.</param>
/// <param name="Id">직원이면 org.employee.id, 협력사면 partner.person.id — 서로 다른 번호 체계라 Kind 없이 섞어 쓰지 않는다.</param>
public record CurrentUser(string Id, string Name, string Role, string Kind = CurrentUser.Staff, long? PartnerCompanyId = null)
{
    public const string Staff = "staff";
    public const string Partner = "partner";
    public const string KindClaim = "kind";
    public const string PartnerCompanyClaim = "partner_company";

    public bool IsPartner => Kind == Partner;

    public static CurrentUser From(ClaimsPrincipal p) => new(
        p.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("인증되지 않은 요청"),
        p.FindFirstValue(ClaimTypes.Name) ?? "",
        p.FindFirstValue(ClaimTypes.Role) ?? "",
        p.FindFirstValue(KindClaim) == Partner ? Partner : Staff,
        long.TryParse(p.FindFirstValue(PartnerCompanyClaim), out var c) ? c : null);
}

public static class ClaimsPrincipalExtensions
{
    public static CurrentUser Current(this ClaimsPrincipal p) => CurrentUser.From(p);
    public static bool IsPartner(this ClaimsPrincipal p) => p.FindFirstValue(CurrentUser.KindClaim) == CurrentUser.Partner;
}
