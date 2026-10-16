using Leistd.BackgroundJobs.Recurring;
using Leistd.Data.Connections;
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.Notifications.EntityFrameworkCore;
using Leistd.Notifications.EntityFrameworkCore.Entities;
using Leistd.Notifications.EntityFrameworkCore.Options;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.Notifications.Tests.EntityFrameworkCore;

/// <summary>通知保留期：已读与未读分开计时，按物理库逐个清理，同库的其余租户一并覆盖。</summary>
public sealed class NotificationRetentionTests : IAsyncLifetime
{
    private static readonly Guid DedicatedTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now = new(2026, 9, 20, 19, 0, 0, DateTimeKind.Utc);

    private readonly string _host = $"Data Source=notify-host-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string _tenant = $"Data Source=notify-tenant-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly FixedDatabases _databases = new(
        TenantDatabase.ForHost("host"), new TenantDatabase(DedicatedTenant, "dedicated", []));
    private SqliteConnection _hostAnchor = default!;
    private SqliteConnection _tenantAnchor = default!;
    private ServiceProvider _services = default!;

    public async Task InitializeAsync()
    {
        _hostAnchor = new SqliteConnection(_host);
        _tenantAnchor = new SqliteConnection(_tenant);
        await _hostAnchor.OpenAsync();
        await _tenantAnchor.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddMultiTenancyCore();
        services.AddSingleton<IConnectionStringResolver>(sp => new RoutingResolver(sp.GetRequiredService<ICurrentTenantAccessor>(), _host, _tenant));
        services.AddSingleton<ITenantDatabaseEnumerator>(_databases);
        services.AddSingleton<IClock>(new FakeClock(Now));
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<RetentionDbContext>((_, options) => options.UseSqlite(DbContextCreationContext.Current?.ConnectionString ?? _host));
        services.AddNotificationsEfCore<RetentionDbContext>();
        services.AddNotificationRetention<RetentionDbContext>();
        _services = services.BuildServiceProvider();

        foreach (var connectionString in new[] { _host, _tenant })
        {
            await using var db = new RetentionDbContext(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(connectionString).Options);
            await db.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _hostAnchor.DisposeAsync();
        await _tenantAnchor.DisposeAsync();
    }

    /// <summary>已读超 90 天删、未读超 365 天删，未读的在两者之间保留；独立库与共享库里的其他租户都被覆盖。</summary>
    [Fact]
    public async Task Expired_notifications_are_deleted_per_database_by_read_state()
    {
        await SeedAsync(_host,
            (null, "read-old", true, 100),
            (Guid.NewGuid(), "shared-tenant-read-old", true, 100),
            (null, "unread-mid", false, 100),
            (null, "unread-old", false, 400),
            (null, "read-new", true, 10));
        await SeedAsync(_tenant,
            (DedicatedTenant, "tenant-read-old", true, 100),
            (DedicatedTenant, "tenant-unread-mid", false, 100));

        var definition = _services.GetServices<RecurringJobDefinition>().Single(d => d.Name == "notifications.retention");
        await using (var scope = _services.CreateAsyncScope())
        {
            var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(definition.JobType);
            await job.ExecuteAsync(new RecurringJobContext(definition.Name, Now), CancellationToken.None);
        }

        Assert.Equal(["read-new", "unread-mid"], await TitlesAsync(_host));
        Assert.Equal(["tenant-unread-mid"], await TitlesAsync(_tenant));
        Assert.Equal(RecurringJobScope.Cluster, definition.Scope);
    }

    /// <summary>有租户解析不出库时本轮失败，不记水位。</summary>
    /// <remarks>
    /// 解析失败被逐库执行器隔离掉了（一个坏租户不该让整轮不执行），但"隔离"不等于"没事"：
    /// 那个租户的通知一条都没清。任务若只看失败的库，就会把这一轮报成成功，
    /// 一批库长期进不去也不会有任何告警。
    /// </remarks>
    [Fact]
    public async Task An_unresolved_tenant_fails_the_run()
    {
        _databases.Unresolved.Add(new TenantDatabaseFailure(Guid.NewGuid(), "ring rotated"));
        var definition = _services.GetServices<RecurringJobDefinition>().Single(d => d.Name == "notifications.retention");

        await using var scope = _services.CreateAsyncScope();
        var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(definition.JobType);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ExecuteAsync(new RecurringJobContext(definition.Name, Now), CancellationToken.None));
    }

