using System.Security.Claims;

namespace Leistd.Authorization.Subjects;

/// <summary>权限检查主体提供器：把认证主体映射为权限检查所需的用户、角色与超管标记。</summary>
/// <remarks>
/// 两个方法必须同一口径：<see cref="GetCurrentSubjectAsync"/> 等价于对当前主体调用 <see cref="GetSubjectAsync"/>。
/// 读主体标识用 <c>ClaimTypeOptions.FindUserId</c> 的共享规则。
/// </remarks>
public interface IPermissionSubjectProvider
{
    /// <summary>获取当前权限检查主体；未登录或无法识别时返回 <see langword="null"/>。</summary>
    Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default);

    /// <summary>获取指定认证主体对应的权限检查主体；无法识别时返回 <see langword="null"/>。</summary>
    /// <remarks>
    /// 用于授权处理器评估 <c>AuthorizationHandlerContext.User</c> 这类显式传入、未必是当前请求用户的主体。
    /// 实现不应读取 <c>ICurrentUser</c> 等环境态，只按 <paramref name="principal"/> 的声明解析。
    /// </remarks>
    /// <param name="principal">要解析的认证主体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
