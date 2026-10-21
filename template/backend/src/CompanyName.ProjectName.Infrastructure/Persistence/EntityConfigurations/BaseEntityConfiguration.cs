using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence.EntityConfigurations;

/// <summary>User 聚合基础配置（始终包含）。</summary>
internal static class BaseEntityConfiguration
{
    internal static void ConfigureBaseEntities(this ModelBuilder builder)
    {
        builder.ConfigureUser();
        builder.ConfigureUserRoles();
    }

    private static void ConfigureUser(this ModelBuilder builder)
    {
        builder.Entity<User>(b =>
        {
            b.Property(e => e.Username).IsRequired().HasMaxLength(UsernameRules.MaxLength);
            b.Property(e => e.Email).IsRequired().HasMaxLength(256);
            // 头像可以是站内上传的 data URL，不设长度即各数据库的大文本类型
            b.Property(e => e.Avatar);
            b.Property(e => e.DisplayName).HasMaxLength(128);
            b.Property(e => e.IsSuperAdmin);

            // 宿主专属不变量：IsSuperAdmin 只能出现在宿主行（为什么见 CreateSuperAdminAsync）。
            // 领域服务已在创建时拒绝，这一道是最终完整性——数据修复脚本、批量导入、
            // 直接 SQL 都不经过领域入口。
            b.ToTable(t => t.HasCheckConstraint(
                UserCheckConstraints.SuperAdminIsHostOnly,
                $"NOT (\"{nameof(User.IsSuperAdmin)}\" AND \"{nameof(User.TenantId)}\" IS NOT NULL)"));

            // 租户内唯一：跨租户允许同名/同邮箱。可空 TenantId 直接进唯一索引时，
            // PostgreSQL/SQLite 均视 NULL 互不相等，宿主行会失去唯一性兜底，
            // 因此宿主行与租户行分别用带过滤的唯一索引收口
            b.HasIndex(e => e.Username)
                .IsUnique()
                .HasFilter($"\"{nameof(User.TenantId)}\" IS NULL");
            // 邮箱可缺席：资源服务形态下它来自签发方令牌，令牌没有 email 声明时存空串。
            // 空串不参与唯一性——否则同一租户里第二个没有邮箱的用户就撞键，
            // 而那是一次正常的投影，不是重复数据。本地身份形态下注册必填邮箱，这个过滤条件恒真。
            b.HasIndex(e => e.Email)
                .IsUnique()
                .HasFilter($"\"{nameof(User.TenantId)}\" IS NULL AND \"{nameof(User.Email)}\" <> ''");

            b.HasIndex(e => new { e.TenantId, e.Username })
                .IsUnique()
                .HasFilter($"\"{nameof(User.TenantId)}\" IS NOT NULL");
            b.HasIndex(e => new { e.TenantId, e.Email })
                .IsUnique()
                .HasFilter($"\"{nameof(User.TenantId)}\" IS NOT NULL AND \"{nameof(User.Email)}\" <> ''");
        });
    }

    // 角色成员关系是 User 聚合的子实体：经 User.Roles 一对多映射，按 RoleId 引用角色，不设导航。
    // 两端都是软删除且带全局查询过滤器：关系声明为可选、删除为限制，主体被过滤或误删时不会连带吞掉成员关系
    private static void ConfigureUserRoles(this ModelBuilder builder)
    {
        builder.Entity<User>().HasMany(user => user.Roles).WithOne().HasForeignKey(userRole => userRole.UserId)
            .OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        builder.Entity<UserRole>(b =>
        {
            // 主键由构造函数生成。按约定标为"添加时生成"的话，经 User.Roles 发现的新成员关系带着非默认主键，
            // EF 会当成已有行去 UPDATE（影响 0 行即并发异常）；标为不生成才按新增插入
            b.Property(userRole => userRole.Id).ValueGeneratedNever();
            // 同一用户对同一角色只有一条有效成员关系；撤销是软删除，历史行不参与
            b.HasIndex(e => new { e.UserId, e.RoleId })
                .IsUnique()
                .HasFilter($"NOT \"{nameof(UserRole.IsDeleted)}\"");
            b.HasOne<Role>().WithMany().HasForeignKey(userRole => userRole.RoleId)
                .OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        });
    }
}
