using Leistd.Lock.Abstractions;
using Leistd.Lock.Redis;
using Leistd.Lock.Redis.Options;
using Leistd.Lock.Tests.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Lock.Tests.Redis;

/// <summary>Redis 实现对 <see cref="ILock"/> 契约的兑现——跑在真实 Redis 上。</summary>
/// <remarks>
/// <para>这一批是 <c>Leistd.Lock.Redis</c> 唯一会真正执行 <c>LockTake</c> / <c>LockRelease</c> /
/// <c>LockExtend</c> 的地方。只驱动 <c>TryAcquireWithRetryAsync</c> 重试循环、喂假 attempt 委托的用例
/// 执行不到这三条路径。</para>
/// <para>租约、令牌归属这些 Redis 自己的话题在 <see cref="RedisLeaseTests"/>，
/// 本类只跑跨实现共享的契约。</para>
/// </remarks>
public sealed class RedisDistributedLockContractTests : LockContractTests
{
    /// <inheritdoc />
    protected override ILock CreateLock()
    {
        Skip.IfNot(RedisServer.IsAvailable, RedisServer.SkipReason);
        return NewLock();
    }

    /// <summary>每个用例用独立键前缀，使同一个 Redis 实例上的并行用例互不干扰。</summary>
    /// <remarks>
    /// 契约套件里的键本身已经是随机的；前缀再隔离一层，是为了让本地开发者
    /// 直接连自己长期运行的 Redis 也不会撞上业务数据。
    /// </remarks>
    internal static RedisDistributedLock NewLock(Action<RedisLockOptions>? configure = null)
    {
        var options = new RedisLockOptions { KeyPrefix = $"leistd-test:{Guid.NewGuid():N}:" };
        configure?.Invoke(options);

        return new RedisDistributedLock(
            RedisServer.Connection,
            Options.Create(options),
            NullLogger<RedisDistributedLock>.Instance);
    }
}
