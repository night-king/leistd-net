#if (LocalIdentity && IncludeMultiTenancy)
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CompanyName.ProjectName.Infrastructure.Persistence.DesignTime;

public sealed class IdentityControlDbContextFactory : IDesignTimeDbContextFactory<IdentityControlDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=companyname_projectname_design;Username=postgres;Password=postgres";

    public IdentityControlDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetControlPlaneConnectionString()
            ?? PlaceholderConnectionString;
        var options = new DbContextOptionsBuilder<IdentityControlDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable(
                    DatabaseSchema.ControlMigrationsHistoryTable, DatabaseSchema.Name))
            .Options;

        // 设计时工具（dotnet ef）没有 DI 容器也没有请求主体，不接审计
        return new IdentityControlDbContext(options, serviceProvider: null);
    }
}
#endif
