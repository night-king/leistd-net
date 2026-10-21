using Leistd.MultiTenancy.ConnectionStrings;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>迁移目标：按连接名解析，登记过连接的租户全都要出现；取不出连接的租户单列为失败，既不挡住其他库，也不被静默略过。</summary>
/// <remarks>
/// 本地与远端共用同一个提供器，差别只在注入的是哪种 <see cref="ITenantConnectionConfigurationStore"/>
/// 实现（EF 读控制库 / 宿主的 HTTP 实现）。这里用桩存储覆盖提供器自身的形态转换与指纹。
/// </remarks>
public class MigrationTargetTests
{
    private const string AcmeConnection = "Data Source=acme;Password=acme-secret";

    private static ITenantMigrationTargetProvider Create(params TenantMigrationConnection[] connections)
        => Create(connections, []);

    private static ITenantMigrationTargetProvider Create(
        TenantMigrationConnection[] connections, TenantDatabaseFailure[] failures)
    {
        var store = new ScriptedRemoteSource();
        store.Migration.AddRange(connections);
        store.MigrationFailures.AddRange(failures);
        return new TenantMigrationTargetProvider(store);
    }

    [Fact]
    public async Task Every_registered_connection_becomes_a_target()
    {
        var tenantId = Guid.NewGuid();

        var target = Assert.Single(
            (await Create(new TenantMigrationConnection(tenantId, "crm", AcmeConnection)).GetDedicatedTargetsAsync("Crm")).Targets);

        Assert.Equal((tenantId, AcmeConnection), (target.TenantId, target.ConnectionString));
        // 指纹给日志用，文本形态不能带出连接串
        Assert.DoesNotContain("acme-secret", target.ToString());
    }

    // 名字带在连接上是为了能回答"这个租户为什么迁到了这个库"，但它不该出现在目标的文本形态里之外的地方
    [Fact]
    public async Task The_resolved_name_is_carried_on_the_connection_without_leaking_the_string()
    {
        var connection = new TenantMigrationConnection(Guid.NewGuid(), "default", AcmeConnection);

        Assert.Equal("default", connection.Name);
        Assert.DoesNotContain("acme-secret", connection.ToString());
    }

    [Fact]
    public async Task Tenants_without_any_registration_simply_do_not_appear()
    {
        var set = await Create().GetDedicatedTargetsAsync("Crm");

        Assert.Empty(set.Targets);
        Assert.Empty(set.FailedTenants);
    }

    [Fact]
    public async Task Several_tenants_each_become_their_own_target()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var targets = (await Create(
            new TenantMigrationConnection(first, "crm", "Data Source=one"),
            new TenantMigrationConnection(second, "default", "Data Source=two")).GetDedicatedTargetsAsync("Crm")).Targets;

        Assert.Equal(2, targets.Count);
        Assert.Equal([first, second], targets.Select(x => x.TenantId));
    }

    /// <summary>共用同一个库的租户只出一条目标，代表租户取标识最小者。</summary>
    /// <remarks>
    /// 去重在提供器里做，迁移作业与运行时逐库作业共用这一份清单；
    /// 由调用方各自去重时，两边的规则迟早会分叉。
    /// </remarks>
    [Fact]
    public async Task Tenants_sharing_a_database_yield_one_target()
    {
        var lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higher = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var target = Assert.Single((await Create(
            new TenantMigrationConnection(higher, "default", AcmeConnection),
            new TenantMigrationConnection(lower, "default", AcmeConnection)).GetDedicatedTargetsAsync("Crm")).Targets);

        Assert.Equal(lower, target.TenantId);
        Assert.DoesNotContain("acme-secret", target.Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取不出连接的租户原样随清单交出，健康租户照常成为目标。</summary>
    /// <remarks>提供器若吞掉失败，迁移作业会以为一切正常，那些库就停在旧结构上。</remarks>
    [Fact]
    public async Task Failed_tenants_travel_with_the_targets_of_healthy_tenants()
    {
        var healthy = Guid.NewGuid();
        var broken = new TenantDatabaseFailure(Guid.NewGuid(), "no connection named 'crm'");

        var set = await Create(
            [new TenantMigrationConnection(healthy, "crm", AcmeConnection)],
            [broken]).GetDedicatedTargetsAsync("Crm");

        Assert.Equal(healthy, Assert.Single(set.Targets).TenantId);
        Assert.Equal(broken, Assert.Single(set.FailedTenants));
    }
}
