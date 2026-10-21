using CompanyName.ProjectName.Application.Auth.Dtos;
using Leistd.Ddd.Application.Contracts.AppServices;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

public interface IEmailVerificationAppService : IAppService
{
    /// <summary>发送邮箱验证码。</summary>
    Task<EmailVerificationChallengeOutputDto> SendEmailCodeAsync(
        SendEmailCodeInputDto input,
        CancellationToken cancellationToken = default);
}
