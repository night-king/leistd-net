using Microsoft.Extensions.DependencyInjection;
using Leistd.DependencyInjection.Extensions;
using Leistd.DependencyInjection.Abstractions;

namespace Leistd.DependencyInjection.Registration;

/// <summary>在构建服务提供程序前执行注册校验和描述符回调。</summary>
public class ServiceRegistrationCallbackFactory : IServiceProviderFactory<IServiceCollection>
{
    private readonly ServiceProviderOptions _options;

    /// <summary>创建工厂。</summary>
    /// <param name="options">服务提供器校验选项；未指定时使用 Microsoft DI 默认值。</param>
    public ServiceRegistrationCallbackFactory(ServiceProviderOptions? options = null)
    {
        _options = options ?? new ServiceProviderOptions();
    }

    /// <summary>返回待处理的服务集合。</summary>
    public virtual IServiceCollection CreateBuilder(IServiceCollection services)
    {
        return services;
    }

    /// <summary>依次执行校验器与描述符回调，再构建服务提供程序。</summary>
    public IServiceProvider CreateServiceProvider(IServiceCollection services)
    {
        // 校验必须先执行，否则回调改写后的工厂描述符会与原始工厂注册混淆。
        foreach (var validator in services.GetRegistrationValidatorList())
        {
            validator.Invoke(services);
        }

        var actionList = services.GetRegistrationActionList();

        if (actionList.Any())
        {
            // 工厂型描述符无法验证构造链，须在织入前校验原始注册。
            // 注册回调只登记拦截器，不得向集合追加服务。
            ValidateBeforeRewrite(services);


            ProcessServiceRegistrations(services, actionList);
        }

        return services.BuildServiceProvider(_options);
    }

    // 用未改写的副本校验一次，补回被织入服务丢掉的 ValidateOnBuild 覆盖。
    // 必须用副本：BuildServiceProvider 会 MakeReadOnly，在原集合上做之后就改不动了。
    private void ValidateBeforeRewrite(IServiceCollection services)
    {
        if (!_options.ValidateOnBuild)
        {
            return;
        }

        IServiceCollection probe = new ServiceCollection();
        foreach (var descriptor in services)
        {
            probe.Add(descriptor);
        }

        // 只为校验而构建，随即释放；校验阶段不解析任何服务，因此不产生副作用。
        ((IDisposable)probe.BuildServiceProvider(_options)).Dispose();
    }

    private void ProcessServiceRegistrations(
        IServiceCollection services,
        ServiceRegistrationActionList actionList)
    {
        // 回调可能修改服务集合，因此遍历快照。
        var descriptors = services.ToList();

        foreach (var descriptor in descriptors)
        {
            // 键控注册的实现信息挂在 Keyed* 系列属性上；读非键控属性只会得到 null，
            // 于是回调看不出实现类型，仅按服务类型判定的约定会连键控注册一起命中。
            var implementationType = descriptor.IsKeyedService
                ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
                : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

            // 跳过不可能实例化的类型；保留实现类型未知的工厂注册供回调按服务类型判断。
            if (implementationType is { IsAbstract: true } or { IsInterface: true })
            {
                continue;
            }

            var context = new OnServiceRegisteredContext(
                descriptor.ServiceType,
                implementationType,
                descriptor.ServiceKey);

            foreach (var action in actionList)
            {
                action.Invoke(context);
            }

            OnRegistrationProcessed(services, descriptor, context);
        }
    }

    /// <summary>单个描述符的回调执行完成后的扩展点。</summary>
    protected virtual void OnRegistrationProcessed(
        IServiceCollection services,
        ServiceDescriptor descriptor,
        IOnServiceRegisteredContext context)
    {
    }
}
