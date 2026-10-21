using Microsoft.Extensions.DependencyInjection;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Extensions;
using Xunit;

namespace Leistd.MultiTenancy.Tests.Core;

// 缓存键、分布式锁键这类租户外部标识的命名空间划分。
public class CurrentTenantKeyTests
{
    [Fact]
    public void The_same_key_is_distinct_between_tenants_and_the_host()
    {
        var tenant = Build();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var hostKey = tenant.ScopeKey("catalog:root");

        using (tenant.Change(a))
        {
            Assert.Equal($"{a:N}:catalog:root", tenant.ScopeKey("catalog:root"));
        }

        using (tenant.Change(b))
        {
            Assert.NotEqual(hostKey, tenant.ScopeKey("catalog:root"));
            Assert.NotEqual($"{a:N}:catalog:root", tenant.ScopeKey("catalog:root"));
        }

        Assert.Equal("host:catalog:root", hostKey);
    }

    [Fact]
    public void An_empty_key_is_rejected()
    {
        var tenant = Build();

        Assert.Throws<ArgumentException>(() => tenant.ScopeKey("  "));
    }

    private static ICurrentTenant Build()
        => new ServiceCollection()
            .AddMultiTenancyCore()
            .BuildServiceProvider()
            .GetRequiredService<ICurrentTenant>();
}
