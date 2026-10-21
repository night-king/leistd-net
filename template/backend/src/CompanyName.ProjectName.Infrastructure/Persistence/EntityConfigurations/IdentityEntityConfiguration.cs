#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Users.Entities;
using Microsoft.EntityFrameworkCore;

namespace CompanyName.ProjectName.Infrastructure.Persistence.EntityConfigurations;

internal static class IdentityEntityConfiguration
{
    internal static void ConfigureIdentity(this ModelBuilder builder)
    {
        builder.ConfigureUserIdentity();
        builder.ConfigureRoles();
        builder.ConfigureUserSessions();
#if (ExternalLogin)
        builder.ConfigureExternalLoginConnections();
#endif
    }

    private static void ConfigureUserIdentity(this ModelBuilder builder)
    {
        builder.Entity<User>(b =>
        {
            b.Property(e => e.PasswordHash).HasMaxLength(256);
            b.Property(e => e.PhoneNumber).HasMaxLength(32);
            b.ComplexProperty(e => e.Lockout);
            b.ComplexProperty(e => e.LastLogin, login => login.Property(l => l.Ip).HasMaxLength(45));
            b.ComplexProperty(e => e.TwoFactor, twoFactor =>
            {
                twoFactor.Property(t => t.Secret).HasMaxLength(512);
                // 十个 SHA-256 十六进制摘要加分隔符
                twoFactor.Property(t => t.RecoveryCodes).HasMaxLength(1024);
            });
            // Guid 的 32 位十六进制
            b.Property(e => e.SecurityStamp).IsRequired().HasMaxLength(32);
        });
    }

    private static void ConfigureRoles(this ModelBuilder builder)
    {
        builder.Entity<Role>(b =>
        {
            b.Property(e => e.Name).IsRequired().HasMaxLength(64);
            b.Property(e => e.DisplayName).IsRequired().HasMaxLength(128);
            b.Property(e => e.Description).HasMaxLength(512);

            // 租户内唯一：每个租户拥有自己的 Admin/Member 角色。
            // 宿主行与租户行分别用带过滤的唯一索引（可空列直接进唯一索引时 NULL 互不相等）
            b.HasIndex(e => e.Name)
                .IsUnique()
                .HasFilter($"\"{nameof(Role.TenantId)}\" IS NULL");

            b.HasIndex(e => new { e.TenantId, e.Name })
                .IsUnique()
                .HasFilter($"\"{nameof(Role.TenantId)}\" IS NOT NULL");
        });
    }

    private static void ConfigureUserSessions(this ModelBuilder builder)
    {
        builder.Entity<UserSession>(b =>
        {
            b.Property(e => e.IpAddress).HasMaxLength(UserSession.IpAddressMaxLength);
            b.Property(e => e.UserAgent).HasMaxLength(UserSession.UserAgentMaxLength);
            b.Property(e => e.ImpersonatorName).HasMaxLength(UserSession.ImpersonatorNameMaxLength);

            b.HasIndex(e => e.UserId);

            // 会话没有独立于用户的意义：用户行被物理删除时一并删除
            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

#if (ExternalLogin)
    private static void ConfigureExternalLoginConnections(this ModelBuilder builder)
    {
        builder.Entity<ExternalLoginConnection>(b =>
        {
            b.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            b.Property(e => e.ProviderUserId).IsRequired().HasMaxLength(256);
            b.ComplexProperty(e => e.Profile, profile =>
            {
                profile.Property(p => p.AccountLabel).HasMaxLength(256);
                profile.Property(p => p.Email).HasMaxLength(256);
                profile.Property(p => p.AvatarUrl).HasMaxLength(1024);
            });

            // 租户内唯一：同一外部身份可在不同租户各自绑定。
            // 宿主行（TenantId 为 NULL）在 PostgreSQL/SQLite 中 NULL 互不相等，
            // 用带过滤的成对索引分别约束，避免宿主侧失去唯一性兜底。
            // 只约束未删除的行：解绑是软删除，留着的旧行不能挡住同一外部账号重新绑定
            b.HasIndex(e => new { e.Provider, e.ProviderUserId })
                .IsUnique()
                .HasFilter($"\"{nameof(ExternalLoginConnection.TenantId)}\" IS NULL AND NOT \"{nameof(ExternalLoginConnection.IsDeleted)}\"");

            b.HasIndex(e => new { e.TenantId, e.Provider, e.ProviderUserId })
                .IsUnique()
                .HasFilter($"\"{nameof(ExternalLoginConnection.TenantId)}\" IS NOT NULL AND NOT \"{nameof(ExternalLoginConnection.IsDeleted)}\"");
            b.HasIndex(e => e.UserId);

            b.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
#endif
}
#endif
