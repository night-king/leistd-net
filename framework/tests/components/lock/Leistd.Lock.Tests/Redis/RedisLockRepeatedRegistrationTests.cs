using Leistd.Lock.Redis;
using Leistd.Lock.Redis.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Leistd.Lock.Tests.Redis;

/// <summary><c>AddRedisDistributedLock</c> 以不同参数重复调用时的既定行为：连接串以首次为准，配置节都绑定、校验键名取首次的节。</summary>
/// <remarks>
/// 连接串指向不可达端口并关闭 <c>abortConnect</c>，解析多路复用器不需要真实 Redis，只读它记下的配置串。
/// </remarks>
public sealed class RedisLockRepeatedRegistrationTests
{
    private const string FirstConnection = "127.0.0.1:1,abortConnect=false,connectTimeout=100";
    private const string SecondConnection = "127.0.0.1:2,abortConnect=false,connectTimeout=100";

    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services;
    }

    [Fact]
    public void A_different_connection_string_on_a_later_call_is_ignored()
    {
        var services = Services([]);

        services.AddRedisDistributedLock(FirstConnection).AddRedisDistributedLock(SecondConnection);

        Assert.Single(services, d => d.ServiceType == typeof(IConnectionMultiplexer));
        using var provider = services.BuildServiceProvider();
        var configuration = provider.GetRequiredService<IConnectionMultiplexer>().Configuration;
        Assert.Contains("127.0.0.1:1", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:2", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_section_is_bound_as_well_and_overrides_the_same_keys()
    {
        var services = Services(new()
        {
            ["First:KeyPrefix"] = "first:",
            ["First:Expiry"] = "00:00:40",
            ["Second:KeyPrefix"] = "second:",
        });

        services.AddRedisDistributedLock(FirstConnection, configSectionPath: "First")
            .AddRedisDistributedLock(FirstConnection, configSectionPath: "Second");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisLockOptions>>().Value;
        Assert.Equal("second:", options.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(40), options.Expiry);
    }

    [Fact]
    public void Validation_names_the_key_under_the_first_section()
    {
        var services = Services(new() { ["Second:Expiry"] = "00:00:00" });

        services.AddRedisDistributedLock(FirstConnection, configSectionPath: "First")
            .AddRedisDistributedLock(FirstConnection, configSectionPath: "Second");

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<RedisLockOptions>));
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RedisLockOptions>>().Value);
        Assert.StartsWith("First:Expiry", Assert.Single(exception.Failures), StringComparison.Ordinal);
    }
}
