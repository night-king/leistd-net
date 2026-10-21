using Leistd.Lock.Abstractions;
using Leistd.Lock.Memory;
using Leistd.Lock.Tests.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leistd.Lock.Tests.Memory;

/// <summary>进程内实现对 <see cref="ILock"/> 契约的兑现。</summary>
/// <remarks>
/// Redis 实现的同一批语义无法在没有 Redis 服务的情况下黑盒验证，
/// 其重试与超时循环由 <c>Redis/RedisLockContractTests</c> 以单元粒度直接驱动
/// <c>TryAcquireWithRetryAsync</c> 覆盖——两处合起来才是这个契约的完整安全网。
/// </remarks>
public sealed class MemoryLocalLockContractTests : LockContractTests, IDisposable
{
    private readonly MemoryLocalLock _lock = new(NullLogger<MemoryLocalLock>.Instance);

    /// <inheritdoc />
    protected override ILock CreateLock() => _lock;

    public void Dispose() => _lock.Dispose();
}
