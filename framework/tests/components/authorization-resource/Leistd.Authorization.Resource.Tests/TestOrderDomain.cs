using System.Security.Claims;
using Leistd.Authorization.Resource.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Resource.Abstractions;

namespace Leistd.Authorization.Resource.Tests;

/// <summary>测试用最小业务领域：一个带所有者与组织的订单。</summary>
/// <remarks>
/// 原语的正确性风险不在代码量，而在没有真实消费者。用一个真实存在的实体在关系型数据库上
/// 跑通"实例判定"与"把 ACL 合并进集合查询"，才能证明契约在实际用法下成立。
/// </remarks>
public class TestOrder : IAuthorizableResource
{
    public const string Resource = "Orders";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>与 ACL 一致的字符串资源 Key，保证集合查询可被数据库翻译。</summary>
    public string ResourceKey { get; set; } = default!;

    public string OwnerId { get; set; } = default!;

    public string OrganizationId { get; set; } = default!;

    public string Title { get; set; } = default!;

    string IAuthorizableResource.ResourceName => Resource;
}

public class TestOrderDbContext(DbContextOptions<TestOrderDbContext> options) : DbContext(options)
{
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureResourceAuthorization();

        modelBuilder.Entity<TestOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OwnerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(256).IsRequired();
            entity.HasIndex(x => x.ResourceKey).IsUnique();
        });
    }
}

internal sealed class FakeSubjectProvider(PermissionSubject? subject) : IPermissionSubjectProvider
{
    public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(subject);

    public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        => Task.FromResult(subject);
}

/// <summary>按主体的 <c>sub</c> 给出主体：用于验证判定针对被授权的主体而不是当前用户。</summary>
internal sealed class ClaimSubjectProvider : IPermissionSubjectProvider
{
    public Task<PermissionSubject?> GetCurrentSubjectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<PermissionSubject?>(null);

    public Task<PermissionSubject?> GetSubjectAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        => Task.FromResult(principal.FindFirst("sub")?.Value is { } userId
            ? new PermissionSubject(userId, [], IsSuperAdmin: false)
            : null);
}

internal sealed class FixedPrincipalAccessor(ClaimsPrincipal? principal) : Leistd.Security.Claims.ICurrentPrincipalAccessor
{
    public ClaimsPrincipal? Principal { get; } = principal;

    public IDisposable Change(ClaimsPrincipal principal) => throw new NotSupportedException();
}
