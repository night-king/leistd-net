#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>本人的两步验证：身份验证器的设置、启用、停用与恢复码，邮箱验证码的开关，以及首选方式。</summary>
public interface ITwoFactorAppService : IAppService
{
    /// <summary>当前状态。</summary>
    Task<TwoFactorStatusOutputDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始设置：生成一个待确认的密钥（几分钟内有效），返回给身份验证器应用添加。未启用时是首次启用，已启用时是换绑。
    /// 会话不够新时要确认身份（<c>Auth:ReauthenticationRequired</c>）：出示当前口令，换绑时也可以是现有验证器上的码；
    /// 没有口令也没开两步验证的账号要重新登录（<c>Auth:SignInAgainRequired</c>）。
    /// 换绑还要现有验证器上的码（同时算作确认身份，只校验一次）或一个恢复码。同一用户只保留最近一次设置，之前开始的随之作废。
    /// </summary>
    Task<TwoFactorSetupOutputDto> BeginSetupAsync(BeginTwoFactorSetupInputDto input, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用新验证器上的验证码确认这一次设置：首次启用或完成换绑，返回一次性展示的新恢复码；本人的其他会话随之退出。
    /// 设置已过期、别处重新开始过或之后凭据变过时以 <c>Auth:TwoFactorSetupExpired</c> 拒绝。
    /// </summary>
    Task<TwoFactorRecoveryCodesOutputDto> EnableAsync(EnableTwoFactorInputDto input, CancellationToken cancellationToken = default);

    /// <summary>停用（有口令的账号要口令与验证码，没有口令的账号只要验证码）；本人的其他会话随之退出。所在租户要求两步验证时拒绝。</summary>
    Task DisableAsync(DisableTwoFactorInputDto input, CancellationToken cancellationToken = default);

    /// <summary>重新生成恢复码（要验证码），旧的全部作废。</summary>
    Task<TwoFactorRecoveryCodesOutputDto> RegenerateRecoveryCodesAsync(TwoFactorCodeInputDto input, CancellationToken cancellationToken = default);
#if (Email)

    /// <summary>
    /// 开始开启邮箱验证码：确认是本人后把确认码发到当前邮箱。会话不够新时要确认身份（同 <see cref="BeginSetupAsync"/>）。
    /// 已开启时以 <c>Auth:TwoFactorAlreadyEnabled</c> 拒绝；组织不允许或邮箱未验证时以 <c>Auth:TwoFactorMethodUnavailable</c> 拒绝。
    /// </summary>
    Task<EmailVerificationChallengeOutputDto> BeginEmailSetupAsync(EmailTwoFactorChangeInputDto input, CancellationToken cancellationToken = default);

    /// <summary>用发到邮箱的确认码开启邮箱验证码；本人的其他会话随之退出。码不对或已过期时以 <c>Auth:EmailCodeInvalid</c> 拒绝。</summary>
    Task EnableEmailAsync(EmailVerificationInputDto input, CancellationToken cancellationToken = default);

    /// <summary>关闭邮箱验证码（会话不够新时要确认身份）；本人的其他会话随之退出。首选是它时回到系统顺序。</summary>
    Task DisableEmailAsync(EmailTwoFactorChangeInputDto input, CancellationToken cancellationToken = default);
#endif

    /// <summary>指定登录第二步的首选方式；该方式未设置时以 <c>Auth:TwoFactorMethodUnavailable</c> 拒绝。</summary>
    Task SetPreferredMethodAsync(SetPreferredTwoFactorMethodInputDto input, CancellationToken cancellationToken = default);
}
#endif
