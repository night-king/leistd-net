using System.Security.Claims;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.AmbientContext;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
using Leistd.Data.Connections;
using Leistd.Ddd.Domain.Repositories;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.ConnectionStrings;
#endif
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.DbMigrator.Runners;

/// <summary>迁移之后给每个 Admin 角色补上所在侧别当前可用、却还没有的权限：宿主 Admin 补宿主侧，租户 Admin 补租户侧。</summary>
/// <remarks>
/// <para>Admin 只增不减：从 Admin 撤掉的权限下次迁移会补回，要细分权限用自定义角色。已有授予不动。</para>
/// <para>先逐个物理业务库（默认库与各独立库）读出未删除的 Admin 角色属于哪些租户，再逐个切到该租户、按正式路由写入，
/// 与 API 写授予走同一条路径。与在线编辑的并发由授权版本保证（点操作冲突重试），不另加锁。</para>
/// <para>每个 Admin 一个工作单元；一个租户出错只记在它名下，其余照常补齐，作业以失败结束。</para>
/// </remarks>
public sealed class AdminGrantTopUpRunner(
    IConfiguration configuration,
#if (IncludeMultiTenancy)
    ITenantMigrationTargetProvider targetProvider,
#endif
    ICurrentTenant currentTenant,
    IAmbientContext ambientContext,
    IUnitOfWorkManager unitOfWorkManager,
    IRepository<Role, Guid> roleRepository,
    IPermissionGrantSeeder seeder,
    ILogger<AdminGrantTopUpRunner> logger)
{
    /// <summary>一个 Admin 角色的补齐结果。</summary>
    /// <param name="TenantId">所属租户；宿主为 <see langword="null"/>。</param>
    /// <param name="Permissions">补授（预演时为将补授）的权限名。</param>
    public sealed record AdminTopUp(Guid? TenantId, IReadOnlyList<string> Permissions);

    /// <summary>补齐失败的 Admin。</summary>
    /// <param name="TenantId">所属租户；宿主为 <see langword="null"/>。</param>
    /// <param name="Reason">异常类型与消息；不含连接串。</param>
    public sealed record FailedTopUp(Guid? TenantId, string Reason);

    /// <summary>全部 Admin 的补齐报告。</summary>
    /// <param name="Admins">检查过的 Admin，含不需要补的。</param>
    /// <param name="Failures">补齐失败的 Admin。</param>
    public sealed record TopUpReport(IReadOnlyList<AdminTopUp> Admins, IReadOnlyList<FailedTopUp> Failures)
    {
        /// <summary>全部 Admin 都已检查或补齐。</summary>
        public bool Succeeded => Failures.Count == 0;
    }

    /// <summary>检查并按 <paramref name="apply"/> 补齐每个 Admin 角色。</summary>
    /// <param name="apply"><see langword="false"/> 时只列出缺口，不写入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<TopUpReport> RunAsync(bool apply, CancellationToken cancellationToken = default)
    {
        using var actorScope = ambientContext.Begin(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", $"deployment:{Environment.UserName}@{Environment.MachineName}")], "deployment")),
            correlationId: Guid.NewGuid().ToString("N"));

        var admins = new List<AdminTopUp>();
        var failures = new List<FailedTopUp>();
        foreach (var tenantId in await AdminTenantsAsync(cancellationToken))
        {
            try
            {
                admins.Add(await TopUpAsync(tenantId, apply, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Topping up the {RoleName} role of tenant {TenantId} failed; continuing with the remaining roles.",
                    AdminConstant.RoleName, tenantId?.ToString() ?? "host");
                failures.Add(new FailedTopUp(tenantId, $"{exception.GetType().Name}: {exception.Message}"));
            }
        }

        return new TopUpReport(admins, failures);
    }

    private async Task<AdminTopUp> TopUpAsync(Guid? tenantId, bool apply, CancellationToken cancellationToken)
    {
        using var tenantScope = currentTenant.Change(tenantId);
        using var uow = unitOfWorkManager.Begin(requiresNew: true);
        var adminRole = await roleRepository.GetFirstAsync(r => r.Name == AdminConstant.RoleName, cancellationToken: cancellationToken);
        if (adminRole is null)
        {
            // 列出时还在、按路由却找不到：租户已迁到别的库，那个库会单独列出
            return new AdminTopUp(tenantId, []);
        }

        var side = tenantId.HasValue ? MultiTenancySides.Tenant : MultiTenancySides.Host;
        var key = adminRole.Id.ToString();
        var permissions = apply
            ? await seeder.TopUpAsync(PermissionGrantProviderNames.Role, key, side, cancellationToken)
            : await seeder.GetMissingAsync(PermissionGrantProviderNames.Role, key, side, cancellationToken);
        await uow.CompleteAsync(cancellationToken);

        if (apply && permissions.Count > 0)
        {
            logger.LogInformation("Granted {Count} missing permission(s) to the {RoleName} role of tenant {TenantId}: {Permissions}",
                permissions.Count, AdminConstant.RoleName, tenantId?.ToString() ?? "host", string.Join(", ", permissions));
        }

        return new AdminTopUp(tenantId, permissions);
    }

    // 未删除的 Admin 角色属于哪些租户（宿主为 null），按物理库读：共享库里有宿主与多个租户，独立库里有共用它的租户
    private async Task<IReadOnlyList<Guid?>> AdminTenantsAsync(CancellationToken cancellationToken)
    {
        var connections = new List<string>();
        var defaultConnection = configuration.GetConnectionString(ConnectionStringNames.Default)
            ?? throw new InvalidOperationException("ConnectionStrings:Default is required by DbMigrator.");
        connections.Add(defaultConnection);
#if (IncludeMultiTenancy)
        // 迁移已成功走过同一份清单；这里只取连接，失败的租户迁移阶段已经报过
        var targets = await targetProvider.GetDedicatedTargetsAsync(
            DatabaseMigrationRunner.BusinessConnectionStringName, cancellationToken);
        connections.AddRange(targets.Targets.Select(target => target.ConnectionString));
#endif

        var tenants = new List<Guid?>();
        foreach (var connection in connections)
        {
            await using var dbContext = DatabaseMigrationRunner.CreateBusinessContext(connection);
            var found = await dbContext.Set<Role>()
                .IgnoreQueryFilters()
                .Where(role => role.Name == AdminConstant.RoleName && !role.IsDeleted)
                .Select(role => role.TenantId)
                .ToListAsync(cancellationToken);
            tenants.AddRange(found);
        }

        // 宿主在前，其余按租户 Id 排序，输出稳定
        return [.. tenants.Distinct().OrderBy(id => id.HasValue).ThenBy(id => id)];
    }
}
