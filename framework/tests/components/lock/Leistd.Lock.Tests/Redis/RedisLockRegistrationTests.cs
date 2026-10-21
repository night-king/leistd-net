using Leistd.Lock.Abstractions;
using Leistd.Lock.Redis;
using Leistd.Lock.Redis.Options;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Leistd.Lock.Tests.Redis;

/// <summary><c>AddRedisDistributedLock</c> 的注册面：单例、幂等，校验器只有一份。</summary>
/// <remarks>
/// 多路复用器是昂贵的长连接，必须单例；重复调用若多建一份，进程会对 Redis 开两组连接。
/// 校验器不计入 <see cref="ServiceCollectionAssertions.AssertIdempotent"/>，多一份时每条配置错误报两遍。
/// 解析会真的连接 Redis，这里只断言描述符。
/// </remarks>
public sealed class RedisLockRegistrationTests
{
    private const string ConnectionString = "localhost:6379";

    [Fact]
    public void Registration_registers_the_lock_and_the_multiplexer_as_singletons()
    {
        var services = new ServiceCollection();

        services.AddRedisDistributedLock(ConnectionString);

        services.AssertSingle<RedisDistributedLock>(ServiceLifetime.Singleton);
        services.AssertSingle<IDistributedLock>(ServiceLifetime.Singleton);
        services.AssertSingle<IConnectionMultiplexer>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddRedisDistributedLock(ConnectionString));

    [Fact]
    public void Repeated_registration_keeps_one_options_validator()
    {
        var services = new ServiceCollection();

        services.AddRedisDistributedLock(ConnectionString).AddRedisDistributedLock(ConnectionString);

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<RedisLockOptions>));
    }
}
