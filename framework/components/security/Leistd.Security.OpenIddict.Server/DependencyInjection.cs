using Leistd.Security.OpenIddict.Server.Handlers;
using Leistd.Security.OpenIddict.Server.Pruning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using global::OpenIddict.Server;

namespace Leistd.Security.OpenIddict.Server;

/// <summary>OpenIddict 签发补充能力的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>约束交换令牌不晚于源令牌到期，重复登记幂等。</summary>
    public static IServiceCollection AddTokenExchangeExpirationLimit(this IServiceCollection services)
    {
        services.TryAdd(ConfigureTokenExchangeExpiration.Descriptor.ServiceDescriptor);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureTokenExchangeExpiration>());
        return services;
    }

    /// <summary>注册默认清理任务及参数，不自动排程。</summary>
    /// <remarks>宿主显式选择原生管理器的存储，并登记周期任务的排期、范围或自己的 IRecurringJob。</remarks>
    public static IServiceCollection AddOpenIddictPruning(this IServiceCollection services,
        Action<OpenIddictPruningOptions>? configure = null,
        string configSectionPath = OpenIddictPruningOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        var options = services.AddOptions<OpenIddictPruningOptions>().BindConfiguration(configSectionPath);
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IValidateOptions<OpenIddictPruningOptions>>(new OpenIddictPruningOptionsValidator(configSectionPath));
        options.ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<OpenIddictPruningJob>();
        return services;
    }
}
