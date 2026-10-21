using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Leistd.Security.Claims;

/// <summary>判定主体是否匿名的唯一规则；框架各处（当前用户、租户解析、Hub 复评、操作记录等）共用。</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>主体的任一身份已认证时为真；主体为 <see langword="null"/> 或全部身份都未认证时为假。</summary>
    /// <remarks>
    /// 与官方 <c>DenyAnonymousAuthorizationRequirement</c> 判定"已认证用户"的口径一致。
    /// 不要用 <see cref="ClaimsPrincipal.Identity"/>：它只是第一个身份，首身份未认证、后续身份已认证的主体会被误判为匿名。
    /// 只回答"是否匿名"；"这个人是谁"取自主体身份，见 <see cref="ClaimTypeOptions.FindSubjectIdentity"/>。
    /// </remarks>
    public static bool HasAuthenticatedIdentity([NotNullWhen(true)] this ClaimsPrincipal? principal)
        => principal?.Identities.Any(identity => identity.IsAuthenticated) == true;
}
