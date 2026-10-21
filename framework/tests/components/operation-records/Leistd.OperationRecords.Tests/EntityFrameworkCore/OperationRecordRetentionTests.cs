using Leistd.BackgroundJobs.Recurring;
using Leistd.Data.Connections;
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.EntityFrameworkCore;
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
using Leistd.OperationRecords.EntityFrameworkCore.Options;
using Leistd.OperationRecords.EntityFrameworkCore.Retention;
using Leistd.OperationRecords.Tests.TestDoubles;
using Leistd.Security;
using Leistd.Tracing;
using Leistd.TestBase.Doubles;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Leistd.OperationRecords.Tests.EntityFrameworkCore;

/// <summary>保留期归档：按物理库逐个搬运，同库的其余租户一并覆盖，列逐一对齐。</summary>
/// <remarks>
/// <para>两个错了都不报错的陷阱：无租户上下文里的租户过滤器只放行宿主自己的行（漏 <c>IgnoreQueryFilters</c>
/// 则租户记录永远留着）；只连宿主库则独立库租户的记录永远留着。日志照样打印"归档成功 N 条"。</para>
/// <para>宿主库与租户库是两个独立的 SQLite 内存库，连接按当前租户路由。</para>
/// </remarks>
public sealed class OperationRecordRetentionTests : IAsyncLifetime
{
    private static readonly Guid DedicatedTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SharedTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime Now = new(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _host = $"Data Source=archive-host-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string _tenant = $"Data Source=archive-tenant-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
        services.AddSingleton<IConnectionStringResolver>(sp => new RoutingResolver(
            sp.GetRequiredService<ICurrentTenantAccessor>(), _host, _tenant));
        services.AddSingleton<ITenantDatabaseEnumerator>(new FixedDatabases(
            TenantDatabase.ForHost("host"), new TenantDatabase(DedicatedTenant, "dedicated", [])));
        services.AddSingleton<IClock>(new UtcClockProvider(new FakeTimeProvider(new DateTimeOffset(Now))));
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<TestDbContext>((_, options) =>
            options.UseSqlite(DbContextCreationContext.Current?.ConnectionString ?? _host));
        services.AddOperationRecordsEfCore<TestDbContext>();
        services.AddOperationRecordRetention<TestDbContext>();
        _services = services.BuildServiceProvider();

        foreach (var connectionString in new[] { _host, _tenant })
        {
            await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options);
            await db.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _hostAnchor.DisposeAsync();
        await _tenantAnchor.DisposeAsync();
    }

    [Fact]
    public async Task Expired_records_are_archived_in_every_database_including_other_tenants_sharing_it()
    {
        var expired = Now.AddDays(-400);
        var fresh = Now.AddDays(-1);
        await SeedAsync(_host, (null, expired), (SharedTenant, expired), (null, fresh));
        await SeedAsync(_tenant, (DedicatedTenant, expired), (DedicatedTenant, fresh));

        // 批大小 1：逐批循环走满，每批一个事务
        var result = await _services.GetRequiredService<IOperationRecordArchiveService>()
            .ArchiveOlderThanAsync(Now.AddDays(-365), batchSize: 1);

        Assert.Equal(new OperationRecordArchiveResult(Archived: 3, Databases: 2, FailedDatabases: 0, UnresolvedTenants: 0), result);
        Assert.Equal((1, 2), await CountAsync(_host));
        Assert.Equal((1, 1), await CountAsync(_tenant));
    }

    /// <summary>搬运是逐字段复制：原表加了列而归档表没跟上，搬完照样报成功，缺的那列到查归档时才会发现。</summary>
    [Fact]
    public void The_archive_carries_every_record_column()
    {
        var archiveColumns = typeof(OperationRecordArchive).GetProperties().Select(p => p.Name).ToHashSet();

        var missing = typeof(OperationRecord).GetProperties().Select(p => p.Name).Except(archiveColumns);

        Assert.Equal([], missing);
    }

    [Fact]
    public void Retention_is_a_cluster_job_scheduled_daily()
    {
        var definition = _services.GetServices<RecurringJobDefinition>().Single(d => d.Name == "operation-records.archive");

        Assert.Equal(RecurringJobScope.Cluster, definition.Scope);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero),
            definition.ScheduleFactory(_services).GetSlot(new DateTimeOffset(2026, 9, 20, 23, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>越界的保留天数在启动期被拒，而不是某天夜里把最近的记录搬走。</summary>
    [Fact]
    public void An_out_of_range_retention_is_rejected()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddOperationRecordRetention<TestDbContext>(o => o.RetentionDays = 5)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OperationRecordRetentionOptions>>().Value);
    }

    /// <summary>
    /// 开关登记给调度器而不是在任务里提前返回：这里只证明真实登记入口带上了开关、且按新作用域里的配置求值；
    /// 关闭时到点跳过、不记完成由后台作业组件的调度测试保证。
    /// </summary>
    [Fact]
    public void The_registered_switch_follows_the_retention_setting_each_round()
    {
        var settings = new Dictionary<string, string?> { ["Leistd:OperationRecords:Retention:Enabled"] = "false" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddOperationRecordRetention<TestDbContext>()
            .BuildServiceProvider();
        var definition = provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == "operation-records.archive");

        using (var off = provider.CreateScope())
            Assert.False(definition.IsEnabled!(off.ServiceProvider));

        configuration["Leistd:OperationRecords:Retention:Enabled"] = "true";
        configuration["Leistd:OperationRecords:Retention:RetentionDays"] = "90";
        using var on = provider.CreateScope();
        Assert.True(definition.IsEnabled!(on.ServiceProvider));
    }

