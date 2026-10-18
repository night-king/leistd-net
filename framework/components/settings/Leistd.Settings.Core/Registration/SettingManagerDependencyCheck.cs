using Leistd.Lock.Abstractions;
using Leistd.Settings.Management;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leistd.Settings.Registration;

// 默认写入入口必须有锁与工作单元；缺了只在第一次写入时才失败，生产宿主默认又不做构建期依赖校验，启动期在此拒绝。
// 只在依赖确实缺失时才解析管理器：宿主替换了 ISettingManager 时解析结果不是默认实现，不强加这两项依赖。
// 不经 Host 启动（纯 ServiceCollection）的场景沿用标准的解析错误。
internal sealed class SettingManagerDependencyCheck(IServiceScopeFactory scopeFactory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 读取器是 Scoped、管理器是 Transient：在短作用域里解析，不从根容器取
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var registry = services.GetService<IServiceProviderIsService>();

        var missing = new List<string>();
        if (!IsRegistered(services, registry, typeof(IDistributedLock)))
        {
            missing.Add($"{nameof(IDistributedLock)} (AddMemoryLocalLock() for a single instance, AddRedisDistributedLock(...) for multiple replicas)");
        }

        if (!IsRegistered(services, registry, typeof(IUnitOfWorkManager)))
        {
            missing.Add($"{nameof(IUnitOfWorkManager)} (AddUnitOfWork(), plus AddUnitOfWorkEfCore() for the EF Core store)");
        }

        if (missing.Count == 0)
        {
            return Task.CompletedTask;
        }

        Exception? resolutionFailure = null;
        try
        {
            if (services.GetService<ISettingManager>() is not DefaultSettingManager)
            {
                return Task.CompletedTask;
            }
        }
        catch (InvalidOperationException exception)
        {
            resolutionFailure = exception;
        }

        throw new InvalidOperationException(
            $"The default {nameof(ISettingManager)} serializes writes per tenant and commits each write in its own unit of work, " +
            $"but the host does not register {string.Join(" or ", missing)}. Register the missing services, " +
            $"or replace {nameof(ISettingManager)} with an implementation that does not need them.",
            resolutionFailure);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsRegistered(IServiceProvider services, IServiceProviderIsService? registry, Type serviceType)
        => registry?.IsService(serviceType) ?? services.GetService(serviceType) is not null;
}
