using Leistd.Data.Paging;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Management.Dtos;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary><c>AddTenantManagement()</c> 的注册契约：不带 EF 也能装，生命周期是 Transient，宿主可替换。</summary>
/// <remarks>
/// 这两个用例只依赖契约与工作单元，因此换存储实现（Dapper、远端控制面）时应当照样可用——
/// 所以按"只引用 Management + 自带存储"的形态钉住。反过来，只读控制库的宿主（租户连接解析、迁移作业）
/// 只调 EF 存储入口，不应被带上用例。
/// </remarks>
public sealed class TenantManagementRegistrationTests
{
    [Fact]
    public void Custom_stores_can_register_the_use_cases_without_the_ef_package()
    {
        using var provider = BuildWithCustomStores().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ITenantManagementService>());
        Assert.NotNull(provider.GetRequiredService<ITenantConnectionManagementService>());
    }

    /// <summary>EF 存储入口只注册存储：管理用例由宿主另行 <c>AddTenantManagement()</c>。</summary>
    [Fact]
    public void The_ef_store_entry_does_not_register_the_use_cases()
    {
        var services = new ServiceCollection().AddMultiTenancyEfCore<Microsoft.EntityFrameworkCore.DbContext>();

        Assert.DoesNotContain(services, service => service.ServiceType == typeof(ITenantManagementService));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(ITenantConnectionManagementService));
    }

    /// <summary>用例按请求解析：它们跨工作单元编排，单例会把上下文钉死在第一次解析的那个作用域。</summary>
    [Theory]
    [InlineData(typeof(ITenantManagementService))]
    [InlineData(typeof(ITenantConnectionManagementService))]
    public void The_use_cases_are_transient(Type contract)
    {
        var services = BuildWithCustomStores();

        var descriptor = Assert.Single(services, service => service.ServiceType == contract);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    /// <summary>重复调用不叠加注册：宿主两条路径都调到时，解析出的实现只有一个。</summary>
    [Fact]
    public void Calling_it_twice_is_idempotent()
    {
        var services = BuildWithCustomStores();
        services.AddTenantManagement();

        Assert.Single(services, service => service.ServiceType == typeof(ITenantManagementService));
    }

    /// <summary>宿主先注册自己的实现时不被覆盖——TryAdd 就是这个替换口的证据。</summary>
    [Fact]
    public void A_host_registration_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantManagementService, FakeTenantManagementService>();
        services.AddTenantManagement();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<FakeTenantManagementService>(provider.GetRequiredService<ITenantManagementService>());
    }

    // 只引用 Core：存储契约由"宿主"自己提供，这里用测试替身代表任何非 EF 实现
    private static ServiceCollection BuildWithCustomStores()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddMultiTenancyCore();
        services.AddSingleton<ITenantStore>(new FakeTenantStore());
        services.AddSingleton<ITenantManager>(new FakeTenantManager());
        services.AddSingleton<ITenantConnectionConfigurationStore>(new FakeConnectionStore());
        services.AddSingleton<ITenantConnectionConfigurationManager>(new FakeConnectionManager());
        services.AddSingleton<ITenantConnectionDirectory>(new FakeConnectionDirectory());
        services.AddSingleton<ITenantDatabaseDirectory>(new FakeDatabaseDirectory());
        services.AddTenantManagement();
        return services;
    }

    // 只为解析：用例的构造函数要拿到这些契约，但注册契约的用例不会调用它们
    private sealed class FakeTenantStore : ITenantStore
    {
        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantConfiguration?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeTenantManager : ITenantManager
    {
        public Task<TenantConfiguration> CreateAsync(string name, string? displayName, bool isActive, string? description = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantConfiguration> UpdateAsync(Guid id, string name, string? displayName, string? description = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantConfiguration> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PagedResult<TenantConfiguration>> GetPagedAsync(string? keyword, bool? isActive, PageRequest page, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeConnectionStore : ITenantConnectionConfigurationStore
    {
        public Task<TenantConnectionLookupResult?> FindAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantMigrationConnectionListResult> GetListAsync(string name, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeConnectionManager : ITenantConnectionConfigurationManager
    {
        public Task<TenantConnectionConfiguration> SetAsync(Guid tenantId, string name, string connectionString, long? expectedVersion, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(Guid tenantId, string name, long expectedVersion, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeConnectionDirectory : ITenantConnectionDirectory
    {
        public Task<IReadOnlyList<TenantConnectionEntry>?> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeDatabaseDirectory : ITenantDatabaseDirectory
    {
        public Task<TenantDatabaseListResult> GetDatabasesAsync(string name, bool activeOnly, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeTenantManagementService : ITenantManagementService
    {
        public Task<PagedResult<TenantOutputDto>> GetPagedAsync(GetTenantPagedInputDto input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantOutputDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantOutputDto> CreateAsync(CreateTenantInputDto input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantOutputDto> UpdateAsync(Guid id, UpdateTenantInputDto input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TenantOutputDto> SetActivationAsync(Guid id, UpdateTenantActivationInputDto input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
