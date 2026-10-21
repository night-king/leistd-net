using Mapster;
using MapsterMapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Leistd.ObjectMapping.Mapster.Options;
using Leistd.ObjectMapping.Mapster.Services;
using Leistd.ObjectMapping.Abstractions;

namespace Leistd.ObjectMapping.Mapster;

/// <summary>Mapster 对象映射的注册入口：注册 <c>IObjectMapper</c> 与组件自己的 <see cref="TypeAdapterConfig"/>。</summary>
public static class DependencyInjection
{
    /// <summary>注册 Mapster 对象映射器。</summary>
    /// <remarks>
    /// 映射配置用 Mapster 官方的 <see cref="IRegister"/> 书写，经 <c>Configurators</c> 扫描登记到组件的
    /// <see cref="TypeAdapterConfig"/>（不是 <c>TypeAdapterConfig.GlobalSettings</c>）。业务代码只注入
    /// <c>IObjectMapper</c>；配置里的嵌套映射交给 Mapster 按同一份配置完成，不调用无参 <c>Adapt&lt;T&gt;()</c>——
    /// 那会改用全局配置，本组件登记的规则在嵌套处静默失效。
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加（各次加入的 <c>Configurators</c> 依次生效）。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMapsterObjectMapper(options =&gt;
    /// {
    ///     options.Configurators.Add(config =&gt; config.Scan(typeof(OrderMappings).Assembly));
    ///     options.ValidateMappings = true;   // 开发/测试环境尽早暴露未配置的映射
    /// });
    ///
    /// public class OrderMappings : IRegister
    /// {
    ///     public void Register(TypeAdapterConfig config) =&gt;
    ///         config.NewConfig&lt;Order, OrderOutputDto&gt;()
    ///             .Map(dest =&gt; dest.CustomerName, src =&gt; src.Customer.Name);
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddMapsterObjectMapper(
        this IServiceCollection services,
        Action<MapsterOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IMapper>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MapsterOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<MapsterObjectMapper>>();

            var config = new TypeAdapterConfig();
            config.Default.PreserveReference(true);

            foreach (var configurator in options.Configurators)
            {
                configurator(config);
            }

            if (options.ValidateMappings)
            {
                logger.LogInformation("Validating Mapster mapping configuration");
                config.Compile();
            }

            return new Mapper(config);
        });

        services.TryAddSingleton<IObjectMapper, MapsterObjectMapper>();

        return services;
    }
}
