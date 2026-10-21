using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Lock.Registration;

/// <summary>表示当前的 <c>IDistributedLock</c> 注册是进程内锁的兜底，而不是真正跨副本的实现。</summary>
/// <remarks>
/// 内存锁在没有分布式实现时兜底并登记本标记；分布式实现见到它只移除 <see cref="Fallback"/> 这一条，
/// 宿主在兜底之后自行注册的实现仍然保留。两者因此可以共存，结果与注册顺序无关。
/// </remarks>
/// <param name="fallback">兜底的 <c>IDistributedLock</c> 注册。</param>
public sealed class InProcessDistributedLockMarker(ServiceDescriptor fallback)
{
    /// <summary>兜底的 <c>IDistributedLock</c> 注册。</summary>
    public ServiceDescriptor Fallback { get; } = fallback;
}
