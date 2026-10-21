#if (LocalIdentity)
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>控制面的连接遵循 <c>IdentityControl → Default</c> 回落链：未单独配置时与业务同库，配置后切到自己的库，业务不跟着动。</summary>
/// <remarks>
/// <para><c>ControlPlaneModelSeparationTests</c> 验的是"哪个实体属于哪个上下文"。这条验的是运行期 DI 实际连到哪：
/// 把控制面上下文直接接到默认连接，模型边界一字未改、单库部署照常可用，但"控制库拆到独立实例"从此失效。</para>
/// <para>DbMigrator 按同一条回落链找控制面目标；两边一致由宿主启动时的迁移校验兜住。</para>
/// </remarks>
public sealed class ControlPlaneStoreSeparationTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    [Fact]
    public void Control_plane_shares_the_default_database_until_configured_separately()
    {
        using var scope = factory.Services.CreateScope();

        Assert.Equal(DatabaseOf<MyProjectDbContext>(scope), DatabaseOf<IdentityControlDbContext>(scope));
    }

    [Fact]
    public void A_configured_control_connection_moves_only_the_control_plane()
    {
        var controlDatabase = new NpgsqlConnectionStringBuilder(PostgreSqlTestDatabase.CreateDatabase());
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting(
            $"ConnectionStrings:{IdentityControlDbContext.ConnectionStringName}", controlDatabase.ConnectionString));
        using var scope = host.Services.CreateScope();

        var control = DatabaseOf<IdentityControlDbContext>(scope);
        Assert.Equal(controlDatabase.Database, control);
        Assert.NotEqual(control, DatabaseOf<MyProjectDbContext>(scope));
#if (OpenIddictServer)
        // OIDC 存储与控制面同库
        Assert.Equal(control, DatabaseOf<OpenIddictDbContext>(scope));
#endif
    }

    private static string? DatabaseOf<TContext>(IServiceScope scope) where TContext : DbContext =>
        new NpgsqlConnectionStringBuilder(
            scope.ServiceProvider.GetRequiredService<TContext>().Database.GetConnectionString()).Database;
}
#endif
