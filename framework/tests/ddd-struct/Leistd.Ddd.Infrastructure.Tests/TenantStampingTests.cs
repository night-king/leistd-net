using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.Ddd.Infrastructure.Tests.TestDoubles;

namespace Leistd.Ddd.Infrastructure.Tests;

/// <summary>
/// 租户落值：新增实体在进入变更跟踪时自动填充当前租户，显式赋值不覆盖，
/// 宿主上下文保持 null。落值由 <c>BaseDbContext</c> 完成，不挂任何拦截器。
/// </summary>
public class TenantStampingTests : IAsyncLifetime
{
    private ServiceProvider _provider = default!;
    private SqliteConnection _connection = default!;
    private TestFilterDbContext _db = default!;
    private ICurrentTenant _currentTenant = default!;

    public async Task InitializeAsync()
    {
        _provider = FilterTestServices.Create();
        _currentTenant = _provider.GetRequiredService<ICurrentTenant>();

        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        // 刻意不挂任何落值拦截器：落值是 BaseDbContext 的职责
        var options = new DbContextOptionsBuilder<TestFilterDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TestFilterDbContext(options, _provider);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
        await _provider.DisposeAsync();
    }

    /// <summary>在租户作用域内新增、退出作用域后才提交，仍须归属该租户。</summary>
    /// <remarks>
    /// 仓储在工作单元内不立即保存（由 UoW 统一提交），因此新增与保存之间可能跨越
    /// <c>Change</c> 的边界。若在保存时刻取当前租户，这条数据会静默落成宿主行——
    /// 该租户自己看不见（过滤器要求 TenantId 等于当前租户），宿主管理员却看得见，
    /// 且没有任何报错。落值时机必须是"进入跟踪"。
    /// </remarks>
    [Fact]
    public async Task Entity_added_inside_a_tenant_scope_keeps_that_tenant_when_saved_later()
    {
        var tenantId = Guid.NewGuid();
        var order = new TestOrder { Title = "in-scope-add" };

        using (_currentTenant.Change(tenantId))
        {
            _db.Orders.Add(order);      // 仓储在 UoW 内不保存，等同于此
        }

        await _db.SaveChangesAsync();   // 作用域已退出，UoW 在这里统一提交

        var stamped = await _db.Orders.IgnoreQueryFilters()
            .Where(o => o.Title == "in-scope-add")
            .Select(o => o.TenantId)
            .SingleAsync();

        Assert.Equal(tenantId, stamped);
    }

    [Fact]
    public async Task Added_entity_gets_current_tenant_id()
    {
        var tenantId = Guid.NewGuid();
        var order = new TestOrder { Title = "stamped" };

        using (_currentTenant.Change(tenantId))
        {
            _db.Add(order);
            await _db.SaveChangesAsync();
        }

        Assert.Equal(tenantId, order.TenantId);
    }

    [Fact]
    public async Task Explicitly_assigned_tenant_id_is_not_overwritten()
    {
        var explicitTenant = Guid.NewGuid();
        var order = new TestOrder { Title = "explicit", TenantId = explicitTenant };

        using (_currentTenant.Change(Guid.NewGuid()))
        {
            _db.Add(order);
            await _db.SaveChangesAsync();
        }

        Assert.Equal(explicitTenant, order.TenantId);
    }

    [Fact]
    public async Task Host_context_leaves_tenant_id_null()
    {
        var order = new TestOrder { Title = "host" };

        _db.Add(order);
        await _db.SaveChangesAsync();

        Assert.Null(order.TenantId);
    }

    /// <summary>宿主上下文新增、却在租户作用域内提交：必须保持宿主行。</summary>
    /// <remarks>
    /// 落值只能发生在 ②：若在 ③（保存时）再按当前租户落一次值，正常路径上无害，
    /// 但在本场景里方向相反地有害——
    /// 它会把合法的宿主数据（<c>TenantId</c> 本就该是 null）盖成租户数据。
    /// 同一条边界漂移，只是方向反过来。
    /// </remarks>
    [Fact]
    public async Task Entity_added_in_host_context_stays_host_when_committed_inside_a_tenant_scope()
    {
        var order = new TestOrder { Title = "host-add-tenant-commit" };

        _db.Orders.Add(order);          // 宿主上下文，无租户

        using (_currentTenant.Change(Guid.NewGuid()))
        {
            await _db.SaveChangesAsync();   // 提交却发生在租户作用域内
        }

        var stamped = await _db.Orders.IgnoreQueryFilters()
            .Where(o => o.Title == "host-add-tenant-commit")
            .Select(o => o.TenantId)
            .SingleAsync();

        Assert.Null(stamped);
    }

    [Fact]
    public async Task Modified_entity_is_not_restamped()
    {
        var tenantId = Guid.NewGuid();
        var order = new TestOrder { Title = "created" };

        using (_currentTenant.Change(tenantId))
        {
            _db.Add(order);
            await _db.SaveChangesAsync();
        }

        // 在另一个租户上下文里修改：TenantId 不被改写（落值只发生在 Added）
        using (_currentTenant.Change(Guid.NewGuid()))
        using (_provider.GetRequiredService<Leistd.Data.Filters.IDataFilter>().Disable<IMultiTenant>())
        {
            order.Title = "updated";
            await _db.SaveChangesAsync();
        }

        Assert.Equal(tenantId, order.TenantId);
    }
}
