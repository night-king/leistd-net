using Leistd.TestBase.Doubles;
using Leistd.Data.Filters;
using Leistd.Ddd.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.Auditing.Abstractions;
using Leistd.Ddd.Infrastructure.Tests.TestDoubles;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>租户全局过滤器与软删除过滤器的组合行为（Sqlite 真实翻译，InMemory 会静默放行隔离缺口）。</summary>
public class MultiTenantFilterTests : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private ServiceProvider _provider = default!;
    private SqliteConnection _connection = default!;
    private TestFilterDbContext _db = default!;
    private ICurrentTenant _currentTenant = default!;
    private IDataFilter _dataFilter = default!;
    private Guid _tenantASoftDeletedId;
    private Guid _tenantAOrderId;
    private Guid _hostOrderId;

    public async Task InitializeAsync()
    {
        _provider = FilterTestServices.Create();
        _currentTenant = _provider.GetRequiredService<ICurrentTenant>();
        _dataFilter = _provider.GetRequiredService<IDataFilter>();

        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestFilterDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TestFilterDbContext(options, _provider);
        await _db.Database.EnsureCreatedAsync();

        // 种子：宿主 1 行、租户 A 2 行（其中 1 行软删）、租户 B 1 行
        var hostOrder = new TestOrder { Title = "host-order" };
        var tenantAOrder = new TestOrder { Title = "a-order", TenantId = TenantA };
        var tenantADeleted = new TestOrder { Title = "a-deleted", TenantId = TenantA };
        tenantADeleted.MarkDeleted();
        var tenantBOrder = new TestOrder { Title = "b-order", TenantId = TenantB };

        _hostOrderId = hostOrder.Id;
        _tenantAOrderId = tenantAOrder.Id;
        _tenantASoftDeletedId = tenantADeleted.Id;

        _db.AddRange(hostOrder, tenantAOrder, tenantADeleted, tenantBOrder);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Tenant_context_sees_only_its_own_live_rows()
    {
        using (_currentTenant.Change(TenantA))
        {
            var titles = await _db.Orders.Select(o => o.Title).ToListAsync();
            Assert.Equal(["a-order"], titles);
        }

        using (_currentTenant.Change(TenantB))
        {
            var titles = await _db.Orders.Select(o => o.Title).ToListAsync();
            Assert.Equal(["b-order"], titles);
        }
    }

    [Fact]
    public async Task Host_context_sees_only_host_rows()
    {
        var titles = await _db.Orders.Select(o => o.Title).ToListAsync();
        Assert.Equal(["host-order"], titles);

        using (_currentTenant.Change(TenantA))
        using (_currentTenant.Change(null))
        {
            // 显式 Change(null) 与从未设置等价：宿主视角
            Assert.Equal(["host-order"], await _db.Orders.Select(o => o.Title).ToListAsync());
        }
    }

    [Fact]
    public async Task Soft_delete_and_tenant_filters_compose_independently()
    {
        using (_currentTenant.Change(TenantA))
        {
            // 双过滤器叠加：软删行不可见
            Assert.Equal(1, await _db.Orders.CountAsync());

            using (_dataFilter.Disable<ISoftDelete>())
            {
                // 只放开软删过滤器：看见本租户的软删行，但看不见其他租户
                var titles = (await _db.Orders.Select(o => o.Title).ToListAsync()).Order().ToList();
                Assert.Equal(["a-deleted", "a-order"], titles);
            }

            // 作用域结束自动恢复
            Assert.Equal(1, await _db.Orders.CountAsync());
        }
    }

    [Fact]
    public async Task Disabling_tenant_filter_reveals_all_tenants()
    {
        using (_currentTenant.Change(TenantA))
        using (_dataFilter.Disable<IMultiTenant>())
        {
            // 全量视角（跨租户系统操作的显式姿势）：所有租户 + 宿主的未删行
            Assert.Equal(3, await _db.Orders.CountAsync());
        }
    }

    [Fact]
    public async Task Entities_entering_the_model_after_derived_configuration_are_also_filtered()
    {
        // TestLedgerEntry 没有 DbSet 声明，只经派生类的 ApplyConfiguration 进入模型。
        // 过滤器若在派生配置之前套用，没有 DbSet 声明的实体（如授权版本表）会对所有租户可见。
        _db.Add(new TestLedgerEntry { Memo = "a-entry", TenantId = TenantA });
        _db.Add(new TestLedgerEntry { Memo = "b-entry", TenantId = TenantB });
        _db.Add(new TestLedgerEntry { Memo = "host-entry" });
        await _db.SaveChangesAsync();

        using (_currentTenant.Change(TenantA))
        {
            Assert.Equal(["a-entry"], await _db.Set<TestLedgerEntry>().Select(x => x.Memo).ToListAsync());
        }

        using (_currentTenant.Change(TenantB))
        {
            Assert.Equal(["b-entry"], await _db.Set<TestLedgerEntry>().Select(x => x.Memo).ToListAsync());
        }

        // 宿主视角只见宿主行
        Assert.Equal(["host-entry"], await _db.Set<TestLedgerEntry>().Select(x => x.Memo).ToListAsync());
    }

    [Fact]
    public async Task Repository_get_by_id_respects_tenant_isolation()
    {
        var repository = new EfCoreRepository<TestFilterDbContext, TestOrder, Guid>(
            new FixedDbContextProvider<TestFilterDbContext>(_db),
            new NullUnitOfWorkManager());

        using (_currentTenant.Change(TenantB))
        {
            // 跨租户按 Id 取数：不可见即 null；按主键走 FindAsync 会绕过过滤器直接命中
            Assert.Null(await repository.GetByIdAsync(_tenantAOrderId));
            Assert.Null(await repository.GetByIdAsync(_hostOrderId));
        }

        using (_currentTenant.Change(TenantA))
        {
            Assert.NotNull(await repository.GetByIdAsync(_tenantAOrderId));
        }
    }

    [Fact]
    public async Task Repository_get_by_id_respects_soft_delete()
    {
        var repository = new EfCoreRepository<TestFilterDbContext, TestOrder, Guid>(
            new FixedDbContextProvider<TestFilterDbContext>(_db),
            new NullUnitOfWorkManager());

        using (_currentTenant.Change(TenantA))
        {
            // 软删除同样生效：按主键走 FindAsync 能取出已删行
            Assert.Null(await repository.GetByIdAsync(_tenantASoftDeletedId));
        }
    }
}
