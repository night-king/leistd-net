using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork.Attributes;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.EventHandlers;
using Leistd.UnitOfWork.Events;
using Leistd.UnitOfWork.Interceptors;
using Leistd.UnitOfWork.Registration;
using Leistd.DependencyInjection.Extensions;
using Leistd.DependencyInjection.DynamicProxy.Extensions;
using Leistd.DependencyInjection.Abstractions;
using Microsoft.Extensions.Options;

namespace Leistd.UnitOfWork;

/// <summary>工作单元核心服务注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册工作单元核心服务：绑定配置节，再应用宿主的编程式配置（代码覆盖配置文件）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">编程式配置，在配置节绑定之后应用。</param>
    /// <param name="configSectionPath">配置节路径，默认 <c>Leistd:UnitOfWork</c>。</param>
    /// <remarks>
    /// 默认选项在宿主启动时校验，校验消息以实际配置节的键开头：<c>Timeout</c> 只能为空或 1 秒至 <see cref="int.MaxValue"/> 秒，
    /// <c>IsolationLevel</c> 只能为空或已定义的枚举值。单次传给 <see cref="IUnitOfWorkManager.Begin"/> 的选项按同一判据校验。
    /// 可重复调用：服务只注册一次，<paramref name="configure"/> 每次都叠加；重复调用换用另一配置节时抛出 <see cref="InvalidOperationException"/>。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddUnitOfWork(options =&gt;
    /// {
    ///     options.IsTransactional = true;
    ///     options.Timeout = TimeSpan.FromSeconds(30);
    /// });
    ///
    /// // 声明式边界（拦截器自动 Begin → SaveChanges → Commit）
    /// [UnitOfWork]
    /// public async Task PlaceOrderAsync(CreateOrderInput input) { /* ... */ }
    /// </code>
    /// </example>
    public static IServiceCollection AddUnitOfWork(
        this IServiceCollection services,
        Action<UnitOfWorkOptions>? configure = null,
        string configSectionPath = UnitOfWorkOptions.SectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configSectionPath);

        // 默认选项只有一份：换用另一配置节的重复调用会让校验消息报错键名
        if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<UnitOfWorkOptionsValidator>().FirstOrDefault()
                is { } registered && registered.ConfigSectionPath != configSectionPath)
        {
            throw new InvalidOperationException(
                $"AddUnitOfWork() already binds '{registered.ConfigSectionPath}'; it cannot also bind '{configSectionPath}'.");
        }

        var options = services.AddOptions<UnitOfWorkOptions>().BindConfiguration(configSectionPath);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<UnitOfWorkOptions>>(
            new UnitOfWorkOptionsValidator(configSectionPath)));

        return services.AddUnitOfWorkCore();
    }

    private static IServiceCollection AddUnitOfWorkCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.TryAddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.TryAddTransient<IUnitOfWork, DefaultUnitOfWork>();
        services.TryAddTransient<UnitOfWorkInterceptor>();
        services.TryAddTransient<UnitOfWorkEventHandlerInterceptor>();

        // 活动工作单元内的事件交给阶段调度，而不是立即分发。
        services.TryAddSingleton<ILocalEventDeferrer, UnitOfWorkLocalEventDeferrer>();

        // 无法织入的声明必须在宿主启动时失败。
        services.AddRegistrationValidator(UnitOfWorkRegistrationValidator.Validate);
        // 注册校验只在代理工厂里执行；工厂本身漏接时由启动检查兜住。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, UnitOfWorkWeavingCheck>());

        services.OnServiceRegistered(context =>
        {
            if (ShouldInterceptUnitOfWork(context))
            {
                context.AddInterceptor(typeof(UnitOfWorkInterceptor));
            }
        });

        services.OnServiceRegistered(context =>
        {
            if (ShouldInterceptEventHandler(context))
            {
                context.AddInterceptor(typeof(UnitOfWorkEventHandlerInterceptor));
            }
        });

        return services;
    }

    private static bool ShouldInterceptUnitOfWork(IOnServiceRegisteredContext context)
    {
        var implementationType = context.ImplementationType;
        if (implementationType == null) return false;

        // Microsoft DI 不支持开放泛型服务类型与代理工厂组合。
        if (context.ServiceType.IsGenericTypeDefinition) return false;

        if (implementationType.GetCustomAttributes(typeof(UnitOfWorkAttribute), true).Any())
            return true;

        return implementationType.GetMethods()
            .Any(m => m.GetCustomAttributes(typeof(UnitOfWorkAttribute), true).Any());
    }

    // 所有事件处理器都必须织入，以便过滤 BeforeCommit 和 AfterCommit 阶段。
    private static bool ShouldInterceptEventHandler(IOnServiceRegisteredContext context)
    {
        if (!context.ServiceType.IsGenericType)
            return false;

        // 开放泛型无法织入，由注册校验器拒绝。
        if (context.ServiceType.IsGenericTypeDefinition)
            return false;

        return context.ServiceType.GetGenericTypeDefinition() == typeof(IEventHandler<>);
    }
}