    /// <summary>按文档注册后能解析出周期任务本身。</summary>
    /// <remarks>
    /// 清理任务另外依赖逐库遍历（<see cref="ITenantDatabaseRunner"/>），由
    /// <c>AddMultiTenancyCore()</c> 提供；本组件不替调用方注册跨组件依赖。
    /// 基础注册面的解析见 <c>NotificationStoreRegistrationTests</c>。
    /// </remarks>
    [Fact]
    public void The_documented_registration_resolves_the_recurring_job()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddUnitOfWork()
            .AddUnitOfWorkEfCore()
            .AddSingleton<IClock, UtcClockProvider>()
            .AddMultiTenancyCore()
            .AddNotificationsEfCore<RetentionDbContext>()
            .AddNotificationRetention<RetentionDbContext>();
        services.AddDbContext<RetentionDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        using var provider = services.BuildServiceProvider();

        var definition = provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == "notifications.retention");

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService(definition.JobType));
    }

    /// <summary>
    /// 开关登记给调度器而不是在任务里提前返回：这里只证明真实登记入口带上了开关、且按新作用域里的配置求值；
    /// 关闭时到点跳过、不记完成由后台作业组件的调度测试保证。
    /// </summary>
    [Fact]
    public void The_registered_switch_follows_the_retention_setting_each_round()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Leistd:Notifications:Retention:Enabled"] = "false" })
            .Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddNotificationRetention<RetentionDbContext>()
            .BuildServiceProvider();
        var definition = provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == "notifications.retention");

        using (var off = provider.CreateScope())
            Assert.False(definition.IsEnabled!(off.ServiceProvider));

        configuration["Leistd:Notifications:Retention:Enabled"] = "true";
        using var on = provider.CreateScope();
        Assert.True(definition.IsEnabled!(on.ServiceProvider));
    }

    /// <summary>未读比已读先删就本末倒置：用户还没看到的通知反而先消失。</summary>
    [Fact]
    public void An_unread_retention_shorter_than_read_is_rejected()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddNotificationRetention<RetentionDbContext>(o => o.UnreadRetentionDays = 30)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<NotificationRetentionOptions>>().Value);
    }

    private static async Task SeedAsync(string connectionString, params (Guid? TenantId, string Title, bool IsRead, int AgeDays)[] rows)
    {
        await using var db = new RetentionDbContext(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(connectionString).Options);
        db.Set<NotificationRecord>().AddRange(rows.Select(row => new NotificationRecord
        {
            TenantId = row.TenantId,
            UserId = "u1",
            Title = row.Title,
            IsRead = row.IsRead,
            CreationTime = Now.AddDays(-row.AgeDays)
        }));
        await db.SaveChangesAsync();
    }

    private static async Task<string[]> TitlesAsync(string connectionString)
    {
        await using var db = new RetentionDbContext(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(connectionString).Options);
        return await db.Set<NotificationRecord>().IgnoreQueryFilters().OrderBy(x => x.Title).Select(x => x.Title).ToArrayAsync();
    }

    public sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureNotifications();
    }

    private sealed class FixedDatabases(params TenantDatabase[] databases) : ITenantDatabaseEnumerator
    {
        /// <summary>本轮解析不出连接的租户，用例按需填入。</summary>
        public List<TenantDatabaseFailure> Unresolved { get; } = [];

        public Task<TenantDatabaseSet> GetDatabasesAsync(string name, bool activeOnly, CancellationToken cancellationToken = default)
            => Task.FromResult(new TenantDatabaseSet(databases, [.. Unresolved]));
    }

    private sealed class RoutingResolver(ICurrentTenantAccessor accessor, string host, string tenant) : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
            => Task.FromResult(accessor.Current?.TenantId == DedicatedTenant ? tenant : host);
    }
}
