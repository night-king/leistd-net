using System.Security.Claims;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Tenants.Provider;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.AmbientContext;
using Leistd.Auditing.Abstractions;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Data.Connections;
using Leistd.Data.Filters;
using Leistd.Data.Querying;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.DbMigrator.Runners;

/// <summary>租户管理员恢复：租户把自己锁死后（管理员被停用、删除、移出 Admin，或 Admin 角色权限被撤空），由部署方把原管理员请回来。</summary>
/// <remarks>
/// <para>恢复的是开通时建的那个管理员（用户名 <see cref="AdminConstant.TenantAdminUsername"/>）的<b>原记录</b>：
/// 撤回软删除、按原 Id 回来，重新启用，补回 Admin 成员关系（只补这一条，被撤的其他角色不复活）；
/// 再经 <see cref="IPermissionGrantManager"/> 把租户侧当前可用、Admin 角色却没有的权限补授给它。
/// Admin 是共享角色，补授对它的全部成员生效。不重置口令与两步验证，不恢复已作废的会话；
/// 找不到原记录（改过名或被永久清除）就失败，不替人新建账号。</para>
/// <para>写入在该租户实际落点（共享库按租户过滤，独立库按登记的连接）的一个工作单元里完成，
/// 与成功记录同生共死；持有与开通共用的租户初始化锁到提交之后。开通一侧在写完开通数据时就放锁、由外层随后提交，
/// 锁不覆盖它的提交窗口（部署说明写明了这个边界）。没有要修的就什么也不写。</para>
/// </remarks>
internal sealed class TenantAdminRestoreRunner(
    ICurrentTenant currentTenant,
    IAmbientContext ambientContext,
    IConnectionStringResolver connectionResolver,
    ITenantConnectionConfigurationStore tenantConnections,
    IUnitOfWorkManager unitOfWorkManager,
    IDistributedLock distributedLock,
    IDataFilter dataFilter,
    IRepository<Role, Guid> roleRepository,
    IUserRepository userRepository,
    IQueryableAsyncExecuter asyncExecuter,
    IPermissionDefinitionManager definitions,
    IPermissionGrantStore grants,
    IPermissionGrantManager grantManager,
    IOperationRecorder recorder)
{
    public async Task<string> RunAsync(Guid tenantId, bool apply, CancellationToken cancellationToken = default)
    {
        var lookup = await tenantConnections.FindAsync(tenantId, ConnectionStringNames.Default, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' does not exist.");
        if (lookup.TenantId != tenantId)
            throw new InvalidOperationException("The control plane returned a different tenant.");

        using var tenantScope = currentTenant.Change(tenantId);
        // 先解析实际落点；不会打印含凭据的连接串。独立库的连接串解不开时在这里失败，不会落到共享库上
        _ = await connectionResolver.ResolveAsync(ConnectionStringNames.Default, cancellationToken);
        using var actorScope = ambientContext.Begin(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", $"deployment:{Environment.UserName}@{Environment.MachineName}")], "deployment")),
            correlationId: Guid.NewGuid().ToString("N"));

        await using var lockHandle = await distributedLock.LockAsync(TenantSeeder.InitializationLockKey(tenantId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockHandle.LockLost);
        cancellationToken = lockScope.Token;

        using var uow = unitOfWorkManager.Begin(requiresNew: true);
        var adminRole = await roleRepository.GetFirstAsync(r => r.Name == AdminConstant.RoleName, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenantId}' has no {AdminConstant.RoleName} role; it was never provisioned. Nothing changed.");
        User? admin;
        // 删除的管理员与撤销过的成员关系都要看得见；租户过滤保持开启
        using (dataFilter.Disable<ISoftDelete>())
        {
            admin = await asyncExecuter.FirstOrDefaultAsync(
                (await userRepository.GetQueryableWithRolesAsync(cancellationToken))
                    .Where(u => u.Username == AdminConstant.TenantAdminUsername),
                cancellationToken);
        }

        if (admin is null)
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has no '{AdminConstant.TenantAdminUsername}' user record (renamed or permanently removed). "
                + "This command only restores the original administrator and never creates accounts; "
                + "create a tenant administrator through the tenant's user management instead. Nothing changed.");

        var missing = await MissingPermissionNamesAsync(adminRole, cancellationToken);
        var repairs = new List<string>();
        if (admin.IsDeleted) repairs.Add("restore the deleted account");
        if (!admin.IsActive) repairs.Add("enable the account");
        if (!admin.IsInRole(adminRole.Id)) repairs.Add($"assign the {AdminConstant.RoleName} role");
        if (missing.Count != 0) repairs.Add($"grant {missing.Count} missing tenant permissions to the shared {AdminConstant.RoleName} role");

        var target = $"'{AdminConstant.TenantAdminUsername}' ({admin.Id}) of tenant {tenantId}";
        if (repairs.Count == 0)
            return $"The administrator {target} already has working access; nothing to restore.";

        if (!apply)
            return $"Dry run for {target}: would {string.Join(", ", repairs)}. No data or audit record changed.";

        if (admin.IsDeleted) admin.Restore();
        admin.Enable();
        admin.AssignRoles([adminRole.Id]);
        await userRepository.UpdateAsync(admin, cancellationToken);
        // 只补缺口：已有的授予、其他角色与用户直授都不动
        foreach (var permission in missing)
        {
            await grantManager.GrantAsync(permission, PermissionGrantProviderNames.Role, adminRole.Id.ToString(), cancellationToken);
        }

        await recorder.RecordSucceededAsync(OperationRecordActions.TenantAdminRestored,
            OperationTarget.For(admin.Id, admin.Username), OperationRecordAuthorizations.DeploymentBootstrap, cancellationToken);
        await uow.CompleteAsync(cancellationToken);

        return $"Restored the administrator {target}: {string.Join(", ", repairs)}. "
            + $"The {AdminConstant.RoleName} role is shared, so every member holds the granted permissions; "
            + "the password, two-factor settings and revoked sessions are unchanged.";
    }

    private async Task<List<string>> MissingPermissionNamesAsync(Role adminRole, CancellationToken cancellationToken)
    {
        var current = await grants.GetGrantsAsync(PermissionGrantProviderNames.Role, adminRole.Id.ToString(), cancellationToken);
        return [.. definitions.GetAll()
            .Select(definition => definition.Name)
            .Where(name => definitions.IsAvailableOn(name, MultiTenancySides.Tenant) && !current.PermissionNames.Contains(name))];
    }
}
