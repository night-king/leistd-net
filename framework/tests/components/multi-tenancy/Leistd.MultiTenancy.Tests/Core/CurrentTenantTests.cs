using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Context;

namespace Leistd.MultiTenancy.Tests.Core;

public class CurrentTenantTests
{
    private static ICurrentTenant CreateCurrentTenant() =>
        new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance);

    // 切换租户即打开日志作用域：各切换入口的日志都能按同一个键过滤，切回宿主写 null 覆盖外层租户
    [Fact]
    public void Change_puts_the_tenant_into_the_log_scope()
    {
        var logger = new FakeLogger<CurrentTenant>();
        var currentTenant = new CurrentTenant(AsyncLocalCurrentTenantAccessor.Instance, logger);
        var tenantId = Guid.NewGuid();

        using (currentTenant.Change(tenantId))
        {
            logger.LogInformation("inside tenant");
            using (currentTenant.Change(null))
            {
                logger.LogInformation("back to host");
            }
        }

        logger.LogInformation("outside");

        Assert.Equal(tenantId, ScopedTenant(logger.Collector.GetSnapshot()[0]));
        Assert.Null(ScopedTenant(logger.Collector.GetSnapshot()[1]));
        Assert.Empty(logger.Collector.GetSnapshot()[2].Scopes);
    }

    // 最内层作用域里的租户键
    private static object? ScopedTenant(FakeLogRecord record)
        => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .SelectMany(scope => scope)
            .Last(pair => pair.Key == TenantLogKeys.TenantId).Value;

    [Fact]
    public void Change_nests_and_restores_parent_on_dispose()
    {
        var currentTenant = CreateCurrentTenant();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        Assert.Null(currentTenant.Id);
        Assert.False(currentTenant.IsAvailable);

        using (currentTenant.Change(t1, "acme"))
        {
            Assert.Equal(t1, currentTenant.Id);
            Assert.Equal("acme", currentTenant.Name);
            Assert.True(currentTenant.IsAvailable);

            using (currentTenant.Change(t2))
            {
                Assert.Equal(t2, currentTenant.Id);
            }

            // 内层释放后恢复外层租户
            Assert.Equal(t1, currentTenant.Id);

            using (currentTenant.Change(null))
            {
                // 显式切换到宿主视角
                Assert.Null(currentTenant.Id);
                Assert.False(currentTenant.IsAvailable);
            }

            Assert.Equal(t1, currentTenant.Id);
        }

        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public void Dispose_twice_restores_only_once()
    {
        var currentTenant = CreateCurrentTenant();
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (currentTenant.Change(outer))
        {
            var handle = currentTenant.Change(inner);
            handle.Dispose();
            Assert.Equal(outer, currentTenant.Id);

            // 二次释放不应把上下文再改一次
            using (currentTenant.Change(Guid.NewGuid()))
            {
                handle.Dispose();
                Assert.NotEqual(outer, currentTenant.Id);
            }
        }
    }

    [Fact]
    public async Task Tenant_context_flows_across_await_and_tasks()
    {
        var currentTenant = CreateCurrentTenant();
        var tenantId = Guid.NewGuid();

        using (currentTenant.Change(tenantId))
        {
            await Task.Delay(1);
            Assert.Equal(tenantId, currentTenant.Id);

            // AsyncLocal 挂在 ExecutionContext 上，Task.Run 一样看得见
            var observed = await Task.Run(() => CreateCurrentTenant().Id);
            Assert.Equal(tenantId, observed);
        }

        Assert.Null(currentTenant.Id);
    }

    [Fact]
    public void Tenant_context_flows_into_new_di_scope()
    {
        // 本地事件总线为每次发布创建新 DI Scope，租户上下文必须穿透 Scope 边界
        var services = new ServiceCollection();
        services.AddMultiTenancyCore();
        using var provider = services.BuildServiceProvider();

        var currentTenant = provider.GetRequiredService<ICurrentTenant>();
        var tenantId = Guid.NewGuid();

        using (currentTenant.Change(tenantId))
        {
            using var scope = provider.CreateScope();
            var scopedTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
            Assert.Equal(tenantId, scopedTenant.Id);
        }
    }
}
