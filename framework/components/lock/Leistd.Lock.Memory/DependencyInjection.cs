using Leistd.Lock.Memory.HostedServices;
using Leistd.Lock.Registration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Leistd.Lock.Abstractions;

namespace Leistd.Lock.Memory;

/// <summary>进程内锁注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>注册进程内锁和清理服务；尚无分布式锁实现时，同时用它兜底 <see cref="IDistributedLock"/>。</summary>
    /// <remarks>
    /// 单副本部署用本方法即可；多副本部署另外注册 <c>AddRedisDistributedLock(...)</c>，它会替换这里的兜底、
    /// 与调用顺序无关，<see cref="ILocalLock"/> 继续可用于只需进程内互斥的场景（如各实例各自预热缓存）。
    /// 宿主已自行注册 <see cref="IDistributedLock"/> 时不兜底、不覆盖。
    /// 兜底在多副本下不提供跨副本互斥，因此兜底的 <see cref="IDistributedLock"/> 首次被解析时记一条 Warning。
    /// 可重复调用：服务、兜底与清理服务都只注册一次。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddMemoryLocalLock();   // 单副本部署
    /// </code>
    /// </example>
    public static IServiceCollection AddMemoryLocalLock(this IServiceCollection services)
    {
        services.TryAddSingleton<MemoryLocalLock>();
        services.TryAddSingleton<ILocalLock>(sp => sp.GetRequiredService<MemoryLocalLock>());

        // 只看非 keyed 注册：宿主的具名锁不提供默认 IDistributedLock，与 TryAdd 同一判定
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDistributedLock) && !descriptor.IsKeyedService))
        {
            var fallback = ServiceDescriptor.Singleton<IDistributedLock>(sp =>
            {
                sp.GetRequiredService<ILogger<MemoryLocalLock>>().LogWarning(
                    "IDistributedLock is served by the in-process memory lock. Mutual exclusion holds " +
                    "within this process only; it does NOT hold across replicas. Register " +
                    "AddRedisDistributedLock(...) before scaling beyond a single instance.");

                return sp.GetRequiredService<MemoryLocalLock>();
            });
            services.Add(fallback);
            services.AddSingleton(new InProcessDistributedLockMarker(fallback));
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MemoryLockCleanupHostedService>());
        return services;
    }
}
