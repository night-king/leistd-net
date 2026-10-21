namespace Leistd.Lock.Redis.Options;

/// <summary>Redis 分布式锁选项。</summary>
public sealed class RedisLockOptions
{
    /// <summary>配置节名称 <c>Leistd:Lock:Redis</c>。</summary>
    public const string SectionName = "Leistd:Lock:Redis";

    /// <summary>锁键前缀，默认空串；<see langword="null"/> 归一化为空串。</summary>
    /// <remarks>
    /// 多个应用共用一个 Redis 实例时必须设置，否则同名业务键会互相阻塞。推荐应用名加环境，如 <c>acme-shop:prod:</c>；
    /// 不会自动补分隔符。
    /// </remarks>
    public string KeyPrefix
    {
        get => _keyPrefix;
        set => _keyPrefix = value ?? string.Empty;
    }

    private string _keyPrefix = string.Empty;

    /// <summary>锁租约时长，默认 30 秒。</summary>
    /// <remarks>
    /// 句柄按租约的三分之一自动续期，临界区可以长于本值；本值决定持有者进程异常终止后锁最多被占多久。
    /// </remarks>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>获取锁失败后的轮询重试间隔，默认 50 毫秒。</summary>
    /// <remarks>间隔越小抢锁越快、Redis 请求越多；高并发争抢同一把锁时应适当放大。</remarks>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}
