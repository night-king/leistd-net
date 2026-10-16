using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.SignIn;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>认证服务接口。</summary>
public interface IAuthAppService : IAppService
{
    /// <summary>验证本地账号：直接得到会话主体，或（已启用两步验证时）得到第二步凭据。</summary>
    Task<SessionLoginResult> AuthenticateSessionAsync(
        LoginInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>登录第二步：校验验证码或恢复码并创建会话主体。</summary>
    Task<ClaimsPrincipal> CompleteTwoFactorLoginAsync(
        TwoFactorLoginInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>在当前服务端会话上按账号最新状态换发主体（受限会话完成两步验证设置后调用）。</summary>
    Task<ClaimsPrincipal> ReissueSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>用户注册。</summary>
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
}
