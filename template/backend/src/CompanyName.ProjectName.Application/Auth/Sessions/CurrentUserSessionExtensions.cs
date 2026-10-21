#if (LocalIdentity)
using Leistd.Security.Claims;
using Leistd.Security.Users;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>当前用户的登录会话。</summary>
public static class CurrentUserSessionExtensions
{
    /// <summary>发出本次请求的会话（<c>sid</c> 声明）；未登录或主体里没有会话声明时为 null。</summary>
    public static Guid? GetSessionId(this ICurrentUser currentUser) =>
        Guid.TryParse(currentUser.FindClaim(CustomClaimTypes.SessionId)?.Value, out var id) ? id : null;
}
#endif
