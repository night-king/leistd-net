using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <summary>忘记口令时凭已验证邮箱上的验证码设置新口令。</summary>
public interface IPasswordResetAppService : IAppService
{
    /// <summary>发送找回口令的验证码；不论邮箱是否对应能找回的账号，响应都一样。</summary>
    Task<EmailVerificationChallengeOutputDto> SendCodeAsync(
        SendEmailCodeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>凭验证码设置新口令，并撤销该账号的全部会话与令牌。</summary>
    Task ResetAsync(PasswordResetInputDto input, CancellationToken cancellationToken = default);
}
