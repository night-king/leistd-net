using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Domain.Users.Policies;
using Leistd.Settings.Resolution;

namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>当前上下文（租户）生效的登录安全策略。</summary>
/// <param name="Lockout">登录失败锁定策略。</param>
/// <param name="RequireTwoFactor">是否要求所有人启用两步验证。</param>
/// <param name="SessionIdleTimeout">会话空闲多久失效；服务端会话判定与会话 Cookie 的有效期都按它。</param>
public sealed record LoginSecurityPolicy(LoginLockoutPolicy Lockout, bool RequireTwoFactor, TimeSpan SessionIdleTimeout);

/// <summary>解析当前租户生效的登录安全策略。</summary>
/// <remarks>与 <see cref="IUserRegistrationPolicyProvider"/> 同型：租户级策略从设置读，而不是 <c>IOptions</c>。</remarks>
public interface ILoginSecurityPolicyProvider
{
    /// <summary>读取当前上下文（租户）生效的登录安全策略。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<LoginSecurityPolicy> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILoginSecurityPolicyProvider" />
/// <param name="settingProvider">按当前上下文解析设置值。</param>
public sealed class LoginSecurityPolicyProvider(
    ISettingProvider settingProvider) : ILoginSecurityPolicyProvider
{
    /// <inheritdoc />
    public async Task<LoginSecurityPolicy> GetAsync(CancellationToken cancellationToken = default)
        => new(
            new LoginLockoutPolicy(
                await settingProvider.GetAsync<int>(
                    SettingConstant.Security.LockoutMaxFailedAttempts, cancellationToken),
                TimeSpan.FromMinutes(await settingProvider.GetAsync<int>(
                    SettingConstant.Security.LockoutDurationMinutes, cancellationToken))),
            await settingProvider.GetAsync<bool>(
                SettingConstant.Security.RequireTwoFactor, cancellationToken),
            TimeSpan.FromDays(await settingProvider.GetAsync<int>(
                SettingConstant.Security.SessionIdleTimeoutDays, cancellationToken)));
}
