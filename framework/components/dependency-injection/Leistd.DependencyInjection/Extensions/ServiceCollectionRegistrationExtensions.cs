using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Registration;
using Leistd.DependencyInjection.Abstractions;

namespace Leistd.DependencyInjection.Extensions;

/// <summary>服务注册回调和校验扩展。</summary>
public static class ServiceCollectionRegistrationExtensions
{
    /// <summary>注册在构建 <see cref="IServiceProvider"/> 时对每个描述符执行的回调；须经 Leistd 服务提供器工厂构建才会执行。</summary>
    public static IServiceCollection OnServiceRegistered(
        this IServiceCollection services,
        Action<IOnServiceRegisteredContext> registrationAction)
    {
        GetOrCreateRegistrationActionList(services).Add(registrationAction);
        return services;
    }

    /// <summary>获取已注册的描述符回调。</summary>
    public static ServiceRegistrationActionList GetRegistrationActionList(this IServiceCollection services)
    {
        return GetOrCreateRegistrationActionList(services);
    }

    /// <summary>注册在描述符回调之前执行的服务集合校验器；不满足约定时应抛出，阻止宿主启动。</summary>
    public static IServiceCollection AddRegistrationValidator(
        this IServiceCollection services,
        Action<IServiceCollection> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);

        GetOrCreateRegistrationValidatorList(services).Add(validator);
        return services;
    }

    /// <summary>获取已注册的服务集合校验器。</summary>
    public static ServiceRegistrationValidatorList GetRegistrationValidatorList(this IServiceCollection services)
    {
        return GetOrCreateRegistrationValidatorList(services);
    }

    private static ServiceRegistrationActionList GetOrCreateRegistrationActionList(IServiceCollection services)
    {
        var actionList = services
            .FirstOrDefault(d => d.ServiceType == typeof(ServiceRegistrationActionList))
            ?.ImplementationInstance as ServiceRegistrationActionList;

        if (actionList == null)
        {
            actionList = new ServiceRegistrationActionList();

            services.AddSingleton(actionList);
        }

        return actionList;
    }

    private static ServiceRegistrationValidatorList GetOrCreateRegistrationValidatorList(IServiceCollection services)
    {
        var validatorList = services
            .FirstOrDefault(d => d.ServiceType == typeof(ServiceRegistrationValidatorList))
            ?.ImplementationInstance as ServiceRegistrationValidatorList;

        if (validatorList == null)
        {
            validatorList = new ServiceRegistrationValidatorList();
            services.AddSingleton(validatorList);
        }

        return validatorList;
    }
}
