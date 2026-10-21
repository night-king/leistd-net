using Leistd.Data.Connections;
#if (OpenIddictServer)
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence;

/// <summary>OpenIddict 存储（应用、授权、作用域、令牌）。与租户控制面同库，但独立上下文。</summary>
/// <remarks>
/// 该上下文随令牌签发能力独立裁剪，与本地身份控制面共享物理库但使用独立迁移历史。
/// OpenIddict 通过 <c>UseOpenIddict()</c> 注入实体，因此本类不声明 <c>DbSet</c>，
/// 仅此上下文抑制 <c>PendingModelChangesWarning</c>。它是宿主级存储，不参与租户路由。
/// </remarks>
[ConnectionStringName("IdentityControl")]
public sealed class OpenIddictDbContext(DbContextOptions<OpenIddictDbContext> options)
    : DbContext(options)
{
    /// <summary>本上下文的迁移历史表名。同库多上下文必须各用一张，否则互相误判已执行</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_OpenIddict";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(DatabaseSchema.Name);
    }
}
#endif