    /// <summary>有库失败、或有租户解析不出库时抛出，让调度器不记水位。</summary>
    /// <remarks>
    /// 解析不出连接的租户同样一条都没搬走。这一档报成功的话，积压虽然会被
    /// 下一轮按截止时间扫到，但"有一批库进不去"这件事没有任何人知道，直到它一直进不去。
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task A_failed_database_or_an_unresolved_tenant_fails_the_run(int failedDatabases, int unresolvedTenants)
    {
        var archive = new CountingArchive(new OperationRecordArchiveResult(3, 2, failedDatabases, unresolvedTenants));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Job(enabled: true, archive).ExecuteAsync(new RecurringJobContext("operation-records.archive", DateTimeOffset.UtcNow), CancellationToken.None));

        Assert.Contains($"{failedDatabases} of 2 database(s) failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, archive.Calls);
    }

    /// <summary>启用时按配置的保留天数算截止时间，批大小原样交给归档服务。</summary>
    [Fact]
    public async Task An_enabled_retention_archives_records_older_than_the_configured_days()
    {
        var archive = new CountingArchive(new OperationRecordArchiveResult(3, 2, 0));

        await Job(enabled: true, archive, retentionDays: 90)
            .ExecuteAsync(new RecurringJobContext("operation-records.archive", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal((1, Now.AddDays(-90), 500), (archive.Calls, archive.LastCutoff, archive.LastBatchSize));
    }

    /// <summary>按文档注册后能解析出周期任务本身。</summary>
    /// <remarks>
    /// 归档任务另外依赖逐库遍历（<see cref="ITenantDatabaseRunner"/>），由
    /// <c>AddMultiTenancyCore()</c> 提供；本组件不替调用方注册跨组件依赖。
    /// 基础注册面的解析见 <c>OperationRecordRegistrationTests</c>。
    /// </remarks>
    [Fact]
    public void The_documented_registration_resolves_the_recurring_job()
    {
        var configuration = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddUnitOfWork()
            .AddUnitOfWorkEfCore()
            .AddDbContext<TestDbContext>(options => options.UseSqlite("DataSource=:memory:"))
            .AddSingleton<IClock, UtcClockProvider>()
            .AddMultiTenancyCore()
            .AddAmbientContext()
            .AddCorrelationIdCore()
            .AddOperationRecordsEfCore<TestDbContext>()
            .AddOperationRecordRetention<TestDbContext>()
            .BuildServiceProvider();

        var definition = provider.GetServices<RecurringJobDefinition>().Single(d => d.Name == "operation-records.archive");

        using var scope = provider.CreateScope();
        Assert.IsType<OperationRecordArchiveJob>(scope.ServiceProvider.GetRequiredService(definition.JobType));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOperationRecordArchiveService>());
    }

    private static OperationRecordArchiveJob Job(bool enabled, IOperationRecordArchiveService archive, int? retentionDays = 365) => new(
        new FixedSnapshot<OperationRecordRetentionOptions>(
            new OperationRecordRetentionOptions { Enabled = enabled, RetentionDays = retentionDays }),
        archive,
        new UtcClockProvider(new FakeTimeProvider(new DateTimeOffset(Now))),
        new FakeLogger<OperationRecordArchiveJob>());

    private static async Task SeedAsync(string connectionString, params (Guid? TenantId, DateTime CreationTime)[] rows)
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options);
        db.Set<OperationRecord>().AddRange(rows.Select(row => new OperationRecord
        {
            TenantId = row.TenantId,
            ActorTenantId = row.TenantId,
            Action = "user.created",
            TargetId = "-",
            AuthorizationBasis = "test",
            Outcome = OperationRecordOutcome.Succeeded,
            Visibility = OperationVisibility.Tenant,
            CreationTime = row.CreationTime
        }));
        await db.SaveChangesAsync();
    }

    private static async Task<(int Records, int Archived)> CountAsync(string connectionString)
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connectionString).Options);
        return (
            await db.Set<OperationRecord>().IgnoreQueryFilters().CountAsync(),
            await db.Set<OperationRecordArchive>().CountAsync(a => a.ActorTenantId == a.TenantId));
    }

    private sealed class CountingArchive(OperationRecordArchiveResult result) : IOperationRecordArchiveService
    {
        public int Calls { get; private set; }

        public DateTime? LastCutoff { get; private set; }

        public int? LastBatchSize { get; private set; }

        public Task<OperationRecordArchiveResult> ArchiveOlderThanAsync(DateTime cutoffUtc, int batchSize, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCutoff = cutoffUtc;
            LastBatchSize = batchSize;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedDatabases(params TenantDatabase[] databases) : ITenantDatabaseEnumerator
    {
        public Task<TenantDatabaseSet> GetDatabasesAsync(string name, bool activeOnly, CancellationToken cancellationToken = default)
            => Task.FromResult(new TenantDatabaseSet(databases, []));
    }

    private sealed class RoutingResolver(ICurrentTenantAccessor accessor, string host, string tenant) : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
            => Task.FromResult(accessor.Current?.TenantId == DedicatedTenant ? tenant : host);
    }

    private sealed class FixedSnapshot<TOptions>(TOptions value) : IOptionsSnapshot<TOptions> where TOptions : class
    {
        public TOptions Value { get; } = value;

        public TOptions Get(string? name) => Value;
    }
}
