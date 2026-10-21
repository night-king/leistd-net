using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>集成测试的 PostgreSQL：一次测试运行一个容器、迁移一次模板库，每个测试宿主克隆一份独立的库。</summary>
/// <remarks>
/// <para>迁移用与 DbMigrator 相同的上下文选项（迁移历史表、schema）。两边一旦不一致，宿主启动时的
/// 迁移校验会以"有待执行迁移"失败，不会静默放行。</para>
/// <para>克隆用 <c>CREATE DATABASE … TEMPLATE</c>，比每个宿主各跑一遍迁移快得多，各宿主的数据互不可见。
/// 克隆要求模板库上没有连接，因此迁移用不进连接池的连接。</para>
/// <para>容器关闭了 fsync：数据随容器丢弃，不需要落盘保证。容器由 Testcontainers 在测试进程退出后回收。</para>
/// </remarks>
internal static class PostgreSqlTestDatabase
{
    private const string TemplateDatabase = "integration_template";

    private static readonly Lazy<Task<string>> AdminConnectionString = new(StartAsync);

    /// <summary>从模板库克隆一个新库，返回它的连接串。</summary>
    public static string CreateDatabase()
    {
        // 宿主配置回调是同步的；在线程池上等待，避开测试框架同步上下文上的阻塞
        var admin = Task.Run(() => AdminConnectionString.Value).GetAwaiter().GetResult();
        var name = $"test_{Guid.NewGuid():N}";
        Execute(admin, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    /// <summary>删除 <see cref="CreateDatabase"/> 建出的库，释放容器里的连接与内存。</summary>
    public static void DropDatabase(string connectionString)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        Execute(AdminConnectionString.Value.GetAwaiter().GetResult(), $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    private static async Task<string> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off", "-c", "max_connections=500")
            .Build();
        await container.StartAsync();

        var admin = container.GetConnectionString();
        Execute(admin, $"CREATE DATABASE \"{TemplateDatabase}\"");
        await MigrateAsync(new NpgsqlConnectionStringBuilder(admin) { Database = TemplateDatabase, Pooling = false }.ConnectionString);
        return admin;
    }

    private static async Task MigrateAsync(string connectionString)
    {
#if (LocalIdentity && IncludeMultiTenancy)
        var controlOptions = new DbContextOptionsBuilder<IdentityControlDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(DatabaseSchema.ControlMigrationsHistoryTable, DatabaseSchema.Name))
            .Options;
        await using (var control = new IdentityControlDbContext(controlOptions, serviceProvider: null))
        {
            await control.Database.MigrateAsync();
        }
#endif
#if (OpenIddictServer)
        var openIddictOptions = new DbContextOptionsBuilder<OpenIddictDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(OpenIddictDbContext.MigrationsHistoryTable, DatabaseSchema.Name))
            .UseOpenIddict()
            .Options;
        await using (var openIddict = new OpenIddictDbContext(openIddictOptions))
        {
            await openIddict.Database.MigrateAsync();
        }
#endif
        var businessOptions = new DbContextOptionsBuilder<MyProjectDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(DatabaseSchema.BusinessMigrationsHistoryTable, DatabaseSchema.Name))
            .Options;
        await using var business = new MyProjectDbContext(businessOptions, serviceProvider: null);
        await business.Database.MigrateAsync();
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
