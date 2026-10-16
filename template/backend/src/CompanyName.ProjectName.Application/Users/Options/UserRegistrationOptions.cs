using System.ComponentModel.DataAnnotations;

namespace CompanyName.ProjectName.Application.Users.Options;

/// <summary>用户注册策略的部署基线；运行期覆盖经应用策略提供器读取。</summary>
public class UserRegistrationOptions
{
    public const string SectionName = "UserRegistration";

    /// <summary>是否开放自助注册；不影响管理员建号，也不影响外部登录首次登录自动建号。</summary>
    public bool Enabled { get; set; } = true;
#if (Email)

    /// <summary>是否开启邮箱验证（若不开启则只验证图形验证码）。</summary>
    public bool EnableEmailVerification { get; set; } = false;
#endif

    /// <summary>图形验证码有效期（分钟）。</summary>
    [Range(1, 60)]
    public int CaptchaExpiryMinutes { get; set; } = 5;
#if (Email)

    /// <summary>邮箱验证码有效期（分钟）。</summary>
    [Range(1, 60)]
    public int EmailCodeExpiryMinutes { get; set; } = 5;

    /// <summary>发送邮箱验证码频率限制（秒）。</summary>
    [Range(1, 3600)]
    public int EmailCodeSendIntervalSeconds { get; set; } = 60;

    /// <summary>单个邮箱验证挑战允许的最大错误尝试次数。</summary>
    [Range(1, 20)]
    public int EmailCodeMaxAttempts { get; set; } = 5;
#endif
}
