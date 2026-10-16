#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Domain.Auth.Abstractions;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>外部身份验证服务接口。</summary>
public interface IExternalAuthAppService : IAppService
{
    /// <summary>处理外部登录回调：直接得到会话主体，或（已启用两步验证时）得到第二步凭据。</summary>
    Task<SessionLoginResult> AuthenticateExternalUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default);

    /// <summary>本人的外部账号绑定情况。</summary>
    Task<ExternalLoginsOutputDto> GetCurrentUserExternalLoginsAsync(IEnumerable<string> availableProviders, CancellationToken cancellationToken = default);

    /// <summary>确认当前会话可以绑定外部账号：刚认证过，或出示了正确的当前口令。</summary>
    /// <remarks>
    /// 绑定回调在消费外部票据之前调用：要求出示口令时票据还在，本人输入口令后用同一张票据重试即可，
    /// 不必再走一遍外部授权。口令错误按再认证失败计数与锁定。
    /// </remarks>
    /// <param name="password">本人的当前口令；会话刚认证过时为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnsureLinkAllowedAsync(string? password, CancellationToken cancellationToken = default);

    /// <summary>把外部身份绑定到当前用户；该外部账号已绑在别人名下时拒绝。</summary>
    /// <remarks>调用方必须先经 <see cref="EnsureLinkAllowedAsync"/> 确认近期认证，再消费外部票据、调用本方法。</remarks>
    Task LinkCurrentUserAsync(
        string provider,
        ExternalUserInfo externalUserInfo,
        CancellationToken cancellationToken = default);

    /// <summary>解绑当前用户的一个外部账号；必须还剩一种登录方式。</summary>
    Task UnlinkCurrentUserAsync(Guid id, CancellationToken cancellationToken = default);
}
#endif
