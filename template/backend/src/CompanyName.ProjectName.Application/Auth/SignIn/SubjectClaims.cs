#if (LocalIdentity)
using System.Security.Claims;
using Leistd.Security.Claims;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

/// <summary>往令牌主体写主体标识：协议要求的 sub 始终签发，<see cref="ClaimTypeOptions.UserIds"/> 不含 sub 时按其首选类型同值再写一条。</summary>
/// <remarks>
/// 令牌的读取方（换码、userinfo、资源服务的当前用户与判权）都按 <see cref="ClaimTypeOptions"/> 读主体标识；
/// 列表里还有 sub 时读取自然回落到它，不必写副本；去掉了 sub 的宿主只签 sub 的话，签出的令牌自己读不回来。
/// </remarks>
public static class SubjectClaims
{
    public static void Set(ClaimsIdentity identity, ClaimTypeOptions claimTypes, string subject)
    {
        identity.SetClaim(Claims.Subject, subject);
        if (claimTypes.UserIds.Contains(Claims.Subject))
        {
            return;
        }

        var preferred = claimTypes.UserIds[0];
        identity.SetClaim(preferred, subject);
        // 与 sub 同去向；不设去向的 claim 不进令牌
        identity.FindFirst(preferred)!.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
    }

    /// <summary>该 claim 是否是写在 sub 之外的主体标识副本。</summary>
    public static bool IsMirror(ClaimTypeOptions claimTypes, string claimType) =>
        !claimTypes.UserIds.Contains(Claims.Subject) && claimType == claimTypes.UserIds[0];
}
#endif
