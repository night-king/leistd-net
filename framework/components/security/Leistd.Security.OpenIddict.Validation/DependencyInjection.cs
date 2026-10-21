using Leistd.Security.OpenIddict.Validation.SigningKeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using global::OpenIddict.Validation;
using global::OpenIddict.Validation.SystemNetHttp;

namespace Leistd.Security.OpenIddict.Validation;

/// <summary>OpenIddict 远端验证补充能力的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册未知 kid 的同请求刷新与限频；保留原生验证管道和宿主时间源。</summary>
    /// <remarks>宿主先启用原生 Validation/SystemNetHttp，并显式设置阻塞刷新开关。重复调用仅挂载一次，配置按调用叠加。</remarks>
    public static IServiceCollection AddSigningKeyRefresh(this IServiceCollection services,
        Action<SigningKeyRefreshOptions>? configure = null,
        string configSectionPath = SigningKeyRefreshOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var options = services.AddOptions<SigningKeyRefreshOptions>().BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IValidateOptions<SigningKeyRefreshOptions>>(new SigningKeyRefreshOptionsValidator(configSectionPath));
        options.ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAdd(RefreshSigningKeysOnUnknownKeyIdentifier.Descriptor.ServiceDescriptor);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<OpenIddictValidationOptions>, ConfigureSigningKeyRefresh>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<OpenIddictValidationSystemNetHttpOptions>, ConfigureSigningKeyRefreshHttpClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIddictValidationOptions>, ThrottleSigningKeyRefresh>());
        return services;
    }
}
