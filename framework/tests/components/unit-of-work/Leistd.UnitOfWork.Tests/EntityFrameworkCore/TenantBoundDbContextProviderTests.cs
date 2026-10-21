using Leistd.MultiTenancy;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.Data.Connections;
using Leistd.MultiTenancy.Context;

namespace Leistd.UnitOfWork.Tests.EntityFrameworkCore;

public class TenantBoundDbContextProviderTests : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private SqliteConnection _anchor = default!;
    private ServiceProvider _services = default!;
    private MutableResolver _resolver = default!;

    public async Task InitializeAsync()
    {
        var databaseName = $"uow-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(connectionString);
        await _anchor.OpenAsync();
        _resolver = new MutableResolver(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMultiTenancyCore();
        services.AddSingleton<IConnectionStringResolver>(_resolver);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<FirstDbContext>((_, options) => ConfigureSqlite(options, connectionString));
        services.AddDbContext<SecondDbContext>((_, options) => ConfigureSqlite(options, connectionString));
        services.AddDbContext<ControlDbContext>((_, options) => ConfigureSqlite(options, connectionString));
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FirstDbContext>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<SecondDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _anchor.DisposeAsync();
    }

    [Fact]
    public async Task Two_dbcontext_types_on_one_target_share_the_unit_of_work_commit()
    {
        var currentTenant = _services.GetRequiredService<ICurrentTenant>();
        var manager = _services.GetRequiredService<IUnitOfWorkManager>();
        var firstProvider = _services.GetRequiredService<IDbContextProvider<FirstDbContext>>();
        var secondProvider = _services.GetRequiredService<IDbContextProvider<SecondDbContext>>();

        using (currentTenant.Change(_tenantId))
        using (var unitOfWork = manager.Begin())
        {
            (await firstProvider.GetDbContextAsync()).FirstRows.Add(new FirstRow());
            (await secondProvider.GetDbContextAsync()).SecondRows.Add(new SecondRow());
            await unitOfWork.CompleteAsync();
        }

        await using var verificationScope = _services.CreateAsyncScope();
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<FirstDbContext>().FirstRows.CountAsync());
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<SecondDbContext>().SecondRows.CountAsync());
        Assert.True(_resolver.WasAwaited);
    }

    [Fact]
    public async Task Two_configured_dbcontext_types_on_one_target_share_the_unit_of_work_without_a_resolver()
    {
        var databaseName = $"configured-uow-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<FirstDbContext>((_, options) => ConfigureSqlite(options, connectionString));
        services.AddDbContext<SecondDbContext>((_, options) => ConfigureSqlite(options, connectionString));
        await using var provider = services.BuildServiceProvider();

        await using (var initializationScope = provider.CreateAsyncScope())
        {
            await initializationScope.ServiceProvider.GetRequiredService<FirstDbContext>().Database.EnsureCreatedAsync();
            await initializationScope.ServiceProvider.GetRequiredService<SecondDbContext>().Database.EnsureCreatedAsync();
        }

        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using (var unitOfWork = manager.Begin())
        {
            var unitOfWorkProvider = ((Leistd.UnitOfWork.DefaultUnitOfWork)unitOfWork).ServiceProvider;
            var firstProvider = unitOfWorkProvider.GetRequiredService<IDbContextProvider<FirstDbContext>>();
            var secondProvider = unitOfWorkProvider.GetRequiredService<IDbContextProvider<SecondDbContext>>();
            (await firstProvider.GetDbContextAsync()).FirstRows.Add(new FirstRow());
            (await secondProvider.GetDbContextAsync()).SecondRows.Add(new SecondRow());
            await unitOfWork.CompleteAsync();
        }

        await using var verificationScope = provider.CreateAsyncScope();
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<FirstDbContext>().FirstRows.CountAsync());
        Assert.Equal(1, await verificationScope.ServiceProvider.GetRequiredService<SecondDbContext>().SecondRows.CountAsync());
    }

    [Fact]
    public async Task Tenant_switch_inside_one_unit_of_work_is_rejected()
    {
        var currentTenant = _services.GetRequiredService<ICurrentTenant>();
        var manager = _services.GetRequiredService<IUnitOfWorkManager>();
        var firstProvider = _services.GetRequiredService<IDbContextProvider<FirstDbContext>>();
        var secondProvider = _services.GetRequiredService<IDbContextProvider<SecondDbContext>>();

        using (currentTenant.Change(_tenantId))
        using (var unitOfWork = manager.Begin())
        {
            await firstProvider.GetDbContextAsync();

            using (currentTenant.Change(Guid.NewGuid()))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => secondProvider.GetDbContextAsync());
            }
        }
    }

    /// <summary>切换租户后复用同一个 DbContext 类型同样必须被拒绝。</summary>
    /// <remarks>
    /// 与上一条的区别在于走的是"命中已创建实例"的快路径：那条路径在解析连接之前就返回，
    /// 因此不经过 <c>Bind</c>。归属校验必须独立地放在快路径之前，否则同一个 DbContext
    /// 被复用时租户切换就检查不到——上一条用例走的是创建新 DbContext 的慢路径，覆盖不到这里。
    /// </remarks>
    [Fact]
    public async Task Tenant_switch_is_rejected_even_when_the_same_dbcontext_type_is_reused()
    {
        var currentTenant = _services.GetRequiredService<ICurrentTenant>();
        var manager = _services.GetRequiredService<IUnitOfWorkManager>();
        var firstProvider = _services.GetRequiredService<IDbContextProvider<FirstDbContext>>();

        using (currentTenant.Change(_tenantId))
        using (var unitOfWork = manager.Begin())
        {
            await firstProvider.GetDbContextAsync();

            using (currentTenant.Change(Guid.NewGuid()))
            {
                // 同一个类型，第二次获取会命中已创建实例
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstProvider.GetDbContextAsync());
            }
        }
    }

    [Fact]
    public async Task Physical_target_switch_inside_one_unit_of_work_is_rejected()
    {
        var currentTenant = _services.GetRequiredService<ICurrentTenant>();
        var manager = _services.GetRequiredService<IUnitOfWorkManager>();
        var firstProvider = _services.GetRequiredService<IDbContextProvider<FirstDbContext>>();
        var secondProvider = _services.GetRequiredService<IDbContextProvider<SecondDbContext>>();

        using (currentTenant.Change(_tenantId))
        using (var unitOfWork = manager.Begin())
        {
            await firstProvider.GetDbContextAsync();
            _resolver.ConnectionString = "Data Source=another-target;Mode=Memory;Cache=Shared";

            await Assert.ThrowsAsync<InvalidOperationException>(() => secondProvider.GetDbContextAsync());
        }
    }

    /// <summary>工作单元超时按整秒向上取整成命令超时：不足整秒的部分不能被截掉，更不能截成表示不限时的 0。</summary>
    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1500, 2)]
    [InlineData(30000, 30)]
    public async Task The_unit_of_work_timeout_becomes_the_command_timeout_rounded_up(int timeoutMilliseconds, int expectedSeconds)
    {
        var manager = _services.GetRequiredService<IUnitOfWorkManager>();
        var provider = _services.GetRequiredService<IDbContextProvider<FirstDbContext>>();

        using var unitOfWork = manager.Begin(
            new UnitOfWorkOptions { Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds) }, requiresNew: true);
        var dbContext = await provider.GetDbContextAsync();

        Assert.Equal(expectedSeconds, dbContext.Database.GetCommandTimeout());
    }

    [Fact]
    public async Task Dbcontext_declared_connection_string_name_is_passed_to_the_resolver()
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDbContextProvider<ControlDbContext>>();

        await provider.GetDbContextAsync();

        Assert.Equal("Control", _resolver.LastConnectionStringName);
    }

    [Fact]
    public async Task Non_relational_dbcontext_can_participate_in_a_unit_of_work()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<InMemoryDbContext>(options =>
            options.UseInMemoryDatabase($"uow-{Guid.NewGuid():N}"));
        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<IUnitOfWorkManager>();

        using var unitOfWork = manager.Begin(new UnitOfWorkOptions { IsTransactional = false });
        var scopedProvider = ((Leistd.UnitOfWork.DefaultUnitOfWork)unitOfWork).ServiceProvider
            .GetRequiredService<IDbContextProvider<InMemoryDbContext>>();
        (await scopedProvider.GetDbContextAsync()).Rows.Add(new FirstRow());

        await unitOfWork.CompleteAsync();
    }

    /// <summary>无解析器、两个 DbContext 分别配置到不同数据库时必须立即失败。</summary>
    /// <remarks>
    /// 不拦的话后果是静默写错库：第二个 DbContext 会收到第一个已绑定目标的连接，
    /// 宿主回调采纳 <c>ExistingConnection</c> 后，它本来配置的库根本不会被访问。
    /// 两个库恰好有同名表时不会报任何错——写入落到了调用方并未选择的库上。
    /// 有解析器时目标由解析结果定案，不走这条路径。
    /// </remarks>
    [Fact]
    public async Task Two_dbcontext_types_on_different_targets_without_a_resolver_fail_fast()
    {
        var connA = $"Data Source=different-a-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var connB = $"Data Source=different-b-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchorA = new SqliteConnection(connA);
        await anchorA.OpenAsync();
        await using var anchorB = new SqliteConnection(connB);
        await anchorB.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<FirstDbContext>((_, o) => ConfigureSqlite(o, connA));
        services.AddDbContext<SecondDbContext>((_, o) => ConfigureSqlite(o, connB));
        await using var provider = services.BuildServiceProvider();

        await using (var init = provider.CreateAsyncScope())
        {
            await init.ServiceProvider.GetRequiredService<FirstDbContext>().Database.EnsureCreatedAsync();
            await init.ServiceProvider.GetRequiredService<SecondDbContext>().Database.EnsureCreatedAsync();
        }

        var manager = provider.GetRequiredService<IUnitOfWorkManager>();
        using var unitOfWork = manager.Begin();
        var sp = ((Leistd.UnitOfWork.DefaultUnitOfWork)unitOfWork).ServiceProvider;
        (await sp.GetRequiredService<IDbContextProvider<FirstDbContext>>().GetDbContextAsync())
            .FirstRows.Add(new FirstRow());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sp.GetRequiredService<IDbContextProvider<SecondDbContext>>().GetDbContextAsync());
        Assert.Contains("different physical database", error.Message, StringComparison.Ordinal);
    }

    /// <summary>工作单元之外，改道后的连接不能静默复用作用域里已创建的 DbContext。</summary>
    /// <remarks>
    /// <c>AddDbContext</c> 默认 Scoped，首次解析后不再执行配置回调；切换物理库时不能复用先前的上下文。
    /// 改写连接解析结果模拟切到分库租户，覆盖工作单元之外的路径；工作单元内由另一用例覆盖。
    /// </remarks>
    [Fact]
    public async Task Outside_a_unit_of_work_a_rerouted_connection_is_rejected_instead_of_reusing_the_scoped_dbcontext()
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDbContextProvider<FirstDbContext>>();

        await provider.GetDbContextAsync();
        _resolver.ConnectionString = "Data Source=rerouted-target;Mode=Memory;Cache=Shared";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetDbContextAsync());
        Assert.Contains("unit of work", error.Message, StringComparison.Ordinal);
    }

    /// <summary>工作单元之外，连接未变时照旧复用作用域里的同一个 DbContext。</summary>
    /// <remarks>
    /// 校验只拦"改道"，不能误伤宿主侧大量本来就在工作单元之外的读取——
    /// 那些路径每次解析出的都是同一个连接。
    /// </remarks>
    [Fact]
    public async Task Outside_a_unit_of_work_an_unchanged_connection_keeps_reusing_the_scoped_dbcontext()
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDbContextProvider<FirstDbContext>>();

        var first = await provider.GetDbContextAsync();
        var second = await provider.GetDbContextAsync();

        Assert.Same(first, second);
    }

    private static void ConfigureSqlite(DbContextOptionsBuilder options, string fallbackConnectionString)
    {
        var creation = DbContextCreationContext.Current;
        if (creation?.ExistingConnection is SqliteConnection connection)
        {
            options.UseSqlite(connection);
        }
        else
        {
            options.UseSqlite(creation?.ConnectionString ?? fallbackConnectionString);
        }
    }

    private sealed class MutableResolver(string connectionString) : IConnectionStringResolver
    {
        public string ConnectionString { get; set; } = connectionString;
        public bool WasAwaited { get; private set; }
        public string? LastConnectionStringName { get; private set; }

        public async Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            WasAwaited = true;
            LastConnectionStringName = connectionStringName;
            return ConnectionString;
        }
    }

    private sealed class FirstDbContext(DbContextOptions<FirstDbContext> options) : DbContext(options)
    {
        public DbSet<FirstRow> FirstRows => Set<FirstRow>();
        public DbSet<SecondRow> SecondRows => Set<SecondRow>();
    }

    private sealed class SecondDbContext(DbContextOptions<SecondDbContext> options) : DbContext(options)
    {
        public DbSet<FirstRow> FirstRows => Set<FirstRow>();
        public DbSet<SecondRow> SecondRows => Set<SecondRow>();
    }

    [ConnectionStringName("Control")]
    private sealed class ControlDbContext(DbContextOptions<ControlDbContext> options) : DbContext(options);

    private sealed class InMemoryDbContext(DbContextOptions<InMemoryDbContext> options) : DbContext(options)
    {
        public DbSet<FirstRow> Rows => Set<FirstRow>();
    }

    private sealed class FirstRow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private sealed class SecondRow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
