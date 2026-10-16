#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.Policies;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.Security.Claims;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>按主体所在租户读会话空闲时限：签发会话 Cookie 时据此定有效期。</summary>
/// <remarks>
/// 签发时环境租户不一定是主体的租户：进入模拟时环境是宿主、主体是目标租户，结束模拟时反过来。
/// 所以按主体的租户声明切换上下文，并在独立工作单元里读（一个工作单元不能改变连接归属）。
/// </remarks>
public interface ISessionIdleTimeoutReader
{
    /// <summary>读取主体所在租户的会话空闲时限；租户声明非法、租户不存在或已停用时返回 <see langword="null"/>。</summary>
    /// <param name="principal">待签发或已签发的会话主体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<TimeSpan?> ReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISessionIdleTimeoutReader" />
internal sealed class SessionIdleTimeoutReader(
    ICurrentTenant currentTenant,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
#endif
    IUnitOfWorkManager unitOfWorkManager,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IOptions<ClaimTypeOptions> claimTypes) : ISessionIdleTimeoutReader
{
    /// <inheritdoc />
    public async Task<TimeSpan?> ReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var tenantClaim = claimTypes.Value.ReadTenant(principal);
        if (!tenantClaim.IsValid)
            return null;

        string? tenantName = null;
#if (IncludeMultiTenancy)
        if (tenantClaim.TenantId is { } id)
        {
            // 不可用的租户交给租户会话自恢复中间件处理，这里不读它的库
            var tenant = await tenantStore.FindAsync(id, cancellationToken);
            if (tenant is null || !tenant.IsActive)
                return null;
            tenantName = tenant.Name;
        }
#else
        if (tenantClaim.TenantId is not null)
            return null;
#endif

        using (currentTenant.Change(tenantClaim.TenantId, tenantName))
        using (var unitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            var idleTimeout = (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout;
            await unitOfWork.CompleteAsync(cancellationToken);
            return idleTimeout;
        }
    }
}
#endif
