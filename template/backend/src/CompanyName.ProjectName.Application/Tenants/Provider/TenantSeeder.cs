using Leistd.Data.Querying;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Domain.Users.Constants;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Domain.Users.Repositories;
using Leistd.Auditing.Abstractions;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Leistd.Authorization.Constants;
using Leistd.Authorization.Grants;
using Leistd.Settings.Stores;
using Leistd.Lock.Abstractions;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using CompanyName.ProjectName.Application.Tenants.Dtos;
using Leistd.MultiTenancy.Management.Provisioning;

namespace CompanyName.ProjectName.Application.Tenants.Provider;

/// <summary>租户开通：在新租户里建初始角色、权限授予与租户管理员；创建失败时清掉写过的东西。</summary>
/// <remarks>
/// <para>多租户组件的租户管理用例在目标租户上下文与新工作单元里调用它（见 <see cref="ITenantProvisioner"/>），
/// 所有仓储与授予读写都自动分区到该租户；分库租户的写入直接落进它的专属库。</para>
/// <para>与宿主侧 <c>SystemInitializer</c> 的判据一致：角色"先查后建"，权限经首次授予写入
/// （从未写过才写，涵盖首建与上次中断，不会把人工撤权补回来）。
/// 互斥锁按租户隔离（锁 key 含 tenantId），不同租户的开通互不排队。</para>
/// </remarks>
public class TenantSeeder(
    ICurrentTenant currentTenant,
    IUserRepository userRepository,
    IRepository<Role, Guid> roleRepository,
    IQueryableAsyncExecuter asyncExecuter,
    UserDomainService userDomainService,
    RoleDomainService roleDomainService,
    IDataFilter dataFilter,
    IPermissionGrantSeeder permissionGrantSeeder,
    IPermissionGrantManager permissionGrantManager,
    IDistributedLock distributedLock,
    ISettingStore settingStore,
    ILogger<TenantSeeder> logger) : ITenantProvisioner
{
    /// <summary>租户初始化互斥锁的键，按租户隔离。</summary>
    /// <remarks>
    /// 开通与 DbMigrator 的租户管理员恢复共用：两者都改该租户的 Admin 角色与管理员账号。
    /// 恢复持锁到自己提交之后；开通在本方法返回时放锁，外层工作单元随后才提交，两边的整个事务并不互斥。
    /// </remarks>
    public static string InitializationLockKey(Guid tenantId) => $"MyProject:tenant-init:{tenantId}";

    /// <inheritdoc />
    public async Task ProvisionAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
    {
        // 端点按 CreateTenantWithAdminInputDto 绑定请求体；拿到基类说明宿主把端点映射错了类型
        var input = context.Input as CreateTenantWithAdminInputDto
            ?? throw new InvalidOperationException(
                $"Tenant provisioning needs a {nameof(CreateTenantWithAdminInputDto)}; map the tenant endpoints with that request type.");

        if (currentTenant.Id is not { } tenantId)
        {
            throw new InvalidOperationException("Tenant seeding must run inside a tenant context: call ICurrentTenant.Change(tenantId) first.");
        }

        // 按租户互斥，避免不同租户相互阻塞。
        await using var lockHandle = await distributedLock.LockAsync(InitializationLockKey(tenantId), cancellationToken);
        using var lockScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockHandle.LockLost);
        cancellationToken = lockScope.Token;

        logger.LogInformation("Initializing tenant {TenantId}...", tenantId);

        var adminRole = await EnsureRolesAsync(cancellationToken);
        await SeedAdminRolePermissionsAsync(adminRole, cancellationToken);
        var adminUser = await EnsureTenantAdminAsync(input.AdminEmail, input.AdminPassword, cancellationToken);
        await AssignAdminRoleAsync(adminUser, adminRole, cancellationToken);

        logger.LogInformation("Tenant {TenantId} initialized", tenantId);
    }

    /// <inheritdoc />
    public async Task PurgeAsync(TenantProvisioningContext context, CancellationToken cancellationToken = default)
    {
        if (currentTenant.Id is not { } tenantId)
        {
            throw new InvalidOperationException("Tenant data purge must run inside a tenant context: call ICurrentTenant.Change(tenantId) first.");
        }

        // 租户过滤器限定查询和写入；先物化以支持后续多次枚举。
        var users = (await userRepository.GetListAsync(cancellationToken: cancellationToken)).ToList();
        var roles = (await roleRepository.GetListAsync(cancellationToken: cancellationToken)).ToList();

        // 永久废弃主体时硬删授予和授权版本，避免孤儿行。
        foreach (var role in roles)
        {
            await permissionGrantManager.RemoveProviderAsync(
                PermissionGrantProviderNames.Role, role.Id.ToString(), cancellationToken);
        }

        foreach (var user in users)
        {
            await permissionGrantManager.RemoveProviderAsync(
                PermissionGrantProviderNames.User, user.Id.ToString(), cancellationToken);
        }

        // 主体和成员关系均软删除；必须先删主体再加载成员关系：
        // 成员关系在删除用户、角色之前就被跟踪时，限制删除的外键会让 EF 当场报错。
        if (users.Count > 0)
        {
            await userRepository.DeleteManyAsync(users, cancellationToken);
        }

        if (roles.Count > 0)
        {
            await roleRepository.DeleteManyAsync(roles, cancellationToken);
        }

        if (users.Count > 0)
        {
            // 用户已在本工作单元内软删除：关掉过滤才能把它们连同成员关系一并加载，不依赖删除是否已冲刷
            var userIds = users.Select(u => u.Id).ToList();
            List<User> holders;
            using (dataFilter.Disable<ISoftDelete>())
            {
                holders = await asyncExecuter.ToListAsync(
                    (await userRepository.GetQueryableWithRolesAsync(cancellationToken)).Where(u => userIds.Contains(u.Id)),
                    cancellationToken);
            }

            foreach (var holder in holders)
            {
                holder.RemoveAllRoles();
                await userRepository.UpdateAsync(holder, cancellationToken);
            }
        }

        // 设置行带租户归属：租户永久废弃后它们既读不到也删不掉，必须一并清理。
        await settingStore.RemoveAllAsync(cancellationToken);

        logger.LogInformation(
            "Purged seed data for tenant {TenantId}: {UserCount} user(s), {RoleCount} role(s)",
            tenantId, users.Count, roles.Count);
    }

    private async Task<Role> EnsureRolesAsync(CancellationToken cancellationToken) =>
        (await roleDomainService.EnsureBuiltInRolesAsync("Tenant administrator with all tenant permissions", cancellationToken)).Admin;

    // 只授予租户侧可用的权限；从未写过授予才写，之后按普通角色管理
    private async Task SeedAdminRolePermissionsAsync(Role adminRole, CancellationToken cancellationToken)
    {
        var granted = await permissionGrantSeeder.SeedAllAsync(
            PermissionGrantProviderNames.Role,
            adminRole.Id.ToString(),
            MultiTenancySides.Tenant,
            cancellationToken);

        if (granted is { } count)
        {
            logger.LogInformation("Seeded permission grants for tenant role {RoleName}: {Count} item(s)", adminRole.Name, count);
        }
    }

    /// <summary>建立租户管理员：<b>普通用户</b> + 本租户 Admin 角色。</summary>
    /// <remarks>
    /// <para><b>刻意不打 <c>IsSuperAdmin</c>。</b>那是宿主的防锁死逃生舱，代价与适用范围见
    /// <c>UserDomainService.CreateSuperAdminAsync</c>。租户管理员的权限完全由角色承载：本租户 Admin 角色
    /// 在上一步拿到全部 Tenant 侧权限（含权限管理本身），之后在租户自己的角色页里可查看、可编辑、可撤销，
    /// 撤权即时生效；升级后新增的权限由租户管理员自己授给自己的角色。</para>
    /// <para>代价是租户可以把自己锁死：管理员被停用、删除、移出 Admin，或 Admin 角色的权限被撤空后，
    /// 租户内再没有人能把它改回来（宿主的模拟登录进的也是这个账号）。恢复走部署级命令
    /// <c>DbMigrator --restore-tenant-admin &lt;tenant-id&gt;</c>，见后端 README。</para>
    /// </remarks>
    private async Task<User> EnsureTenantAdminAsync(string adminEmail, string adminPassword, CancellationToken cancellationToken)
    {
        var adminUser = await userRepository.GetFirstAsync(u => u.Username == AdminConstant.TenantAdminUsername, q => q.OrderBy(u => u.Id), cancellationToken);
        if (adminUser == null)
        {
            adminUser = await userDomainService.CreateUserAsync(
                AdminConstant.TenantAdminUsername,
                adminEmail,
                adminPassword,
                displayName: "Tenant Administrator",
                passwordSubject: "Tenant admin password",
                cancellationToken);
            logger.LogInformation("Tenant admin user created: {Username}", AdminConstant.TenantAdminUsername);
        }

        return adminUser;
    }

    private async Task AssignAdminRoleAsync(User adminUser, Role adminRole, CancellationToken cancellationToken)
    {
        // 刚建的管理员在工作单元内还没落库、按 Id 读不到，它也还没有任何角色，直接用它
        var user = await userRepository.GetWithRolesAsync(adminUser.Id, cancellationToken) ?? adminUser;
        if (!user.IsInRole(adminRole.Id))
        {
            user.AssignRoles([adminRole.Id]);
            await userRepository.UpdateAsync(user, cancellationToken);
        }
    }
}
