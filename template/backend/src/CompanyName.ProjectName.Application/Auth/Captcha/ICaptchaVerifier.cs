namespace CompanyName.ProjectName.Application.Auth.Captcha;

/// <summary>图形验证码的校验与消费。</summary>
public interface ICaptchaVerifier
{
    /// <summary>验证码正确且未过期时返回 true；无论对错，token 都随即作废。</summary>
    Task<bool> VerifyAsync(string token, string code, CancellationToken cancellationToken = default);
}
