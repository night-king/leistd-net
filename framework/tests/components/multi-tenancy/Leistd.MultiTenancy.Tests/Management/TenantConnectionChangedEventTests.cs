using Leistd.EventBus.Abstractions;
using Leistd.EventBus.Events;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Stores;
using Xunit;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Events;

namespace Leistd.MultiTenancy.Tests.Management;

/// <summary>连接登记变化发出的事件：宿主据此留痕，所以"发不发、发哪一种"是契约。</summary>
/// <remarks>
/// 组件不依赖操作记录组件，只发中性事件；记成什么词汇由宿主定。因此这里钉住的是事件本身，
/// 不是审计表的内容。事件里<b>没有连接串</b>——它是凭据。
/// </remarks>
public class TenantConnectionChangedEventTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    // 直接构造而不过 DI：本用例钉的是"发不发事件"，不该绑在注册细节上
    private static (ITenantConnectionManagementService Service, RecordingEventBus Bus, StubManager Manager) Create()
    {
        var manager = new StubManager();
        var bus = new RecordingEventBus();
        var service = new TenantConnectionManagementService(
            new StubDirectory(), new StubStore(), manager, new StubTenants(), new StubDatabaseDirectory(), eventBus: bus);
        return (service, bus, manager);
    }

    /// <summary>首次登记（没有预期版本）发 Registered。</summary>
    [Fact]
    public async Task Registering_a_connection_publishes_Registered()
    {
        var (service, bus, _) = Create();

        await service.SetAsync(Tenant, "default", new UpsertTenantConnectionInputDto
        {
            ConnectionString = "Host=db",
            ExpectedVersion = null
        });

        var published = Assert.Single(bus.Published);
        Assert.Equal((Tenant, "default", TenantConnectionChangeKind.Registered), (published.TenantId, published.Name, published.Change));
    }

    /// <summary>带预期版本的改写发 Changed：那是换库或轮换凭据，与新增落点不是一回事。</summary>
    [Fact]
    public async Task Changing_an_existing_connection_publishes_Changed()
    {
        var (service, bus, _) = Create();

        await service.SetAsync(Tenant, "default", new UpsertTenantConnectionInputDto
        {
            ConnectionString = "Host=db2",
            ExpectedVersion = 1
        });

        Assert.Equal(TenantConnectionChangeKind.Changed, Assert.Single(bus.Published).Change);
    }

    /// <summary>删除登记发 Removed：该租户退回宿主库，这件事必须与新增区分得开。</summary>
    [Fact]
    public async Task Removing_a_connection_publishes_Removed()
    {
        var (service, bus, _) = Create();

        await service.RemoveAsync(Tenant, "default", expectedVersion: 3);

        var published = Assert.Single(bus.Published);
        Assert.Equal((Tenant, "default", TenantConnectionChangeKind.Removed), (published.TenantId, published.Name, published.Change));
    }

    /// <summary>写入失败时一条事件都不发，否则审计表里会留下一笔没发生过的变更。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_write_publishes_nothing(bool removing)
    {
        var (service, bus, manager) = Create();
        manager.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => removing
            ? service.RemoveAsync(Tenant, "default", expectedVersion: 1)
            : service.SetAsync(Tenant, "default", new UpsertTenantConnectionInputDto
            {
                ConnectionString = "Host=db",
                ExpectedVersion = null
            }));

        Assert.Empty(bus.Published);
    }

    /// <summary>事件里不带连接串：它是凭据，发出去就进了每一个订阅者。</summary>
    [Fact]
    public async Task The_event_never_carries_the_connection_string()
    {
        var (service, bus, _) = Create();

        await service.SetAsync(Tenant, "default", new UpsertTenantConnectionInputDto
        {
            ConnectionString = "Host=db;Password=s3cret",
            ExpectedVersion = null
        });

        var published = Assert.Single(bus.Published);
        Assert.DoesNotContain("s3cret", string.Join('|', published.TenantId, published.Name, published.Change, published.Version));
    }

    /// <summary>事件带租户显示名快照：订阅者写"给谁改的"时不必拿连接名顶替。</summary>
    /// <remarks>
    /// 没有这一项时，宿主手上只有租户标识与连接名，审计的目标名列就会被填进
    /// <c>default</c>、<c>crm</c> 这样的连接名，界面上显示成"为租户 default 登记了连接"。
    /// 快照而非外键：事后按标识反查只能得到改名后的值。
    /// </remarks>
    [Fact]
    public async Task The_event_carries_the_tenant_display_name_not_the_connection_name()
    {
        var (service, bus, _) = Create();

        await service.SetAsync(Tenant, "crm", new UpsertTenantConnectionInputDto
        {
            ConnectionString = "Host=crm",
            ExpectedVersion = null
        });

        var published = Assert.Single(bus.Published);
        Assert.Equal((StubTenants.DisplayName, "crm"), (published.TenantDisplayName, published.Name));
    }

    /// <summary>事件里的连接名是归一化后的值，删除这条路径也不例外。</summary>
    /// <remarks>
    /// 写入路径发的是管理器回的 <c>configuration.Name</c>（权威值），删除没有这个返回值，
    /// 照原样转发就会让 <c>" CRM "</c> 与 <c>crm</c> 在审计里落成两个目标标识，
    /// 同一条连接的登记与删除从此检索不到一起。
    /// </remarks>
    [Fact]
    public async Task A_removal_publishes_the_normalized_connection_name()
    {
        var (service, bus, _) = Create();

        await service.RemoveAsync(Tenant, "  CRM  ", expectedVersion: 3);

        Assert.Equal("crm", Assert.Single(bus.Published).Name);
    }

    private sealed class RecordingEventBus : ILocalEventBus
    {
        public List<TenantConnectionChangedEvent> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : IEvent
        {
            Capture(@event);
            return Task.CompletedTask;
        }

        public Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default)
        {
            Capture(@event);
            return Task.CompletedTask;
        }

        private void Capture(object? @event)
        {
            if (@event is TenantConnectionChangedEvent changed)
            {
                Published.Add(changed);
            }
        }
    }

    private sealed class StubManager : ITenantConnectionConfigurationManager
    {
        public bool Fail { get; set; }

        public Task<TenantConnectionConfiguration> SetAsync(
            Guid tenantId, string name, string connectionString, long? expectedVersion, CancellationToken cancellationToken = default)
            => Fail
                ? throw new InvalidOperationException("injected write failure")
                : Task.FromResult(new TenantConnectionConfiguration
                {
                    TenantId = tenantId,
                    Name = name,
                    ConnectionString = connectionString,
                    Version = (expectedVersion ?? 0) + 1
                });

        public Task RemoveAsync(Guid tenantId, string name, long expectedVersion, CancellationToken cancellationToken = default)
            => Fail ? throw new InvalidOperationException("injected write failure") : Task.CompletedTask;
    }

    private sealed class StubDirectory : ITenantConnectionDirectory
    {
        public Task<IReadOnlyList<TenantConnectionEntry>?> ListAsync(
            Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantConnectionEntry>?>([]);
    }

    private sealed class StubDatabaseDirectory : ITenantDatabaseDirectory
    {
        public Task<TenantDatabaseListResult> GetDatabasesAsync(
            string name, bool activeOnly, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubTenants : ITenantStore
    {
        public const string DisplayName = "Acme 株式会社";

        public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConfiguration?>(new TenantConfiguration
            {
                Id = id,
                Name = "acme",
                NormalizedName = "acme",
                DisplayName = DisplayName,
                IsActive = false
            });

        public Task<TenantConfiguration?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConfiguration?>(null);
    }

    private sealed class StubStore : ITenantConnectionConfigurationStore
    {
        public Task<TenantConnectionLookupResult?> FindAsync(
            Guid tenantId, string name, CancellationToken cancellationToken = default)
            => Task.FromResult<TenantConnectionLookupResult?>(null);

        public Task<TenantMigrationConnectionListResult> GetListAsync(
            string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new TenantMigrationConnectionListResult([], []));
    }
}
