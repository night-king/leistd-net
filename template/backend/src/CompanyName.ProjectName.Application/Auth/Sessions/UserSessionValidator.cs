#if (LocalIdentity)
using System.Security.Claims;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.Security.Claims;
using Leistd.Timing;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Leistd.Security.RequestContext;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <inheritdoc cref="IUserSessionValidator" />
/// <remarks>
/// 校验发生在认证阶段、多租户中间件之前，由本类按主体的租户声明自行切换上下文。
/// 有效的结果缓存 <see cref="UserSession.TouchInterval"/>；会话被撤销时由
/// <see cref="EventHandlers.UserSessionRevokedEventHandler"/> 作废对应缓存。
/// 受限判定在缓存之外，命中与未命中都按最新的策略与账号状态得出。
/// </remarks>
internal sealed class UserSessionValidator(
    IRepository<UserSession, Guid> userSessionRepository,
    IRepository<User, Guid> userRepository,
    ICurrentTenant currentTenant,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
#endif
    IUnitOfWorkManager unitOfWorkManager,
    IDistributedCache distributedCache,
    IRequestClientInfo clientInfo,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IOptions<ClaimTypeOptions> claimTypes,
    IClock clock) : IUserSessionValidator
{
    private const string CacheKeyPrefix = "auth:session:";

    /// <inheritdoc />
    public async Task<SessionValidationResult> ValidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        // 没有会话声明的主体一律无效：不留"旧 Cookie 免检"的口子，否则撤销对它们不起作用
        if (ReadGuid(principal.FindFirst(CustomClaimTypes.SessionId)?.Value) is not { } sessionId ||
            ReadGuid(claimTypes.Value.FindUserId(principal)) is not { } userId)
            return SessionValidationResult.Invalid;

        // 租户声明非法的会话一律无效：按宿主处理等于让租户会话进到宿主库
        var tenantClaim = claimTypes.Value.ReadTenant(principal);
        if (!tenantClaim.IsValid)
            return SessionValidationResult.Invalid;

        var tenantId = tenantClaim.TenantId;
#if (!IncludeMultiTenancy)
        if (tenantId is not null) return SessionValidationResult.Invalid;
#endif
        string? tenantName = null;
#if (IncludeMultiTenancy)
        if (tenantId is { } id)
        {
            var tenant = await tenantStore.FindAsync(id, cancellationToken);
            // 租户已不存在或已停用：不在这里判会话无效，交给租户会话自恢复中间件（UseTenantSessionRecovery）。
            // 它注销的同时带上 X-Tenant-Invalid，前端据此清掉本地的租户选择；在这里拒绝只会得到一个普通 401，
            // 下次登录仍落到那个不可用的租户上。也不读它的策略：注销就在本请求里发生
            if (tenant is null || !tenant.IsActive)
                return SessionValidationResult.Valid(twoFactorSetupRequired: false);
            tenantName = tenant.Name;
        }

#endif
        // 模拟登录的会话不受限：要求的是本人先完成设置，不该由发起人替被模拟者设置两步验证
        var restrictable = true;
#if (Impersonation)
        restrictable = principal.FindFirst(CustomClaimTypes.ImpersonatorUserId) is null;
#endif
        var cacheKey = CacheKey(sessionId);
        var cached = await distributedCache.GetStringAsync(cacheKey, cancellationToken) is not null;
        bool restricted;
        TimeSpan? idleTimeout = null;

        // 认证发生在多租户中间件之前，环境租户还没设：按主体的租户进到它的库里查
        using (currentTenant.Change(tenantId, tenantName))
        using (var unitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            // 两步验证开关与空闲时限都是主体所在租户的登录安全策略，在租户上下文里一次读出（同一份设置快照）。
            // 两项都只有租户级。设置读取器按租户与用户分键缓存：认证阶段主体尚未挂到请求上，这次读取记在匿名键下，
            // 不会让同一请求里随后按用户读的设置落空，所以直接用请求作用域里的策略读取，不必另开作用域
            var policy = await loginSecurityPolicy.GetAsync(cancellationToken);

            if (cached)
            {
                // 缓存只说明会话有效；受限与否每次重新判定，开关与两步验证状态都不跟着缓存。
                // 空闲时限不在这里收敛：缓存命中不查会话，过期仍按上次实际校验的结论
                restricted = restrictable &&
                    policy.RequireTwoFactor &&
                    await userRepository.GetByIdAsync(userId, cancellationToken) is { TwoFactorEnabled: false };
            }
            else
            {
                var now = clock.Now;
                var session = await userSessionRepository.GetByIdAsync(sessionId, cancellationToken);
                // 空闲时限按主体所在租户的策略：调短后，空闲已超过新时长的会话在下一次实际校验时失效
                if (session is null || session.UserId != userId || session.IsExpired(now, policy.SessionIdleTimeout))
                    return SessionValidationResult.Invalid;

                // 经应用停用、删除账号时会话已被撤销；这里兜住绕过应用直接改库的情形，最迟在缓存到期后生效
                var user = await userRepository.GetByIdAsync(userId, cancellationToken);
                if (user is null || !user.AllowsExistingSessions(now))
                    return SessionValidationResult.Invalid;

                restricted = restrictable && policy.RequireTwoFactor && !user.TwoFactorEnabled;
                idleTimeout = policy.SessionIdleTimeout;

                if (session.Touch(now, clientInfo.IpAddress))
                {
                    await userSessionRepository.UpdateAsync(session, cancellationToken);
                }
            }

            await unitOfWork.CompleteAsync(cancellationToken);
        }

        if (!cached)
        {
            // 缓存时长即最近活跃的更新间隔：缓存命中期间不查会话、不写库。只缓存"有效"，不缓存受限与否
            await distributedCache.SetStringAsync(
                cacheKey,
                "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = UserSession.TouchInterval },
                cancellationToken);
        }

        return SessionValidationResult.Valid(restricted, idleTimeout);
    }

    // 校验结果的缓存键；会话撤销时按同一个键作废
    internal static string CacheKey(Guid sessionId) => CacheKeyPrefix + sessionId.ToString("N");

    private static Guid? ReadGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
#endif
