using Leistd.Lock.Abstractions;
using Leistd.Settings.Management;
using Leistd.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leistd.Settings.Registration;

// 默认写入入口必须有锁与工作单元；缺了只在第一次写入时才失败，生产宿主默认又不做构建期依赖校验，启动期在此拒绝。
// 只看最终生效的那条 ISettingManager 登记：能从登记确定实现类型时据此判断，替换实现不强加这两项依赖；
// 工厂登记看不出类型，才在短作用域里解析一次，解析失败原样上抛，不推断成默认实现缺依赖。
// 不经 Host 启动（纯 ServiceCollection）的场景沿用标准的解析错误。
internal sealed class SettingManagerDependencyCheck(
    IServiceScopeFactory scopeFactory,
    IServiceCollection registrations) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        if (!IsRegistered(typeof(IDistributedLock)))
        {
            missing.Add($"{nameof(IDistributedLock)} (AddMemoryLocalLock() for a single instance, AddRedisDistributedLock(...) for multiple replicas)");
        }

        if (!IsRegistered(typeof(IUnitOfWorkManager)))
        {
            missing.Add($"{nameof(IUnitOfWorkManager)} (AddUnitOfWork(), plus AddUnitOfWorkEfCore() for the EF Core store)");
        }

        if (missing.Count == 0 || !await UsesDefaultManagerAsync())
        {
            return;
        }

        throw new InvalidOperationException(
            $"The default {nameof(ISettingManager)} serializes writes per tenant and commits each write in its own unit of work, " +
            $"but the host does not register {string.Join(" or ", missing)}. Register the missing services, " +
            $"or replace {nameof(ISettingManager)} with an implementation that does not need them.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool IsRegistered(Type serviceType)
        => registrations.Any(descriptor => descriptor.ServiceType == serviceType && !descriptor.IsKeyedService);

    private async Task<bool> UsesDefaultManagerAsync()
    {
        var descriptor = registrations.LastOrDefault(
            candidate => candidate.ServiceType == typeof(ISettingManager) && !candidate.IsKeyedService);
        if (descriptor is null)
        {
            return false;
        }

        if ((descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType()) is { } implementationType)
        {
            return implementationType == typeof(DefaultSettingManager);
        }

        // 读取器是 Scoped、管理器是 Transient：在短作用域里解析；异步释放，替换实现可以只支持 IAsyncDisposable
        await using var scope = scopeFactory.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ISettingManager>() is DefaultSettingManager;
    }
}
