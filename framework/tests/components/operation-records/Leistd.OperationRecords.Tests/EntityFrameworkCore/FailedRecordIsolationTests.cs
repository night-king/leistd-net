using Leistd.Data.Connections;
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Stores;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary>存储按记录结果决定事务边界：成功跟随调用方，失败在记录所在的层里独立提交。</summary>
/// <remarks>
/// 宿主库与租户库是两个独立的 SQLite 内存库，连接按当前租户路由——与分库租户的真实形态一致，
/// "写进了哪个库"因此可以直接断言。
/// </remarks>
public sealed class FailedRecordIsolationTests : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _hostConnectionString = $"Data Source=host-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string _tenantConnectionString = $"Data Source=tenant-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _hostAnchor = default!;
    private SqliteConnection _tenantAnchor = default!;
    private ServiceProvider _services = default!;

    public async Task InitializeAsync()
    {
        // 共享缓存的内存库在最后一个连接关闭时消失，锚连接让它们活到用例结束
        _hostAnchor = new SqliteConnection(_hostConnectionString);
        _tenantAnchor = new SqliteConnection(_tenantConnectionString);
        await _hostAnchor.OpenAsync();
        await _tenantAnchor.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMultiTenancyCore();
        services.AddSingleton<IConnectionStringResolver>(provider => new TenantRoutingResolver(
            provider.GetRequiredService<ICurrentTenantAccessor>(), _hostConnectionString, _tenantConnectionString));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<TestDbContext>((_, options) =>
            options.UseSqlite(DbContextCreationContext.Current?.ConnectionString ?? _hostConnectionString));
        services.AddOperationRecordsEfCore<TestDbContext>();
        _services = services.BuildServiceProvider();

        foreach (var connectionString in new[] { _hostConnectionString, _tenantConnectionString })
        {
            await using var db = new TestDbContext(
                new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options);
            await db.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _hostAnchor.DisposeAsync();
        await _tenantAnchor.DisposeAsync();
    }

    /// <summary>失败记录扛过调用方的回滚。</summary>
    /// <remarks>
    /// 业务在工作单元内记完失败紧接着抛出、整体回滚，是被拒路径最常见的形态。
    /// 写入若落在调用方的工作单元里，就会随回滚一起消失——"谁在反复做他不被允许的事"就这样丢了。
    /// </remarks>
    [Fact]
    public async Task A_failed_record_survives_the_callers_rollback()
    {
        using (_services.GetRequiredService<ICurrentTenant>().Change(TenantId))
        using (_services.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await Store.InsertAsync(Record(OperationRecordOutcome.Failed, TenantId));
            // 不 Complete：离开作用域即回滚
        }

        Assert.Equal(1, await CountAsync(_tenantConnectionString));
    }

    /// <summary>成功记录与调用方同生共死：业务回滚，记录一并消失。</summary>
    [Fact]
    public async Task A_succeeded_record_rolls_back_with_the_caller()
    {
        using (_services.GetRequiredService<ICurrentTenant>().Change(TenantId))
        using (_services.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await Store.InsertAsync(Record(OperationRecordOutcome.Succeeded, TenantId));
        }

        Assert.Equal(0, await CountAsync(_tenantConnectionString));
    }

    /// <summary>归属宿主层的失败记录写进宿主库，即便此刻处在分库租户的上下文里。</summary>
    /// <remarks>只改 <c>TenantId</c> 不切连接的话，这一行会带着宿主归属落进租户库，两边都查不到。</remarks>
    [Fact]
    public async Task A_host_layer_failed_record_lands_in_the_host_database_from_a_tenant_context()
    {
        using (_services.GetRequiredService<ICurrentTenant>().Change(TenantId))
        using (_services.GetRequiredService<IUnitOfWorkManager>().Begin())
        {
            await Store.InsertAsync(Record(OperationRecordOutcome.Failed, tenantId: null));
        }

        Assert.Equal(1, await CountAsync(_hostConnectionString));
        Assert.Equal(0, await CountAsync(_tenantConnectionString));
    }

    /// <summary>成功记录的归属必须与当前租户上下文一致，否则拒绝写入，不静默写进别人的库。</summary>
    [Fact]
    public async Task A_succeeded_record_for_another_layer_is_rejected()
    {
        using (_services.GetRequiredService<ICurrentTenant>().Change(TenantId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Store.InsertAsync(Record(OperationRecordOutcome.Succeeded, tenantId: null)));
        }
    }

    private IOperationRecordWriter Store => _services.GetRequiredService<IOperationRecordWriter>();

    private static OperationRecordInfo Record(OperationRecordOutcome outcome, Guid? tenantId) => new()
    {
        TenantId = tenantId,
        ActorTenantId = TenantId,
        Action = "tenant.created",
        TargetId = OperationTarget.NoTargetId,
        AuthorizationBasis = "App.Tenants.Create",
        Outcome = outcome,
        Visibility = OperationVisibility.Host,
        CreationTime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)
    };

    private static async Task<int> CountAsync(string connectionString)
    {
        await using var db = new TestDbContext(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options);
        return await db.Set<OperationRecord>().IgnoreQueryFilters().CountAsync();
    }

    // 宿主上下文连宿主库，租户上下文连租户库
    private sealed class TenantRoutingResolver(
        ICurrentTenantAccessor accessor,
        string hostConnectionString,
        string tenantConnectionString) : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
            => Task.FromResult(accessor.Current?.TenantId is null ? hostConnectionString : tenantConnectionString);
    }
}
