using System.Globalization;
using Leistd.Localization.Json;
using Leistd.Localization.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Leistd.Localization.AspNetCore.HostedServices;

namespace Leistd.Localization.AspNetCore;

/// <summary>Leistd JSON 本地化的注册与中间件装配入口。</summary>
public static class DependencyInjection
{
    /// <summary>
    /// 注册基于嵌入 JSON 的本地化：<see cref="CompositeStringLocalizerFactory"/> 按资源标记类型路由
    /// （仅 <see cref="JsonLocalizationOptions.JsonResourceTypes"/> 中的类型走 JSON，其余委派官方 RESX），
    /// 及 <see cref="IStringLocalizer"/> / <see cref="IStringLocalizer{T}"/> 解析，并按
    /// <see cref="JsonLocalizationOptions.SupportedCultures"/> 配置请求区域性。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">配置本地化选项（支持语言、追加资源程序集、登记 JSON 资源标记类型）。</param>
    /// <remarks>
    /// <para>支持语言只在 <see cref="JsonLocalizationOptions.SupportedCultures"/> 维护一份：首项即
    /// <see cref="JsonLocalizationOptions.DefaultCulture"/>，同时是请求默认区域性与资源回落语言。
    /// 列表为空或含无效文化名时启动失败。</para>
    /// <para>仅注册服务，不挂载中间件：请求 culture 解析由宿主显式调用 <see cref="UseJsonRequestLocalization"/>。
    /// 本组件所在程序集被默认登记为资源程序集之一，用于分发通用键（<c>Error:*</c>）。
    /// 不全局接管宿主本地化——未登记为 JSON 资源类型的 <see cref="IStringLocalizer{T}"/> 一律走官方 RESX 工厂。</para>
    /// <para>重复调用不重复登记服务，各次的 <paramref name="configure"/> 按调用顺序依次应用。</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddJsonLocalization(options =&gt;
    /// {
    ///     options.SupportedCultures = ["zh-CN", "en"];               // 首项为默认/回落语言
    ///     options.ResourceAssemblies.Add(typeof(Program).Assembly);
    ///     options.JsonResourceTypes.Add(typeof(OrderResource));   // 未登记的类型走官方 RESX
    /// });
    ///
    /// app.UseJsonRequestLocalization();
    /// </code>
    /// </example>
    public static IServiceCollection AddJsonLocalization(
        this IServiceCollection services,
        Action<JsonLocalizationOptions>? configure = null)
    {
        // JSON 本地化器与官方 RESX 工厂，由组合工厂按资源类型路由
        services.TryAddSingleton<JsonLocalizationResourceReader>();
        services.TryAddSingleton<JsonStringLocalizerFactory>();
        // 官方 ResourceManagerStringLocalizerFactory 依赖 IOptions<LocalizationOptions> / ILoggerFactory：由 AddLocalization 提供；
        // 它同时登记 IStringLocalizer<> → StringLocalizer<>，typed 本地化器经下面的组合工厂路由。
        services.AddLocalization();
        services.TryAddSingleton<ResourceManagerStringLocalizerFactory>();
        // 替换 AddLocalization 登记的 IStringLocalizerFactory：IStringLocalizer<T> 按类型路由，未登记的类型仍走官方 RESX
        services.Replace(ServiceDescriptor.Singleton<IStringLocalizerFactory, CompositeStringLocalizerFactory>());
        // 无参 IStringLocalizer 直接使用全局 JSON 词条；按 object 路由会误入 RESX 分支。
        services.TryAddTransient<IStringLocalizer>(sp =>
            sp.GetRequiredService<JsonStringLocalizerFactory>().Create(typeof(object)));

        var options = services.AddOptions<JsonLocalizationOptions>().Configure(options =>
        {
            // 框架自身资源程序集（承载通用键默认中英文案）
            var frameworkAssembly = typeof(JsonLocalizationOptions).Assembly;
            if (!options.ResourceAssemblies.Contains(frameworkAssembly))
                options.ResourceAssemblies.Add(frameworkAssembly);
        });
        if (configure is not null)
        {
            options.Configure(configure);
        }
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<JsonLocalizationOptions>, JsonLocalizationOptionsValidator>());
        options.ValidateOnStart();

        // 请求区域性从同一份支持语言派生，不另存清单。
        services.AddOptions<RequestLocalizationOptions>()
            .Configure<IOptions<JsonLocalizationOptions>>((request, json) =>
            {
                var cultures = json.Value.SupportedCultures.Select(name => new CultureInfo(name)).ToList();
                request.DefaultRequestCulture = new(json.Value.DefaultCulture);
                request.SupportedCultures = cultures;
                request.SupportedUICultures = cultures;
                request.ApplyCurrentCultureToResponseHeaders = true;
            });

        // 启动预热：资源解析与坏文件告警在启动阶段而非首个请求发生
        services.AddHostedService<LocalizationPreloadHostedService>();

        return services;
    }

    /// <summary>启用请求区域性解析中间件。</summary>
    /// <remarks>必须在任何读取当前区域性的中间件之前调用。</remarks>
    public static IApplicationBuilder UseJsonRequestLocalization(this IApplicationBuilder app)
        => app.UseRequestLocalization();
}
