#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
#endif
using CompanyName.ProjectName.Application.Auth.Captcha;
using CompanyName.ProjectName.Application.Auth.EmailVerification;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Errors;
using Leistd.Ddd.Application.AppServices;
using Leistd.ExceptionHandling;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

public class EmailVerificationAppService(
    IUserRegistrationPolicyProvider registrationPolicy,
    ICaptchaVerifier captchaVerifier,
    EmailChallengeStore emailChallengeStore,
    UserDomainService userDomainService) : BaseAppService, IEmailVerificationAppService
{
    public async Task<EmailVerificationChallengeOutputDto> SendEmailCodeAsync(
        SendEmailCodeInputDto input,
        CancellationToken cancellationToken = default)
    {
        // 功能关闭时明确拒绝。不拒绝的话请求会一路走到摘要计算，
        // 在"密钥未配置"处失败——那个错误对调用方毫无意义，
        // 因为它真正的问题是这个功能压根没开
        var policy = await registrationPolicy.GetAsync(cancellationToken);
        if (!policy.Enabled)
        {
            throw new BusinessException(AuthErrorCodes.RegistrationDisabled, "Self sign-up is not open.");
        }

        if (!policy.EnableEmailVerification)
        {
            throw new BusinessException(AuthErrorCodes.EmailVerificationDisabled, "Email verification is not enabled.");
        }

        var isValidCaptcha = await captchaVerifier.VerifyAsync(
            input.CaptchaToken,
            input.CaptchaCode,
            cancellationToken);
        if (!isValidCaptcha)
        {
            throw new BusinessException(AuthErrorCodes.CaptchaInvalid, "The image captcha is incorrect or has expired.");
        }

        // 与建号、改邮箱同一判定：看得见软删除行、按唯一索引的原样比较。
        // 这里先放行、建号时再撞上，用户就白收了一封验证码。
        var email = input.Email.Trim();
        if (!await userDomainService.IsEmailAvailableAsync(email, cancellationToken))
        {
            throw new BusinessException(UserErrorCodes.EmailTaken, "Email is already in use.")
                .WithData("Email", email);
        }

        return await emailChallengeStore.IssueRegistrationAsync(email, policy, cancellationToken);
    }
}
