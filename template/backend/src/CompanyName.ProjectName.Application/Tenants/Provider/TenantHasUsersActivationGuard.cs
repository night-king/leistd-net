using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.MultiTenancy.Management.Provisioning;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Tenants.Errors;
#endif
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Stores;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.Application.Tenants.Provider;

/// <summary>启用前要求租户里至少有一个用户。</summary>
/// <remarks>
/// <para>启用一个没有管理员的租户毫无用途，只会成为匿名入口（注册、找回密码）的靶子；它同时挡住控制面竞争：
/// 另一个宿主管理员在开通阶段抢先手动启用，会把一个还没有管理员的半成品租户暴露出去。</para>
/// <para><b>必须在租户上下文内新开工作单元</b>，不能只切上下文：请求作用域里的 DbContext 在授权阶段读权限授予时
/// 就绑定到了宿主库，分库租户的用户在它自己的库里，只切上下文计数恒为 0——已停用的分库租户再也启不回来。</para>
/// </remarks>
internal sealed class TenantHasUsersActivationGuard(
    ICurrentTenant currentTenant,
    IUnitOfWorkManager unitOfWorkManager,
    IRepository<User, Guid> userRepository) : ITenantActivationGuard
{
    public async Task EnsureCanActivateAsync(TenantConfiguration tenant, CancellationToken cancellationToken = default)
    {
        long userCount;
        using (currentTenant.Change(tenant.Id, tenant.Name))
        using (var tenantUnitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            userCount = await userRepository.CountAsync(cancellationToken: cancellationToken);
            await tenantUnitOfWork.CompleteAsync(cancellationToken);
        }

        if (userCount == 0)
        {
            throw new BusinessException(TenantErrorCodes.ActivateWithoutUsers,
                    "This tenant has no users yet; activating it would let nobody in. Finish provisioning first.");
        }
    }
}
