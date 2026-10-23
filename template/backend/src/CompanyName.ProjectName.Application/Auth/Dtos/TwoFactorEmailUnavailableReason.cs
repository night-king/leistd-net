namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>邮箱验证码不能作为第二步的原因。</summary>
public enum TwoFactorEmailUnavailableReason
{
    /// <summary>所在组织没有允许邮箱验证码。</summary>
    NotAllowed,

    /// <summary>账号邮箱尚未验证。</summary>
    EmailNotVerified,

    /// <summary>部署暂时发不了验证码（验证码摘要密钥未配置）。</summary>
    DeliveryUnavailable
}
