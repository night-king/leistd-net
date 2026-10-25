namespace CompanyName.ProjectName.Application.Auth.Dtos;

/// <summary>当前租户的注册摘要，匿名可读：登录页、注册页、首页据此决定是否给出注册入口与验证方式。</summary>
public record SecurityConfigOutputDto
{
    /// <summary>是否开放自助注册。</summary>
    public bool RegistrationEnabled { get; init; }

    /// <summary>部署配置了通行密钥的依赖方：登录页据此给出"用通行密钥登录"与条件式自动填充。</summary>
    public bool PasskeysAvailable { get; init; }
#if (Email)

    /// <summary>注册时是否要求邮箱验证。</summary>
    public bool EnableEmailVerification { get; init; }

    /// <summary>部署具备发邮箱验证码的前提（验证码摘要密钥已配置）。</summary>
    /// <remarks>
    /// 与上面的开关是两回事：那个管"注册时要不要验证"，这个管"能不能发验证码"。
    /// 个人资料里的"验证我的邮箱"据此决定是给出发送按钮，还是只说明暂不可用——
    /// 不给这一项的话，用户点下去得到的是一个 500。
    /// </remarks>
    public bool EmailVerificationAvailable { get; init; }
#endif
}
