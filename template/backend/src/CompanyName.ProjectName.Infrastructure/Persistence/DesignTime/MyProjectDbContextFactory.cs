using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Leistd.Data.Connections;

namespace CompanyName.ProjectName.Infrastructure.Persistence.DesignTime;

/// <summary>设计时 DbContext 工厂。</summary>
/// <remarks>
/// 仅供 EF Core 工具（<c>dotnet ef migrations add</c> / <c>dotnet ef dbcontext</c>）在设计时构建模型使用，
/// 不参与运行时（运行时由 DI 注册的 <c>AddDbContext</c> 创建）。
/// 放在 Api（启动项目）下，避免 EF 工具走 Program.cs 主机构建路径而触发启动副作用（自动迁移、种子数据等）。
///
/// 连接字符串优先读取 <c>ConnectionStrings:Default</c>；取不到时回退占位连接串——
/// 生成迁移只需构建模型、并不实际连库，因此未配置数据库的环境（如 CI）也能成功生成迁移。
/// </remarks>
public sealed class MyProjectDbContextFactory : IDesignTimeDbContextFactory<MyProjectDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=companyname_projectname_design;Username=postgres;Password=postgres";

    public MyProjectDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.Default);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = PlaceholderConnectionString;
        }

        var optionsBuilder = new DbContextOptionsBuilder<MyProjectDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    DatabaseSchema.BusinessMigrationsHistoryTable, DatabaseSchema.Name));

        // 设计时无需运行时服务（审计/软删除过滤等仅在 SaveChanges 生效），serviceProvider 传 null。
        return new MyProjectDbContext(optionsBuilder.Options, serviceProvider: null);
    }
}
