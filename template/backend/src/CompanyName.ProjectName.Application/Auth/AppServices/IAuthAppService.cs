using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.SignIn;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>认证服务接口。</summary>
public interface IAuthAppService : IAppService
{
    /// <summary>验证本地账号：直接得到会话主体，或（有能用的第二步方式时）得到第二步凭据与可用方式。</summary>
    Task<SessionLoginResult> AuthenticateSessionAsync(
        LoginInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>登录第二步：校验其中一种方式的码（身份验证器、邮箱或恢复码）并创建会话主体。</summary>
    Task<ClaimsPrincipal> CompleteTwoFactorLoginAsync(
        TwoFactorLoginInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>登录第二步：给出用通行密钥验证的断言选项；这种方式此刻不能用时以 <c>Auth:TwoFactorMethodUnavailable</c> 拒绝。</summary>
    Task<PasskeyOptionsOutputDto> BeginTwoFactorPasskeyAsync(
        TwoFactorPasskeyOptionsInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>免口令登录：给出断言选项与仪式凭据。</summary>
    Task<PasskeyOptionsOutputDto> BeginPasskeySignInAsync(CancellationToken cancellationToken = default);

    /// <summary>免口令登录：校验断言并创建会话主体，不再要求第二步。</summary>
    Task<ClaimsPrincipal> CompletePasskeySignInAsync(
        PasskeySignInInputDto input,
        CancellationToken cancellationToken = default);
#if (Email)

    /// <summary>登录第二步：把验证码发到本人的已验证邮箱；邮箱方式此刻不能用时以 <c>Auth:TwoFactorMethodUnavailable</c> 拒绝。</summary>
    Task<TwoFactorEmailCodeOutputDto> SendTwoFactorEmailCodeAsync(
        TwoFactorEmailCodeInputDto input,
        CancellationToken cancellationToken = default);
#endif

    /// <summary>当前租户的注册摘要：是否开放自助注册、注册时是否要求邮箱验证、部署能否发出验证码。匿名可读。</summary>
    Task<SecurityConfigOutputDto> GetSecurityConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>用户注册；租户关闭自助注册时拒绝。</summary>
    Task<UserOutputDto> RegisterAsync(RegisterInputDto input, CancellationToken cancellationToken = default);

    /// <summary>获取当前用户信息。</summary>
    Task<UserOutputDto> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>更新个人信息。</summary>
    Task<UserOutputDto> UpdateCurrentUserAsync(UpdateCurrentUserInputDto input, CancellationToken cancellationToken = default);

    /// <summary>设置或清除自己的头像（只接受图片）。</summary>
    Task<UserOutputDto> SetCurrentUserAvatarAsync(SetAvatarInputDto input, CancellationToken cancellationToken = default);
#if (Email)

    /// <summary>给自己当前的邮箱发验证码。</summary>
    Task<EmailVerificationChallengeOutputDto> SendCurrentUserEmailCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>用验证码确认自己当前的邮箱。</summary>
    Task<UserOutputDto> ConfirmCurrentUserEmailAsync(EmailVerificationInputDto input, CancellationToken cancellationToken = default);
#endif

    /// <summary>修改密码。成功后撤销本人除当前会话以外的全部会话。</summary>
    Task ChangePasswordAsync(ChangePasswordInputDto input, CancellationToken cancellationToken = default);
#if (ExternalLogin)

    /// <summary>给没有本地口令的账号设置第一个口令。成功后撤销本人除当前会话以外的全部会话。</summary>
    /// <remarks>已有口令时以 <c>Security:PasswordAlreadySet</c> 拒绝；会话不够新时要出示验证码，没开两步验证的要重新登录。</remarks>
    Task SetPasswordAsync(SetPasswordInputDto input, CancellationToken cancellationToken = default);
#endif
}
