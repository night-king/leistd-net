using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Entities.Auditing;
using Leistd.Ddd.Infrastructure.Persistence;
using Leistd.UnitOfWork.Options;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Leistd.MultiTenancy;
using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Ddd.Infrastructure.Tests.TestDoubles;

/// <summary>过滤器与落值测试的共享基础设施：多租户 + 软删除的双过滤器实体、BaseDbContext 宿主、最小服务图。</summary>
internal class TestOrder : FullAuditedEntity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public string Title { get; set; } = string.Empty;

    public TestOrder()
    {
        Id = Guid.CreateVersion7();
    }

    /// <summary>直接置软删标记（审计基类的 IsDeleted 是 protected set）。</summary>
    public void MarkDeleted() => IsDeleted = true;
}

/// <summary>
/// 只经 <c>ApplyConfiguration</c> 进入模型的租户化实体（没有 DbSet 声明），
/// 用于验证全局过滤器在派生类配置之后套用。
/// </summary>
internal class TestLedgerEntry : IMultiTenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid? TenantId { get; set; }

    public string Memo { get; set; } = string.Empty;
}

internal class TestLedgerEntryConfiguration : IEntityTypeConfiguration<TestLedgerEntry>
{
    public void Configure(EntityTypeBuilder<TestLedgerEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Memo).HasMaxLength(128);
    }
}

internal class TestFilterDbContext(DbContextOptions options, IServiceProvider? serviceProvider)
    : BaseDbContext(options, serviceProvider)
{
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestOrder>().Property(x => x.Title).HasMaxLength(128);

        // 无 DbSet 声明、仅经 ApplyConfiguration 进入模型的实体：
        // 过滤器必须同样覆盖它，否则"没有 DbSet 就不受隔离"会成为静默缺口
        modelBuilder.ApplyConfiguration(new TestLedgerEntryConfiguration());
    }
}

/// <summary>无环境工作单元的管理器（仓储立即 SaveChanges 路径）。</summary>
internal sealed class NullUnitOfWorkManager : IUnitOfWorkManager
{
    public IUnitOfWork? Current => null;

    public IUnitOfWork Begin(UnitOfWorkOptions? options = null, bool requiresNew = false)
        => throw new NotSupportedException("The tests do not use a unit of work.");
}

internal static class FilterTestServices
{
    /// <summary>最小服务图：租户上下文 + 数据过滤开关（与 AddDddInfrastructure 的注册形态一致）。</summary>
    public static ServiceProvider Create()
    {
        var services = new ServiceCollection();
        services.AddMultiTenancyCore();
        services.AddSingleton<IDataFilter, DataFilter>();
        services.AddScoped(typeof(IDataFilter<>), typeof(DataFilter<>));
        return services.BuildServiceProvider();
    }
}
