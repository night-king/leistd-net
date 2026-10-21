using Leistd.RealTime.Subscriptions;
using Leistd.TestBase.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.RealTime.Tests;

/// <summary>订阅授权器的注册面：两个内置选择都是单例、按 TryAdd 登记，宿主或先做出的选择不被后来者盖掉。</summary>
/// <remarks>
/// 授权器决定谁能订阅什么。被后一行静默换掉时，表现是越权订阅或全部被拒，而不是启动报错。
/// </remarks>
public sealed class SubscriptionAuthorizerRegistrationTests
{
    [Fact]
    public void Allow_all_registers_a_singleton_authorizer()
    {
        var services = new ServiceCollection();

        services.AddAllowAllRealTimeSubscriptions();

        services.AssertSingle<IRealTimeSubscriptionAuthorizer>(ServiceLifetime.Singleton);
        services.AssertImplementedBy<IRealTimeSubscriptionAuthorizer, AllowAllRealTimeSubscriptionAuthorizer>();
    }

    [Fact]
    public void Prefix_registers_a_singleton_authorizer()
    {
        var services = new ServiceCollection();

        services.AddPrefixRealTimeSubscriptions("orders:");

        services.AssertSingle<IRealTimeSubscriptionAuthorizer>(ServiceLifetime.Singleton);
    }

    [Fact]
    public void Allow_all_registration_is_idempotent()
        => ServiceCollectionAssertions.AssertIdempotent(services => services.AddAllowAllRealTimeSubscriptions());

    // 前缀授权器是实例注册，重复调用同样只留一条
    [Fact]
    public void Prefix_registration_twice_keeps_one_authorizer()
    {
        var services = new ServiceCollection();

        services.AddPrefixRealTimeSubscriptions("orders:").AddPrefixRealTimeSubscriptions("orders:");

        services.AssertSingle<IRealTimeSubscriptionAuthorizer>(ServiceLifetime.Singleton);
    }

    // 宿主自己的授权器先登记时，内置选择不得盖掉它
    [Fact]
    public void A_host_authorizer_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRealTimeSubscriptionAuthorizer, DenyAllAuthorizer>();

        services.AddAllowAllRealTimeSubscriptions();
        services.AddPrefixRealTimeSubscriptions("orders:");

        services.AssertSingle<IRealTimeSubscriptionAuthorizer>(ServiceLifetime.Scoped);
        services.AssertImplementedBy<IRealTimeSubscriptionAuthorizer, DenyAllAuthorizer>();
    }

    // 先选了前缀限制、后又调"全部放行"时保留先做的限制：越权方向的覆盖不能静默发生
    [Fact]
    public async Task A_later_allow_all_does_not_widen_an_earlier_prefix_choice()
    {
        using var provider = new ServiceCollection()
            .AddPrefixRealTimeSubscriptions("orders:")
            .AddAllowAllRealTimeSubscriptions()
            .BuildServiceProvider();

        var allowed = await provider.GetRequiredService<IRealTimeSubscriptionAuthorizer>()
            .AuthorizeAsync(new RealTimeSubscriptionContext("payroll:1", "user-1"));

        Assert.False(allowed);
    }

    private sealed class DenyAllAuthorizer : IRealTimeSubscriptionAuthorizer
    {
        public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
