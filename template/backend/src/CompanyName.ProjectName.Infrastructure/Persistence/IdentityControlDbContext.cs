using Leistd.Auditing.EntityFrameworkCore.Extensions;
using Leistd.Data.Connections;
#if (LocalIdentity && IncludeMultiTenancy)
using Leistd.Auditing.EntityFrameworkCore.Conventions;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence;

/// <summary>
/// Identity control-plane storage. This context is always pinned to the host database.
/// Tenant business data belongs to <see cref="MyProjectDbContext"/>.
/// </summary>
/// <remarks>
/// <para>刻意不继承 <c>BaseDbContext</c>：控制面数据是宿主侧的，不参与租户查询过滤——
/// 租户注册表若被租户过滤器作用，解析租户就要先知道租户，形成循环。</para>
/// <para>代价是拿不到基座在"进入跟踪"时落创建审计的钩子，而
/// <c>TenantConnectionRecord</c> 恰恰是全系统最敏感的一行、必须能回答"谁改的"。
/// 因此这里显式接上同一个原语（<c>ChangeTracker.EnableCreationAuditing</c>），
/// 只落创建审计、不落租户归属——修改审计由宿主的
/// <c>AuditSaveChangesInterceptor</c> 在保存时处理。</para>
/// </remarks>
[ConnectionStringName(ConnectionStringName)]
public sealed class IdentityControlDbContext : DbContext
{
    public const string ConnectionStringName = "IdentityControl";

    /// <param name="options">上下文选项</param>
    /// <param name="serviceProvider">
    /// 用于解析审计设置器。<see langword="null"/> 时不落创建审计——DbMigrator 直接
    /// <c>new</c> 出本上下文只为算迁移，没有 DI 容器也没有请求主体。
    /// 形参刻意不给默认值：与 <see cref="MyProjectDbContext"/> 同型，让"不接审计"
    /// 成为调用点上写明的选择，而不是漏传参数的默认结果
    /// </param>
    public IdentityControlDbContext(
        DbContextOptions<IdentityControlDbContext> options,
        IServiceProvider? serviceProvider) : base(options)
    {
        ChangeTracker.EnableCreationAuditing(serviceProvider);
    }

    // 普通上下文只登记审计约定。
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new AuditingEntityConvention());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(DatabaseSchema.Name);
        modelBuilder.ConfigureMultiTenancy();
    }
}
#endif
