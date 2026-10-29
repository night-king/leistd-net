using System.Diagnostics.CodeAnalysis;
using Leistd.Data.Connections;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Leistd.UnitOfWork.EntityFrameworkCore.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.UnitOfWork.Tests.EntityFrameworkCore;

/// <summary>
/// 同一物理库上的第二个 DbContext 复用活动事务的连接时，物理目标沿用查到该事务时的键。
/// 有的提供程序在连接打开后改写上报的连接串：Npgsql 在 <c>Persist Security Info=false</c>（默认）时去掉 <c>Password</c>。
/// 按改写后的串重算目标，会把同一个库误判成换了库，两个 DbContext 就永远共用不了事务。
/// </summary>
public class ReusedConnectionTargetTests
{
    /// <summary>
    /// 两个 DbContext 类型共用一个连接与事务：完成时一起提交，未完成时一起回滚。有解析器与宿主自行配置连接两条路径都复用连接。
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Second_dbcontext_shares_the_reused_connection_even_if_its_reported_string_changed_after_opening(bool withResolver, bool complete)
    {
        var connectionString = NewDatabase();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var services = BuildServices(connectionString, withResolver, secondUsesExistingConnection: true);
        await CreateTablesAsync(services);

        var manager = services.GetRequiredService<IUnitOfWorkManager>();
        using (var unitOfWork = manager.Begin())
        {
            var first = await services.GetRequiredService<IDbContextProvider<FirstDbContext>>().GetDbContextAsync();
            first.FirstRows.Add(new FirstRow());
            var second = await services.GetRequiredService<IDbContextProvider<SecondDbContext>>().GetDbContextAsync();
            second.SecondRows.Add(new SecondRow());

            Assert.Same(first.Database.GetDbConnection(), second.Database.GetDbConnection());
            // 先写进当前事务，未完成时的 0 行才能证明回滚，而不是根本没写。
            await unitOfWork.SaveChangesAsync();
            if (complete)
            {
                await unitOfWork.CompleteAsync();
            }
        }

        await using var verification = services.CreateAsyncScope();
        var expected = complete ? 1 : 0;
        Assert.Equal(expected, await verification.ServiceProvider.GetRequiredService<FirstDbContext>().FirstRows.CountAsync());
        Assert.Equal(expected, await verification.ServiceProvider.GetRequiredService<SecondDbContext>().SecondRows.CountAsync());
    }

    /// <summary>
    /// 宿主回调没有采用传入的 <c>ExistingConnection</c>、另开了一个连接：它无法加入活动事务，提交会分裂成两个事务，在取上下文时就拒绝。
    /// </summary>
    [Fact]
    public async Task Host_callback_that_ignores_the_existing_connection_is_rejected()
    {
        var connectionString = NewDatabase();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var services = BuildServices(connectionString, withResolver: true, secondUsesExistingConnection: false);
        await CreateTablesAsync(services);

        using var unitOfWork = services.GetRequiredService<IUnitOfWorkManager>().Begin();
        await services.GetRequiredService<IDbContextProvider<FirstDbContext>>().GetDbContextAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.GetRequiredService<IDbContextProvider<SecondDbContext>>().GetDbContextAsync());
        Assert.Contains("ExistingConnection", error.Message, StringComparison.Ordinal);
    }

    // Default Timeout 只是一个不影响行为的键：打开后的连接把它从上报的串里去掉，模拟提供程序改写连接串
    private static string NewDatabase() => $"Data Source=reused-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=31";

    private static ServiceProvider BuildServices(string connectionString, bool withResolver, bool secondUsesExistingConnection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        if (withResolver)
        {
            services.AddSingleton<IConnectionStringResolver>(new FixedResolver(connectionString));
        }

        services.AddUnitOfWork();
        services.AddUnitOfWorkEfCore();
        services.AddDbContext<FirstDbContext>((_, options) => Configure(options, connectionString, useExistingConnection: true));
        services.AddDbContext<SecondDbContext>((_, options) => Configure(options, connectionString, secondUsesExistingConnection));
        return services.BuildServiceProvider();
    }

    private static async Task CreateTablesAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FirstDbContext>().Database.EnsureCreatedAsync();
    }

    private static void Configure(DbContextOptionsBuilder options, string configured, bool useExistingConnection)
    {
        var creation = DbContextCreationContext.Current;
        if (useExistingConnection && creation?.ExistingConnection is { } existing)
        {
            options.UseSqlite(existing);
            return;
        }

        options.UseSqlite(new RewritingConnection(creation?.ConnectionString ?? configured), contextOwnsConnection: true);
    }

    /// <summary>打开后上报的连接串被改写的连接（Npgsql 在 <c>Persist Security Info=false</c> 时去掉 <c>Password</c>）。</summary>
    private sealed class RewritingConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private bool _hasOpened;

        [AllowNull]
        public override string ConnectionString
        {
            get => _hasOpened ? Rewritten(base.ConnectionString) : base.ConnectionString;
            set => base.ConnectionString = value;
        }

        public override void Open()
        {
            base.Open();
            _hasOpened = true;
        }

        private static string Rewritten(string value)
        {
            var builder = new SqliteConnectionStringBuilder(value);
            builder.Remove("Default Timeout");
            return builder.ToString();
        }
    }

    private sealed class FixedResolver(string connectionString) : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default) => Task.FromResult(connectionString);
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

    private sealed class FirstRow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    private sealed class SecondRow
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
