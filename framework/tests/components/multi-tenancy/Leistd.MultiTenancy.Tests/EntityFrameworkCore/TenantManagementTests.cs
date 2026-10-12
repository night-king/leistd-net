using Leistd.TestBase.Doubles;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Leistd.Data.Paging;
using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Errors;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.EntityFrameworkCore.Managers;
using Leistd.MultiTenancy.Exceptions;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Events;
using Leistd.MultiTenancy.Management.Provisioning;

namespace Leistd.MultiTenancy.Tests.EntityFrameworkCore;

/// <summary>
/// 租户管理用例：先停用登记、同一控制面工作单元登记连接，再在新租户里开通，最后启用；
/// 失败按"清开通数据 → 删连接 → 删租户"补偿，数据库原因翻成带码的 400。
/// </summary>
public sealed class TenantManagementTests : IAsyncLifetime
{
    private readonly RecordingProvisioner _provisioner = new();
    private readonly RecordingEventBus _events = new();
    private readonly RejectingGuard _guard = new();
    private ServiceProvider _provider = default!;
    private SqliteConnection _connection = default!;
    private ITenantManagementService _service = default!;

    // 在用例读完快照之后、调用管理器删除之前插入的动作，用来确定性地制造并发交错
    private Func<Guid, Task>? _beforeDelete;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        // 错误翻译是宿主的技术适配（码表随数据库而异）：组件默认不翻译，这里登记一个假的，
        // 验的是"开通失败时编排会把驱动异常交给描述器、并把它的结果抛出去"这条接缝
        services.AddSingleton<ITenantDatabaseErrorDescriber>(new FakeErrorDescriber());
        services.AddDbContext<TestDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMultiTenancyEfCore<TestDbContext>();
        services.AddTenantManagement();
        services.AddSingleton<ITenantProvisioner>(_provisioner);
        services.AddSingleton<ITenantActivationGuard>(_guard);
        services.AddSingleton<ILocalEventBus>(_events);
        services.Replace(ServiceDescriptor.Transient<ITenantManager>(sp => new InterleavingTenantManager(
            ActivatorUtilities.CreateInstance<EfCoreTenantManager<TestDbContext>>(sp), () => _beforeDelete)));

