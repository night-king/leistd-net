using System.Security.Claims;
using CompanyName.ProjectName.Application.Initialization;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using Leistd.AmbientContext;
using Leistd.Auditing.Abstractions;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
#if (IncludeMultiTenancy)
using Leistd.Data.Connections;
using Leistd.MultiTenancy.ConnectionStrings;
#endif
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.DbMigrator.Runners;

/// <summary>资源服务首位管理员引导：把远端主体加入 Administrator 角色；部署权限是依据，不伪造自然人身份。</summary>
/// <remarks>
/// <para>授权载体是角色，不是给这个人的直接授予：首位管理员与之后的管理员一样由角色管理，
/// 调整 Administrator 的权限、移除其成员关系都能收回。直接授予在本服务没有管理入口，收不回。</para>
/// <para>本地还没有这个主体（远端用户尚未访问过）时，先建只含主体标识的最小用户行，
/// 首次真实访问时按令牌补齐资料。</para>
/// <para>普通引导只做一次：这个人曾被移出 Administrator（成员关系的软删除历史）就不再加回，
/// 重跑命令不会推翻之后的撤销；被停用、被删除的也原样保留。</para>
/// <para>显式恢复模式（<c>--restore</c>）用于没有其他可授权管理员时把人请回来：撤回删除、重新启用、
/// 补回 Admin 成员关系（只有这一条，别的角色不复活），再经 <see cref="IPermissionGrantManager"/> 把当前侧别可用的权限
/// 补齐给 Admin 角色。Admin 是共享角色，补齐对它的全部成员生效；不恢复已作废的会话与令牌。</para>
/// </remarks>
internal sealed class ResourceAdminBootstrapRunner(
    ICurrentTenant currentTenant,
    IAmbientContext ambientContext,
#if (IncludeMultiTenancy)
    IConnectionStringResolver connectionResolver,
    ITenantConnectionConfigurationStore tenantConnections,
#endif
    IUnitOfWorkManager unitOfWorkManager,
    IDistributedLock distributedLock,
    IDataFilter dataFilter,
    IRepository<Role, Guid> roleRepository,
    IUserRepository userRepository,
    UserDomainService userDomainService,
    IPermissionDefinitionManager definitions,
    IPermissionGrantStore grants,
    IPermissionGrantManager grantManager,
    ISystemInitializer initializer,
    IOperationRecorder recorder)
{
    public async Task<string> RunAsync(Guid subject, Guid? tenant, bool apply, bool restore, CancellationToken cancellationToken = default)
    {
#if (IncludeMultiTenancy)
        if (tenant is { } tenantId)
        {
            var lookup = await tenantConnections.FindAsync(tenantId, ConnectionStringNames.Default, cancellationToken)
                ?? throw new InvalidOperationException($"Tenant '{tenantId}' does not exist.");
            if (lookup.TenantId != tenantId)
                throw new InvalidOperationException("The control plane returned a different tenant.");
        }
#else
        if (tenant.HasValue) throw new InvalidOperationException("This application only accepts the host scope.");
#endif
        using var tenantScope = currentTenant.Change(tenant);
#if (IncludeMultiTenancy)
        // 先解析实际落点；不会打印含凭据的连接串。缺失路由与回源错误必须失败。
        _ = await connectionResolver.ResolveAsync(ConnectionStringNames.Default, cancellationToken);
#endif
        using var actorScope = ambientContext.Begin(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", $"deployment:{Environment.UserName}@{Environment.MachineName}")], "deployment")),
            correlationId: Guid.NewGuid().ToString("N"));

        // 首次初始化与授权写在本事务里，锁要持有到提交之后：
        // 提交前放锁，另一入口拿到锁时读不到未提交的角色，会再建一个。
        await using var lockHandle = await distributedLock.LockAsync(SystemInitializer.InitializationLockKey, cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockHandle.LockLost);
        cancellationToken = lockScope.Token;

        using var uow = unitOfWorkManager.Begin(requiresNew: true);
        var key = subject.ToString();
        var side = tenant.HasValue ? MultiTenancySides.Tenant : MultiTenancySides.Host;
        var adminRole = await FindAdminRoleAsync(cancellationToken);
        User? user;
        // 软删除的成员关系就是"曾被移出"的历史，随用户一并加载；租户过滤保持开启
        using (dataFilter.Disable<ISoftDelete>())
        {
            user = await userRepository.GetWithRolesAsync(subject, cancellationToken);
        }

        if (restore)
        {
            return await RestoreAsync(uow, subject, side, adminRole, user, apply, cancellationToken);
        }

        if (user is { IsDeleted: true })
            throw new InvalidOperationException($"User {key} has been deleted in this service and cannot be bootstrapped; add --restore to restore it as an administrator.");

        if (adminRole is not null && user is not null)
        {
            var memberships = user.Roles.Where(ur => ur.RoleId == adminRole.Id).ToList();
            if (memberships.Any(ur => !ur.IsDeleted))
                return $"User {key} already holds the {AdminConstant.RoleName} role; nothing changed.";
            if (memberships.Count != 0)
                return $"User {key} was removed from the {AdminConstant.RoleName} role earlier; re-assign it through role management, or add --restore when no administrator is left. Nothing changed.";
        }

        if (!apply)
        {
            var roleStep = adminRole is null
                ? $"create the {AdminConstant.RoleName} role and seed {AvailablePermissionNames(side).Count} {side} permissions"
                : await DescribeExistingRoleAsync(adminRole, side, cancellationToken);
            var userStep = user is null
                ? "create a minimal local user"
                : $"reuse local user '{user.Username}' ({(user.IsActive ? "active" : "inactive")}) without changing it";
            return $"Dry run for {key}: would {roleStep}, {userStep}, and assign the {AdminConstant.RoleName} role. "
                + "No data or audit record changed.";
        }

        adminRole = await initializer.InitializeWithinLockAsync(cancellationToken);

        var (subjectUser, created) = await userDomainService.EnsureSubjectAsync(subject, cancellationToken);
        subjectUser.AssignRoles([adminRole.Id]);
        await userRepository.UpdateAsync(subjectUser, cancellationToken);
        await recorder.RecordSucceededAsync(OperationRecordActions.ResourceAdminGranted,
            OperationTarget.For($"User/{key}", key), OperationRecordAuthorizations.DeploymentBootstrap, cancellationToken);
        await uow.CompleteAsync(cancellationToken);

        return $"Assigned the {AdminConstant.RoleName} role to {key}"
            + (created ? " (created a minimal local user; its profile fills in on first sign-in)" : string.Empty)
            + ". Later role changes are managed through role management; re-running will not undo them.";
    }

    // 先列出要修的每一项：预演照实说、没有要修的就什么也不写（重复执行不留第二条记录）
    private async Task<string> RestoreAsync(
        IUnitOfWork uow, Guid subject, MultiTenancySides side, Role? adminRole, User? user, bool apply, CancellationToken cancellationToken)
    {
        var key = subject.ToString();
        var missing = adminRole is null
            ? AvailablePermissionNames(side)
            : await MissingPermissionNamesAsync(adminRole, side, cancellationToken);
        var repairs = new List<string>();
        if (user is null) repairs.Add("create a minimal local user");
        if (user is { IsDeleted: true }) repairs.Add("restore the deleted local user");
        if (user is { IsActive: false }) repairs.Add("enable the local user");
        if (adminRole is null) repairs.Add($"create the {AdminConstant.RoleName} role");
        if (adminRole is null || user is null || !user.IsInRole(adminRole.Id)) repairs.Add($"assign the {AdminConstant.RoleName} role");
        if (missing.Count != 0) repairs.Add($"grant {missing.Count} missing {side} permissions to the shared {AdminConstant.RoleName} role");

        if (repairs.Count == 0)
            return $"User {key} already has working administrator access; nothing to restore.";

        if (!apply)
            return $"Dry run (restore) for {key}: would {string.Join(", ", repairs)}. No data or audit record changed.";

        adminRole = await initializer.InitializeWithinLockAsync(cancellationToken);
        var target = user ?? (await userDomainService.EnsureSubjectAsync(subject, cancellationToken)).User;
        if (target.IsDeleted) target.Restore();
        target.Enable();
        target.AssignRoles([adminRole.Id]);
        await userRepository.UpdateAsync(target, cancellationToken);
        // 角色若刚在上一步首次播种，这里已无缺口；否则按缺口逐项补授，已有的授予与其他角色不动
        foreach (var permission in await MissingPermissionNamesAsync(adminRole, side, cancellationToken))
        {
            await grantManager.GrantAsync(permission, PermissionGrantProviderNames.Role, adminRole.Id.ToString(), cancellationToken);
        }

        await recorder.RecordSucceededAsync(OperationRecordActions.ResourceAdminRestored,
            OperationTarget.For($"User/{key}", key), OperationRecordAuthorizations.DeploymentBootstrap, cancellationToken);
        await uow.CompleteAsync(cancellationToken);

        return $"Restored administrator access for {key}: {string.Join(", ", repairs)}. "
            + $"The {AdminConstant.RoleName} role is shared, so every member holds the granted permissions; sessions and tokens revoked earlier stay revoked.";
    }

    private Task<Role?> FindAdminRoleAsync(CancellationToken cancellationToken)
        => roleRepository.GetFirstAsync(r => r.Name == AdminConstant.RoleName, cancellationToken: cancellationToken);

    private List<string> AvailablePermissionNames(MultiTenancySides side)
        => [.. definitions.GetAll().Select(d => d.Name).Where(name => definitions.IsAvailableOn(name, side))];

    private async Task<List<string>> MissingPermissionNamesAsync(Role adminRole, MultiTenancySides side, CancellationToken cancellationToken)
    {
        var current = await grants.GetGrantsAsync(PermissionGrantProviderNames.Role, adminRole.Id.ToString(), cancellationToken);
        return [.. AvailablePermissionNames(side).Where(name => !current.PermissionNames.Contains(name))];
    }

    // 角色已经播种过就按现状如实说，不能在权限被人为缩减后仍报"将授予全部"
    private async Task<string> DescribeExistingRoleAsync(Role adminRole, MultiTenancySides side, CancellationToken cancellationToken)
    {
        var current = await grants.GetGrantsAsync(PermissionGrantProviderNames.Role, adminRole.Id.ToString(), cancellationToken);
        return current.Version == 0
            ? $"seed {AvailablePermissionNames(side).Count} {side} permissions to the existing {AdminConstant.RoleName} role"
            : $"keep the {AdminConstant.RoleName} role's current {current.PermissionNames.Count} permissions";
    }
}
