using Leistd.Security.OneTimeCodes.VerificationCodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Leistd.Security.OneTimeCodes;

/// <summary>一次码能力的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册可替换的验证码摘要实现，先绑定配置再应用委托。</summary>
    /// <remarks>单例、重复调用保留宿主实现。缺失密钥允许启动，默认实现使用时失败；功能必需性由宿主验证。</remarks>
    public static IServiceCollection AddVerificationCodeDigest(this IServiceCollection services,
        Action<VerificationCodeOptions>? configure = null,
        string configSectionPath = VerificationCodeOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var options = services.AddOptions<VerificationCodeOptions>().BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IValidateOptions<VerificationCodeOptions>>(new VerificationCodeOptionsValidator(configSectionPath));
        options.ValidateOnStart();
        services.TryAddSingleton<IVerificationCodeDigest, HmacVerificationCodeDigest>();
        return services;
    }
}
