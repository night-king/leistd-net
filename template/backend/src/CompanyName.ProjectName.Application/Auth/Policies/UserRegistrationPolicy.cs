using CompanyName.ProjectName.Application.Settings.Provider;
using Leistd.Settings.Resolution;

namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>当前上下文生效的注册策略快照。</summary>
/// <remarks>
/// 字段与 <c>UserRegistrationOptions</c> 一一对应：消费方原来读 <c>IOptions&lt;T&gt;.Value</c>，
/// 换成读这份快照，除了多一次 <c>await</c> 之外用法不变。
/// </remarks>
public sealed record UserRegistrationPolicy
{
    /// <summary>是否开放自助注册。</summary>
    public required bool Enabled { get; init; }

    /// <summary>图形验证码有效期（分钟）。</summary>
    public required int CaptchaExpiryMinutes { get; init; }
#if (Email)

    /// <summary>是否要求邮箱验证。</summary>
    public required bool EnableEmailVerification { get; init; }

    /// <summary>邮箱验证码有效期（分钟）。</summary>
    public required int EmailCodeExpiryMinutes { get; init; }

    /// <summary>发送邮箱验证码的最小间隔（秒）。</summary>
    public required int EmailCodeSendIntervalSeconds { get; init; }

    /// <summary>单个验证挑战允许的最大错误次数。</summary>
    public required int EmailCodeMaxAttempts { get; init; }
#endif
}

/// <summary>解析当前租户生效的注册策略。</summary>
/// <remarks>
/// 注册策略是<b>按租户</b>的：同一套部署下不同租户可以有不同的注册门槛，
/// 而 <c>IOptions&lt;T&gt;</c> 来自 <c>IConfiguration</c>，整个进程只有一份，表达不了这件事。
/// 所以这些值改从设置里读——<c>appsettings</c> 的 <c>UserRegistration</c> 段仍是部署基线
/// （设置定义的代码默认值取自它，见 <c>SettingDefinitionProvider</c>），
/// 设置表只承载租户级覆盖，两者不是两份真相。
/// <para>
/// 这也是"把 Options 改成热加载"在<b>租户级</b>配置上的正确形状：
/// 不是让 <c>IOptionsMonitor</c> 去重载——它没有租户维度，重载出来的还是全进程一份——
/// 而是让消费方改从按上下文解析的设置里读。
/// </para>
/// </remarks>
public interface IUserRegistrationPolicyProvider
{
    /// <summary>读取当前上下文（租户）生效的注册策略。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<UserRegistrationPolicy> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IUserRegistrationPolicyProvider" />
/// <remarks>
/// 各项分开读没有额外开销：<c>ISettingProvider</c> 是 Scoped 且在一次请求内记忆化，
/// 一次请求只查库一次。
/// </remarks>
/// <param name="settingProvider">按当前上下文解析设置值。</param>
public sealed class UserRegistrationPolicyProvider(
    ISettingProvider settingProvider) : IUserRegistrationPolicyProvider
{
    /// <inheritdoc />
    public async Task<UserRegistrationPolicy> GetAsync(CancellationToken cancellationToken = default)
        => new()
        {
            Enabled = await settingProvider.GetAsync<bool>(
                SettingConstant.Registration.Enabled, cancellationToken),
            CaptchaExpiryMinutes = await settingProvider.GetAsync<int>(
                SettingConstant.Registration.CaptchaExpiryMinutes, cancellationToken),
#if (Email)
            EnableEmailVerification = await settingProvider.GetAsync<bool>(
                SettingConstant.Registration.EnableEmailVerification, cancellationToken),
            EmailCodeExpiryMinutes = await settingProvider.GetAsync<int>(
                SettingConstant.Registration.EmailCodeExpiryMinutes, cancellationToken),
            EmailCodeSendIntervalSeconds = await settingProvider.GetAsync<int>(
                SettingConstant.Registration.EmailCodeSendIntervalSeconds, cancellationToken),
            EmailCodeMaxAttempts = await settingProvider.GetAsync<int>(
                SettingConstant.Registration.EmailCodeMaxAttempts, cancellationToken),
#endif
        };
}
