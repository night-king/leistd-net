namespace Leistd.ServiceClient.Abstractions;

/// <summary>读取当前请求经过认证的用户访问令牌；Cookie 与后台任务没有此凭据。</summary>
public interface IUserAccessTokenAccessor
{
    /// <summary>返回已验证的原始 Bearer 令牌；没有用户访问令牌时返回 null。</summary>
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
