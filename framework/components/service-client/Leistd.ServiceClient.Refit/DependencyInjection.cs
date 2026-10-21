using Leistd.ServiceClient.Options;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using Leistd.ServiceClient.Refit.Options;

namespace Leistd.ServiceClient.Refit;

/// <summary>Refit 服务客户端注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册 Refit 客户端并装配标准调用管道。</summary>
    /// <remarks>
    /// <para>须经本方法注册而非裸 <c>AddRefitClient</c>，失败才抛 <c>RemoteServiceException</c> 而不是 Refit 的 <c>ApiException</c>。</para>
    /// <para>先绑定 <paramref name="configSectionPath"/>（默认 <c>Leistd:ServiceClients:{serviceName}</c>），再应用
    /// <paramref name="configure"/>；无主机的 <c>ServiceCollection</c> 须自行注册 <c>IConfiguration</c>。</para>
    /// <para>按 <paramref name="serviceName"/> 区分客户端，不同服务名可多次登记。同一服务名以相同的接口、
    /// 选项类型与配置节重复调用时不重复登记客户端与处理器，只追加 <paramref name="configure"/>，<paramref name="settings"/>
    /// 沿用首次登记的值，并返回该命名客户端的构建器；同一服务名换用其他接口、选项类型或配置节，或同一选项类型用于另一服务名时抛出
    /// <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    /// <typeparam name="TApi">Refit 接口，须对源生成器可见（public，或 internal + InternalsVisibleTo）。</typeparam>
    /// <typeparam name="TOptions">客户端配置类型，每个客户端一个具体类型。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <param name="serviceName">下游服务名：命名 HttpClient、默认配置节与日志类别。</param>
    /// <param name="configure">在配置节之后应用的选项配置。</param>
    /// <param name="configSectionPath">选项绑定的配置节；省略时为 <c>Leistd:ServiceClients:{serviceName}</c>。</param>
    /// <param name="settings">自定义 <see cref="RefitSettings"/>；默认 <see cref="ServiceClientRefitSettings.Create"/>。</param>
    /// <returns>可继续追加认证（OAuth 包）、弹性等处理器的构建器。</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddRefitServiceClient&lt;IIdentityApi, IdentityClientOptions&gt;("identity")
    ///     .AddClientCredentials();
    ///
    /// // Refit 接口须对源生成器可见（public，或 internal + InternalsVisibleTo）
    /// public interface IIdentityApi
    /// {
    ///     [Get("/api/users/{id}")] Task&lt;UserDto&gt; GetAsync(Guid id);
    /// }
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddRefitServiceClient<TApi, TOptions>(
        this IServiceCollection services,
        string serviceName,
        Action<TOptions>? configure = null,
        string? configSectionPath = null,
        RefitSettings? settings = null)
        where TApi : class
        where TOptions : ServiceClientOptions, new()
        => ServiceClient.DependencyInjection.AddServiceClientRegistration(
            services,
            serviceName,
            typeof(TApi),
            implementationType: null,
            configure,
            configSectionPath,
            () => services.AddRefitClient<TApi>(settings ?? ServiceClientRefitSettings.Create(), httpClientName: serviceName));
}
