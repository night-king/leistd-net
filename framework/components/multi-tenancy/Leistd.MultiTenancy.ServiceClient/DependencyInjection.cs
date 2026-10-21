using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.ServiceClient.Options;
using Leistd.MultiTenancy.ServiceClient.Stores;
using Leistd.ServiceClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Leistd.MultiTenancy.ServiceClient;

/// <summary>远端租户连接存储的注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册 <see cref="ITenantConnectionConfigurationStore"/> 的远端实现：回源控制面经 <c>MapTenantConnections</c> 暴露的机器端点。</summary>
    /// <remarks>
    /// <para>与控制库的 EF 实现二选一：连接配置只能有一个权威来源，已注册其它实现时抛出。
    /// 以相同参数重复调用不重复登记，返回同一命名客户端的构建器；换用另一 <paramref name="serviceName"/>
    /// 或 <paramref name="configSectionPath"/> 时抛出 <see cref="InvalidOperationException"/>。
    /// 解析、缓存与单飞另由 <c>AddRemoteTenantConnectionResolution()</c> 注册。</para>
    /// <para>先绑定 <paramref name="configSectionPath"/>（默认 <c>Leistd:ServiceClients:{serviceName}</c>），再应用
    /// <paramref name="configure"/>；<c>BaseAddress</c> 缺失或不是绝对地址时启动失败并报出实际键名。</para>
    /// <para>返回的构建器上由宿主决定鉴权方式（如 client credentials）与弹性策略；控制面按名字下发已解密的连接串，
    /// 本服务不持有控制面的密钥环。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddRemoteTenantConnectionResolution();
    /// builder.Services.AddRemoteTenantConnectionStore("Identity")
    ///     .AddClientCredentials()
    ///     .AddStandardResilienceHandler();
    /// </code>
    /// </example>
    /// <param name="services">服务集合。</param>
    /// <param name="serviceName">控制面服务名：命名 HttpClient 与默认配置节 <c>Leistd:ServiceClients:{serviceName}</c>。</param>
    /// <param name="configure">在配置节之后应用的选项配置。</param>
    /// <param name="configSectionPath">选项绑定的配置节；省略时为 <c>Leistd:ServiceClients:{serviceName}</c>。</param>
    public static IHttpClientBuilder AddRemoteTenantConnectionStore(
        this IServiceCollection services,
        string serviceName,
        Action<RemoteTenantConnectionClientOptions>? configure = null,
        string? configSectionPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (configSectionPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);
        }

        var registration = new RemoteStoreRegistration(
            serviceName,
            configSectionPath ?? $"{Leistd.ServiceClient.DependencyInjection.ConfigurationSectionPrefix}:{serviceName}");
        var registered = services.Select(d => d.ImplementationInstance).OfType<RemoteStoreRegistration>().FirstOrDefault();
        if (registered is not null && registered != registration)
        {
            throw new InvalidOperationException(
                $"The remote tenant connection store is already registered for service '{registered.ServiceName}' " +
                $"(section '{registered.ConfigSectionPath}'); it cannot also use service '{serviceName}' " +
                $"(section '{registration.ConfigSectionPath}'). Tenant connection configuration has exactly one authoritative source.");
        }

        if (registered is null && services.Any(descriptor => descriptor.ServiceType == typeof(ITenantConnectionConfigurationStore)))
        {
            throw new InvalidOperationException(
                "Tenant connection configuration must have exactly one authoritative source: either the control " +
                "database (AddMultiTenancyEfCore) or a remote control plane (AddRemoteTenantConnectionStore), never both.");
        }

        var builder = services.AddServiceClient<ITenantConnectionConfigurationStore, RemoteTenantConnectionStore, RemoteTenantConnectionClientOptions>(
            serviceName,
            configure,
            registration.ConfigSectionPath);
        if (registered is not null)
        {
            return builder;
        }

        services.AddSingleton(registration);
        services.AddOptions<RemoteTenantConnectionClientOptions>().ValidateOnStart();
        services.AddSingleton<IValidateOptions<RemoteTenantConnectionClientOptions>>(
            new RemoteTenantConnectionClientOptionsValidator(registration.ConfigSectionPath));

        // 同一个客户端也服务逐库作业的库目录，不另建认证与 HTTP 管道。
        services.TryAddTransient<ITenantDatabaseDirectory>(provider =>
            (ITenantDatabaseDirectory)provider.GetRequiredService<ITenantConnectionConfigurationStore>());

        return builder;
    }

    private sealed record RemoteStoreRegistration(string ServiceName, string ConfigSectionPath);
}
