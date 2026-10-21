using CompanyName.ProjectName.Domain.Users.Entities;
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Entities;
#endif
using Leistd.Authorization.EntityFrameworkCore;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore;
#endif
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore.Entities;
#endif
using Leistd.Settings.EntityFrameworkCore;
using Leistd.Ddd.Infrastructure.Persistence;
#if (IncludeNotifications)
using Leistd.Notifications.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;
using CompanyName.ProjectName.Infrastructure.Persistence.EntityConfigurations;
using Leistd.BackgroundJobs.EntityFrameworkCore;
using Leistd.BackgroundJobs.EntityFrameworkCore.Entities;
using Leistd.Authorization.EntityFrameworkCore.Entities;

namespace CompanyName.ProjectName.Infrastructure.Persistence;

public class MyProjectDbContext(
    DbContextOptions<MyProjectDbContext> options,
    IServiceProvider? serviceProvider) : BaseDbContext(options, serviceProvider)
{
    // Users（始终存在）
    public DbSet<User> Users { get; set; } = null!;
    // Identity 角色模型
    public DbSet<Role> Roles { get; set; } = null!;
    // 角色成员关系是 User 聚合的子实体：声明 DbSet 只为表名走命名约定，它不是聚合根，不会得到仓储
    public DbSet<UserRole> UserRoles { get; set; } = null!;
#if (LocalIdentity)
    public DbSet<UserSession> UserSessions { get; set; } = null!;
#endif
#if (ExternalLogin)
    public DbSet<ExternalLoginConnection> ExternalLoginConnections { get; set; } = null!;
#endif
    public DbSet<PermissionGrantRecord> PermissionGrantRecords { get; set; } = null!;
#if (IncludeOperationRecords)
    // 声明 DbSet 只为表名走命名约定（未声明时 EF 取实体类名），查询一律经 IOperationRecordReader
    public DbSet<OperationRecord> OperationRecords { get; set; } = null!;

    // 到期归档表（操作记录组件的保留期任务写入）。它不实现 IMultiTenant，因此不受租户全局过滤器约束——
    // 归档作业逐库执行，套上过滤器会让它只搬走宿主那部分且不报错。
    public DbSet<OperationRecordArchive> OperationRecordArchives { get; set; } = null!;

#endif
    // 集群周期任务的完成水位：多副本同一时段只跑一次
    public DbSet<RecurringJobState> RecurringJobStates { get; set; } = null!;

    protected override void ConfigureModelConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(64);
    }

    /// <remarks>
    /// 改写 <c>ConfigureModel</c> 而非 <c>OnModelCreating</c>：基类已封闭后者，
    /// 以保证软删除与租户全局过滤器在本方法之后套用，覆盖这里经
    /// <c>Configure*</c> 才进入模型的实体（它们没有 DbSet 声明）。
    /// </remarks>
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DatabaseSchema.Name);

        // 基础实体配置（始终包含）
        modelBuilder.ConfigureBaseEntities();
#if (LocalIdentity)
        // 认证相关实体配置
        modelBuilder.ConfigureIdentity();
#endif
        // 权限授予实体配置
        modelBuilder.ConfigurePermissionAuthorization();
        // 设置值实体配置
        modelBuilder.ConfigureSettings();
#if (IncludeOperationRecords)
        // 操作记录与归档表
        modelBuilder.ConfigureOperationRecords();
#endif
        // 周期任务完成水位
        modelBuilder.ConfigureBackgroundJobs();
#if (IncludeNotifications)
        // 通知实体配置
        modelBuilder.ConfigureNotifications();
#endif
    }
}
