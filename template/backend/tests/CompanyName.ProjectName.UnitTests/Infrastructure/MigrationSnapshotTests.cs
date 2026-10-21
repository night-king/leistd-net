using CompanyName.ProjectName.Infrastructure.Persistence.DesignTime;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.UnitTests.Infrastructure;

/// <summary>当前模型与已提交的迁移快照一致：改了实体或框架配置却没有生成迁移时在这里失败。</summary>
/// <remarks>
/// <para>这类漂移在真实库执行 <c>Migrate</c> 时才抛 <c>PendingModelChangesWarning</c>
/// （集成测试准备模板库时也会撞上，但要先起数据库容器）。这里用设计时工厂按关系型
/// Provider 构建模型并与快照比对，不连接数据库，几毫秒给出结论。</para>
/// <para>结论能代表运行时，前提是设计时工厂与运行时的 Npgsql 配置在模型层面一致；
/// 运行时若加了影响模型的选项，须同步到设计时工厂。</para>
/// <para>OIDC 存储上下文不在此检查：其实体由 <c>UseOpenIddict()</c> 动态注入、不在快照中，
/// 注册处已抑制模型差异警告（见 Infrastructure 的 <c>DependencyInjection</c>）。</para>
/// </remarks>
public sealed class MigrationSnapshotTests
{
    [Fact]
    public void Business_model_matches_its_migration_snapshot()
    {
        using var context = new MyProjectDbContextFactory().CreateDbContext([]);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The MyProjectDbContext model differs from the migration snapshot; add a migration (see the database migration section of backend/README.md).");
    }
#if (LocalIdentity && IncludeMultiTenancy)

    [Fact]
    public void Control_plane_model_matches_its_migration_snapshot()
    {
        using var context = new IdentityControlDbContextFactory().CreateDbContext([]);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The IdentityControlDbContext model differs from the migration snapshot; add a migration (see the database migration section of backend/README.md).");
    }
#endif
}
