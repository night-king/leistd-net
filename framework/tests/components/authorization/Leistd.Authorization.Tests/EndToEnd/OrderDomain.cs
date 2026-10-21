using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Leistd.MultiTenancy.Tenancy;
using System.Linq.Expressions;
using Leistd.Authorization.EntityFrameworkCore;
using Leistd.Authorization.Resource.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Subjects;
using Leistd.Authorization.Resource.Abstractions;
using Leistd.Authorization.DataScope.Abstractions;

namespace Leistd.Authorization.Tests.EndToEnd;

/// <summary>被测业务领域：一个带所有者、组织和状态的订单。</summary>
/// <remarks>
/// 这是三层授权各自都能作用到的最小形状：所有者用于资源规则、组织用于数据范围、
/// 状态用于"规则拒绝优先于 ACL 允许"。
/// </remarks>
public class Order : IAuthorizableResource
{
    public const string Resource = "Orders";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>与 ACL 一致的字符串资源 Key，使集合查询可被数据库翻译。</summary>
    public string ResourceKey { get; set; } = default!;

    public string OwnerId { get; set; } = default!;

    public string OrganizationId { get; set; } = default!;

    public string Code { get; set; } = default!;

    public bool IsArchived { get; set; }

    string IAuthorizableResource.ResourceName => Resource;
}

public class PipelineDbContext(DbContextOptions<PipelineDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigurePermissionAuthorization();
        modelBuilder.ConfigureResourceAuthorization();

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OwnerId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ResourceKey).IsUnique();
        });
    }
}

/// <summary>本次流水线用到的权限名。</summary>
public static class OrderPermissions
{
    public const string Default = "Orders";
    public const string Read = "Orders.Read";
    public const string Update = "Orders.Update";
    public const string Export = "Orders.Export";

    /// <summary>宿主会为它显式注册一个更严格的同名策略，用于验证策略优先级。</summary>
    public const string Approve = "Orders.Approve";

    /// <summary>
    /// "任一满足"策略：导出报表既对导出人开放，也对编辑人开放。
    /// 用来验证多权限策略名真的接上了检查器。
    /// </summary>
    public const string ExportOrUpdate = Export + "|" + Update;
}

public sealed class OrderPermissionDefinitionProvider : IPermissionDefinitionProvider
{
    public void Define(IPermissionDefinitionContext context)
    {
        var group = context.GetOrAddGroup("Orders", "订单");
        var orders = group.AddPermission(OrderPermissions.Default, MultiTenancySides.Both, "订单管理");
        orders.AddChild(OrderPermissions.Read, "查看订单");
        orders.AddChild(OrderPermissions.Update, "编辑订单");
        orders.AddChild(OrderPermissions.Approve, "审批订单");
        orders.AddChild(OrderPermissions.Export, "导出订单");
    }
}

/// <summary>只允许本人的订单。</summary>
public sealed class OwnOrderScopeProvider : IDataScopeProvider<Order>
{
    public const string Scope = "Own";

    public string ResourceName => Order.Resource;
    public string ScopeName => Scope;

    public ValueTask<Expression<Func<Order, bool>>> BuildPredicateAsync(
        DataScopeContext context,
        CancellationToken cancellationToken = default)
    {
        var userId = context.Subject.UserId;
        return ValueTask.FromResult<Expression<Func<Order, bool>>>(order => order.OwnerId == userId);
    }
}

/// <summary>允许被分配的组织下的订单。</summary>
public sealed class OrganizationOrderScopeProvider : IDataScopeProvider<Order>
{
    public const string Scope = "Organization";

    public string ResourceName => Order.Resource;
    public string ScopeName => Scope;

    public ValueTask<Expression<Func<Order, bool>>> BuildPredicateAsync(
        DataScopeContext context,
        CancellationToken cancellationToken = default)
    {
        var organizationIds = context.Assignments
            .Where(x => x.ScopeName == Scope && !string.IsNullOrWhiteSpace(x.ScopeValue))
            .Select(x => x.ScopeValue!)
            .ToList();

        return ValueTask.FromResult<Expression<Func<Order, bool>>>(
            order => organizationIds.Contains(order.OrganizationId));
    }
}

/// <summary>所有者可以操作自己的订单。官方资源型处理器，主体取被授权的 <c>context.User</c>。</summary>
public sealed class OrderOwnerHandler(IPermissionSubjectProvider subjects)
    : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Order resource)
    {
        var subject = await subjects.GetSubjectAsync(context.User);
        if (subject is not null && resource.OwnerId == subject.UserId)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>已归档的订单一律不可修改——领域规则的拒绝优先于任何 ACL 允许，超级管理员也不例外。</summary>
public sealed class ArchivedOrderHandler : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OperationAuthorizationRequirement requirement,
        Order resource)
    {
        if (requirement.Name == ResourceOperations.Update && resource.IsArchived)
        {
            context.Fail();
        }

        return Task.CompletedTask;
    }
}