        _provider = services.BuildServiceProvider();
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        }

        _service = _provider.GetRequiredService<ITenantManagementService>();
        _provisioner.Services = _provider;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_created_tenant_is_provisioned_inside_its_own_context_then_activated()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme", DisplayName = "Acme" });

        Assert.True(tenant.IsActive);
        Assert.Equal(tenant.Id, _provisioner.ProvisionedInTenant);
        var created = Assert.IsType<TenantChangedEvent>(Assert.Single(_events.Published));
        Assert.Equal((tenant.Id, "Acme", TenantChangeKind.Created), (created.TenantId, created.DisplayName, created.Change));
    }

    // 分库在开通之前定案：开通时解析到的已经是专属库
    [Fact]
    public async Task A_connection_string_is_registered_under_the_default_name_before_provisioning()
    {
        _provisioner.OnProvision = async (services, tenant) =>
            _provisioner.ConnectionsSeenDuringProvisioning =
                await services.GetRequiredService<ITenantConnectionDirectory>().ListAsync(tenant.Id);

        var tenant = await _service.CreateAsync(Create("acme", ("default", "Host=acme;Database=acme")));

        var registered = Assert.Single(_provisioner.ConnectionsSeenDuringProvisioning!);
        Assert.Equal("default", registered.Name);
        Assert.Equal("default", Assert.Single((await Connections().GetListAsync(tenant.Id))).Name);
    }

    /// <summary>多服务部署一次登记多条：开通钩子第一次执行时看到的就是完整集合。</summary>
    /// <remarks>
    /// 建完租户再逐条调连接管理接口会留下"租户已启用、某条连接还没登记"的中间状态，
    /// 那一刻用该连接名的服务解析到的是回落库，数据会落错地方。
    /// </remarks>
    [Fact]
    public async Task All_named_connections_are_registered_in_one_unit_of_work_before_provisioning()
    {
        _provisioner.OnProvision = async (services, tenant) =>
            _provisioner.ConnectionsSeenDuringProvisioning =
                await services.GetRequiredService<ITenantConnectionDirectory>().ListAsync(tenant.Id);

        var tenant = await _service.CreateAsync(
            Create("acme", ("default", "Host=acme;Database=acme"), ("CRM", "Host=crm;Database=acme-crm")));

        // 名字大小写不敏感：登记与解析都用归一化后的小写
        Assert.Equal(["crm", "default"], _provisioner.ConnectionsSeenDuringProvisioning!.Select(c => c.Name).Order());
        Assert.Equal(["crm", "default"], (await Connections().GetListAsync(tenant.Id)).Select(c => c.Name).Order());
    }

    /// <summary>请求体显式传 <c>connections: null</c> 按"不分库"处理，不是 500。</summary>
    /// <remarks>集合属性的默认值挡不住显式 null：反序列化会把它写进去，而下游按非空用它。</remarks>
    [Fact]
    public async Task An_explicit_null_connection_list_is_treated_as_no_dedicated_database()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme", Connections = null! });

        Assert.Empty(await Connections().GetListAsync(tenant.Id));
    }

    /// <summary>数组里的 null 元素是调用契约错误，而不是解引用成 500——DataAnnotations 不递归进集合。</summary>
    [Fact]
    public async Task A_null_connection_entry_is_rejected_as_a_validation_error()
    {
        var input = new CreateTenantInputDto
        {
            Name = "acme",
            Connections = [null!]
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(input));

        Assert.Contains("connections", error.ValidationResult.MemberNames);
        Assert.Empty(await NamedAsync("acme"));
    }

    /// <summary>重名在写库前拒绝：否则第二条会以"改已有登记"的语义覆盖第一条。</summary>
    [Fact]
    public async Task A_duplicated_connection_name_is_rejected_before_anything_is_written()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _service.CreateAsync(Create("acme", ("crm", "Host=a"), ("CRM", "Host=b"))));

        Assert.Equal(MultiTenancyErrorCodes.ConnectionNameDuplicated, error.Code);
        Assert.Empty(await NamedAsync("acme"));
    }

    [Fact]
    public async Task A_failed_provisioning_is_compensated_in_order_and_described()
    {
        _provisioner.OnProvision = (_, _) => throw new FakeDbException("3D000");

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _service.CreateAsync(
                Create("acme", ("default", "Host=acme;Database=missing"), ("crm", "Host=crm;Database=missing"))));

        Assert.Equal(MultiTenancyErrorCodes.DedicatedDatabaseMissing, error.Code);
        Assert.NotNull(_provisioner.PurgedTenant);
        Assert.Empty(await NamedAsync("acme"));
        Assert.Empty(_events.Published);

        // 名字可复用、没有指向已删租户的孤儿连接行
        _provisioner.OnProvision = null;
        var retried = await _service.CreateAsync(Create("acme"));
        Assert.Empty(await Connections().GetListAsync(retried.Id));
        await using var scope = _provider.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<TestDbContext>()
            .Set<Leistd.MultiTenancy.EntityFrameworkCore.Entities.TenantConnectionRecord>().CountAsync());
    }

    [Fact]
    public async Task A_failure_that_is_not_a_database_error_is_rethrown_unchanged()
    {
        _provisioner.OnProvision = (_, _) => throw new InvalidOperationException("seed bug");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(new CreateTenantInputDto { Name = "acme" }));

        Assert.Equal("seed bug", error.Message);
        Assert.Empty(await NamedAsync("acme"));
    }

    // 连接串填错是最常见的错误：先校验，不建租户再靠补偿擦掉
    [Fact]
    public async Task A_malformed_connection_string_is_rejected_before_anything_is_written()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(
            () => _service.CreateAsync(Create("acme", ("default", "Host=a;=b"))));

        Assert.Equal(MultiTenancyErrorCodes.ConnectionStringInvalid, error.Code);
        Assert.Null(_provisioner.ProvisionedInTenant);
        Assert.Empty(await NamedAsync("acme"));
    }

    [Fact]
    public async Task An_invalid_input_is_unprocessable()
    {
        var error = await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => _service.CreateAsync(new CreateTenantInputDto { Name = new string('x', 65) }));

        Assert.Contains(nameof(CreateTenantInputDto.Name), error.ValidationResult?.MemberNames ?? []);
    }

    [Fact]
    public async Task Activation_runs_the_guards_and_deactivation_does_not()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        await _service.SetActivationAsync(tenant.Id, new UpdateTenantActivationInputDto { IsActive = false });
        _guard.Reject = true;

        await Assert.ThrowsAsync<BusinessException>(
            () => _service.SetActivationAsync(tenant.Id, new UpdateTenantActivationInputDto { IsActive = true }));

        Assert.False((await _service.GetAsync(tenant.Id)).IsActive);
        Assert.Equal(1, _guard.Calls);
    }

    [Fact]
    public async Task Activating_a_missing_tenant_is_not_found_rather_than_rejected_by_a_guard()
    {
        await Assert.ThrowsAsync<TenantNotFoundException>(
            () => _service.SetActivationAsync(Guid.NewGuid(), new UpdateTenantActivationInputDto { IsActive = true }));

        Assert.Equal(0, _guard.Calls);
    }

    [Fact]
    public async Task Deletion_publishes_the_name_it_had_before_deletion()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme", DisplayName = "Acme" });
        _events.Published.Clear();

        await _service.DeleteAsync(tenant.Id);

        var deleted = Assert.IsType<TenantChangedEvent>(Assert.Single(_events.Published));
        Assert.Equal(("Acme", TenantChangeKind.Deleted), (deleted.DisplayName, deleted.Change));
        await Assert.ThrowsAsync<TenantNotFoundException>(() => _service.GetAsync(tenant.Id));
    }

    /// <summary>删除幂等：不存在与重复删除都直接成功，且不发事件——宿主据事件记的"已删除"只对应真正发生的那一次。</summary>
    [Fact]
    public async Task Deleting_a_missing_or_already_deleted_tenant_succeeds_without_an_event()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        _events.Published.Clear();

        await _service.DeleteAsync(Guid.NewGuid());
        await _service.DeleteAsync(tenant.Id);
        await _service.DeleteAsync(tenant.Id);

        Assert.Equal(TenantChangeKind.Deleted, Assert.IsType<TenantChangedEvent>(Assert.Single(_events.Published)).Change);
    }

    /// <summary>快照之后、删除之前被另一请求删掉：管理器按严格契约抛不存在，用例按删除幂等视为成功，不发事件。</summary>
    [Fact]
    public async Task A_tenant_deleted_concurrently_after_the_snapshot_is_treated_as_deleted_without_an_event()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        _events.Published.Clear();
        var raced = false;
        _beforeDelete = async id =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await ActivatorUtilities.CreateInstance<EfCoreTenantManager<TestDbContext>>(scope.ServiceProvider).DeleteAsync(id);
            raced = true;
        };

        await _service.DeleteAsync(tenant.Id);

        Assert.True(raced, "The concurrent deletion was not injected; the race path was not exercised");
        Assert.Empty(_events.Published);
        await Assert.ThrowsAsync<TenantNotFoundException>(() => _service.GetAsync(tenant.Id));
    }

    /// <summary>只有"目标已不存在"算成功：并发冲突等真实失败照常抛出，租户仍在，也没有成功事件。</summary>
    [Fact]
    public async Task A_concurrency_conflict_while_deleting_is_still_thrown_without_an_event()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        _events.Published.Clear();
        _beforeDelete = id => throw new TenantConcurrencyConflictException(id);

        await Assert.ThrowsAsync<TenantConcurrencyConflictException>(() => _service.DeleteAsync(tenant.Id));

        Assert.Empty(_events.Published);
        Assert.Equal(tenant.Id, (await _service.GetAsync(tenant.Id)).Id);
    }

    [Fact]
    public async Task Paging_uses_the_registry()
    {
        await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        await _service.CreateAsync(new CreateTenantInputDto { Name = "contoso" });

        var page = await _service.GetPagedAsync(new GetTenantPagedInputDto { Keyword = "ACME", Limit = 5 });

        Assert.Equal("acme", Assert.Single(page.Items).Name);
    }

    [Fact]
    public async Task Connection_listing_distinguishes_a_missing_tenant_from_an_unsplit_one()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });

        Assert.Empty(await Connections().GetListAsync(tenant.Id));
        await Assert.ThrowsAsync<TenantNotFoundException>(() => Connections().GetListAsync(Guid.NewGuid()));
    }

    /// <summary>连接名来自 URL：不合法是 400，不以 500 出去——每个按名字查的入口都算。</summary>
    /// <remarks>
    /// 逐条列出来是因为它们各自调用一次归一化，漏一个不会有任何编译或运行期提示：
    /// 某个入口若把原始入参直接交给目录，目录用的是代码级归一化
    /// （非法即 <see cref="ArgumentException"/>），同一个错输入在这个路由上就变成 500。
    /// </remarks>
    [Theory]
    [InlineData("runtime")]
    [InlineData("databases")]
    [InlineData("migration")]
    public async Task An_invalid_connection_name_in_a_machine_lookup_is_a_coded_bad_request(string entry)
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });

        var error = await Assert.ThrowsAsync<BusinessException>(() => entry switch
        {
            "runtime" => Connections().GetRuntimeAsync(tenant.Id, "crm_db"),
            "databases" => Connections().GetDatabaseListAsync("crm_db", activeOnly: true),
            _ => Connections().GetMigrationListAsync("crm_db")
        });

        Assert.Equal(MultiTenancyErrorCodes.ConnectionNameInvalid, error.Code);
    }

    [Fact]
    public async Task Connections_are_managed_by_name_with_versions()
    {
        var tenant = await _service.CreateAsync(new CreateTenantInputDto { Name = "acme" });
        await _service.SetActivationAsync(tenant.Id, new UpdateTenantActivationInputDto { IsActive = false });

        var set = await Connections().SetAsync(tenant.Id, "CRM", new UpsertTenantConnectionInputDto { ExpectedVersion = null, ConnectionString = "Host=crm" });
        var runtime = await Connections().GetRuntimeAsync(tenant.Id, "crm");
        await Connections().RemoveAsync(tenant.Id, "crm", set.Version);

        Assert.Equal(("crm", 1L), (set.Name, set.Version));
        Assert.Equal("Host=crm", runtime.Connection!.ConnectionString);
        Assert.Empty(await Connections().GetListAsync(tenant.Id));
    }

    private ITenantConnectionManagementService Connections() => _provider.GetRequiredService<ITenantConnectionManagementService>();

    private sealed class RecordingProvisioner : ITenantProvisioner
    {
        public Func<IServiceProvider, TenantConfiguration, Task>? OnProvision { get; set; }

        public Guid? ProvisionedInTenant { get; private set; }

        public Guid? PurgedTenant { get; private set; }

        public IReadOnlyList<TenantConnectionEntry>? ConnectionsSeenDuringProvisioning { get; set; }

        public IServiceProvider? Services { get; set; }

        public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            ProvisionedInTenant = CurrentTenant.Id;
            if (OnProvision is not null)
            {
                await OnProvision(Services!, context.Tenant);
            }
        }

        public Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
        {
            PurgedTenant = CurrentTenant.Id;
            return Task.CompletedTask;
        }

        private static ICurrentTenant CurrentTenant => new CurrentTenantProbe();
    }

    // 直接读环境上下文里的当前租户，证明开通与清理都发生在目标租户上下文
    private sealed class CurrentTenantProbe : ICurrentTenant
    {
        private readonly ICurrentTenantAccessor _accessor = Leistd.MultiTenancy.Context.AsyncLocalCurrentTenantAccessor.Instance;

        public bool IsAvailable => Id.HasValue;
        public Guid? Id => _accessor.Current?.TenantId;
        public string? Name => _accessor.Current?.Name;
        public IDisposable Change(Guid? id, string? name = null) => throw new NotSupportedException();
    }

    private sealed class RejectingGuard : ITenantActivationGuard
    {
        public bool Reject { get; set; }

        public int Calls { get; private set; }

        public Task EnsureCanActivateAsync(TenantConfiguration tenant, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Reject
                ? throw new BusinessException("Tenant:ActivateWithoutUsers", "This tenant has no users yet.")
                : Task.CompletedTask;
        }
    }

    // 接口上不再有按名称查租户：那是 A2b 移除的匿名探测语义。
    // 测试要确认"建没建出来"时走管理侧的分页查询
    private async Task<IReadOnlyList<TenantOutputDto>> NamedAsync(string name)
        => (await _service.GetPagedAsync(new GetTenantPagedInputDto { Keyword = name, Limit = 5 })).Items;

    private static CreateTenantInputDto Create(string name, params (string Name, string ConnectionString)[] connections)
        => new()
        {
            Name = name,
            Connections = [.. connections.Select(c => new CreateTenantConnectionInputDto
            {
                Name = c.Name,
                ConnectionString = c.ConnectionString
            })]
        };

    // 透传真实管理器，只在删除前执行测试给的动作
    private sealed class InterleavingTenantManager(ITenantManager inner, Func<Func<Guid, Task>?> beforeDelete) : ITenantManager
    {
        public Task<TenantConfiguration> CreateAsync(
            string name,
            string? displayName,
            bool isActive,
            string? description = null,
            CancellationToken cancellationToken = default)
            => inner.CreateAsync(name, displayName, isActive, description, cancellationToken);

        public Task<TenantConfiguration> UpdateAsync(
            Guid id,
            string name,
            string? displayName,
            string? description = null,
            CancellationToken cancellationToken = default)
            => inner.UpdateAsync(id, name, displayName, description, cancellationToken);

        public Task<TenantConfiguration> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
            => inner.SetActiveAsync(id, isActive, cancellationToken);

        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (beforeDelete() is { } hook)
            {
                await hook(id);
            }

            await inner.DeleteAsync(id, cancellationToken);
        }

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.FindAsync(id, cancellationToken);

        public Task<PagedResult<TenantConfiguration>> GetPagedAsync(string? keyword, PageRequest page, CancellationToken cancellationToken = default)
            => inner.GetPagedAsync(keyword, page, cancellationToken);
    }

    private sealed class FakeDbException(string sqlState) : DbException("database failure")
    {
        public override string SqlState => sqlState;
    }

    // 只认一种码，其余返回 null——与真实宿主实现同形：认不出来的错误不翻译
    private sealed class FakeErrorDescriber : ITenantDatabaseErrorDescriber
    {
        public BusinessException? Describe(Exception error)
        {
            for (var current = error; current is not null; current = current.InnerException)
            {
                if (current is DbException { SqlState: "3D000" })
                {
                    return new BusinessException(MultiTenancyErrorCodes.DedicatedDatabaseMissing, "The database does not exist.");
                }
            }

            return null;
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ConfigureMultiTenancy();
        }
    }
}
