using Leistd.AmbientContext;
using Leistd.Tracing.Abstractions;
using Leistd.Tracing.AmbientContext;
using Leistd.Tracing.Options;
using Leistd.Tracing.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Leistd.Tracing;

/// <summary>关联标识核心能力的注册入口：<c>ICorrelationIdProvider</c> 与非 HTTP 入口的环境上下文维度。</summary>
public static class DependencyInjection
{
    /// <summary>注册关联标识核心能力（不含 ASP.NET Core 中间件）。</summary>
    /// <remarks>
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加；
    /// 换用另一配置节时两个配置节都会绑定（后绑定的覆盖同名键），校验消息仍按首次调用的配置节给出键名。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">在配置节绑定之后应用的覆盖。</param>
    /// <param name="configSectionPath">选项绑定的配置节路径。</param>
    /// <example>
    /// <code>
    /// builder.Services.AddCorrelationIdCore();
    ///
    /// // 非 HTTP 入口显式建立作用域：指定值优先，否则取当前 Activity 的 TraceId
    /// using (ambientContext.Begin(principal, correlationId: messageCorrelationId))
    /// {
    ///     await handler.HandleAsync(message, ct);
    /// }
    /// </code>
    /// </example>
    public static IServiceCollection AddCorrelationIdCore(
        this IServiceCollection services,
        Action<CorrelationIdOptions>? configure = null,
        string configSectionPath = CorrelationIdOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<CorrelationIdOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CorrelationIdOptions>>(
            new CorrelationIdOptionsValidator(configSectionPath)));

        services.TryAddSingleton<ICorrelationIdProvider, CorrelationIdProvider>();
        // 非 HTTP 入口的关联标识维度：Hub 调用、后台作业不经入站中间件，由环境上下文原语建立
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAmbientContextContributor, CorrelationIdAmbientContextContributor>());
        return services;
    }
}
