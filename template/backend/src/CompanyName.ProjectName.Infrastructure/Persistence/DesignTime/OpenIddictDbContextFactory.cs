#if (OpenIddictServer)
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CompanyName.ProjectName.Infrastructure.Persistence.DesignTime;

/// <summary>设计时工厂：<c>dotnet ef</c> 生成 OIDC 存储迁移时使用</summary>
public sealed class OpenIddictDbContextFactory : IDesignTimeDbContextFactory<OpenIddictDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=companyname_projectname_design;Username=postgres;Password=postgres";

    public OpenIddictDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetControlPlaneConnectionString()
            ?? PlaceholderConnectionString;

        var options = new DbContextOptionsBuilder<OpenIddictDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    OpenIddictDbContext.MigrationsHistoryTable, DatabaseSchema.Name))
            .UseOpenIddict()
            .Options;

        return new OpenIddictDbContext(options);
    }
}
#endif
