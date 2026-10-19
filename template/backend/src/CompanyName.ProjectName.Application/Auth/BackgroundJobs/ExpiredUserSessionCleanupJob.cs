#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Leistd.BackgroundJobs.Recurring;
using Leistd.Data.Querying;
using Leistd.Data.Connections;
using Leistd.Data.Filters;
using Leistd.Ddd.Domain.Repositories;
using Leistd.MultiTenancy.Tenancy;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.Timing;
using Leistd.UnitOfWork;
using Microsoft.Extensions.Logging;

namespace CompanyName.ProjectName.Application.Auth.BackgroundJobs;

/// <summary>每天删除已过期的会话（最近活动早于空闲超时）。</summary>
/// <remarks>
/// <para>登录时会顺手删掉此人自己的过期会话，但不再登录的用户没有这个时机：他们的会话行连同原始 IP
/// 会无限期留在表里。过期会话既不能再用于登录，设备列表也不显示，保留它没有任何读取方，所以不设保留期。</para>
/// <para>会话落在各自租户的库里：按物理库逐个执行，停用租户的库也清（数据不因停用就不占空间）；
/// 有库失败或租户解析不出库时本轮报失败，调度器不记水位，下一个时段按截止时间一并清掉。
/// 停用租户的登记仍在而解析或执行失败时同样如实报失败；数据已了结的租户应删除（见多租户组件文档）。</para>
/// <para>空闲时限是租户策略：每个库先查出其中有会话的租户（共享库里的租户只能从数据取），再逐个在该租户的上下文与
/// 独立工作单元里读策略、删它自己的过期会话——一个工作单元不能改变连接归属，不在同一个里逐个切租户。
/// 已删除的租户跳过：它的连接解析不出来，数据随租户清理一并处理。</para>
/// </remarks>
internal sealed class ExpiredUserSessionCleanupJob(
    ITenantDatabaseRunner databaseRunner,
    IUnitOfWorkManager unitOfWorkManager,
    IRepository<UserSession, Guid> userSessionRepository,
    IQueryableAsyncExecuter asyncExecuter,
    IDataFilter dataFilter,
    ICurrentTenant currentTenant,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
#endif
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IClock clock,
    ILogger<ExpiredUserSessionCleanupJob> logger) : IRecurringJob
{
    internal const string Name = "auth.sessions.cleanup";

    public async Task ExecuteAsync(RecurringJobContext context, CancellationToken cancellationToken)
    {
        var now = clock.Now;

        var result = await databaseRunner.ForEachDatabaseAsync(ConnectionStringNames.Default, activeOnly: false, async (_, ct) =>
        {
            foreach (var tenantId in await TenantsWithSessionsAsync(ct))
            {
                await DeleteExpiredAsync(tenantId, now, ct);
            }
        }, cancellationToken);

        if (result.FailedDatabases.Count > 0 || result.UnresolvedTenants.Count > 0)
        {
            throw new InvalidOperationException(
                $"Expired session cleanup failed for {result.FailedDatabases.Count} of {result.Databases} database(s); " +
                $"{result.UnresolvedTenants.Count} tenant(s) could not be resolved to a database.");
        }

        logger.LogInformation("Deleted expired sessions across {Databases} database(s).", result.Databases);
    }

    // 本库里有会话的租户（宿主为 null）；单独一个工作单元，查完即结束
    private async Task<List<Guid?>> TenantsWithSessionsAsync(CancellationToken cancellationToken)
    {
        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
        List<Guid?> tenantIds;
        using (dataFilter.Disable<IMultiTenant>())
        {
            tenantIds = await asyncExecuter.ToListAsync(
                (await userSessionRepository.GetQueryableAsync(cancellationToken)).Select(s => s.TenantId).Distinct(),
                cancellationToken);
        }

        await unitOfWork.CompleteAsync(cancellationToken);
        return tenantIds;
    }

    private async Task DeleteExpiredAsync(Guid? tenantId, DateTime now, CancellationToken cancellationToken)
    {
#if (IncludeMultiTenancy)
        if (tenantId is { } id && await tenantStore.FindAsync(id, cancellationToken) is null)
            return;

#endif
        using (currentTenant.Change(tenantId))
        using (var unitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            var idleTimeout = (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout;
            await userSessionRepository.DeleteManyAsync(UserSession.ExpiredAt(now, idleTimeout), cancellationToken);
            await unitOfWork.CompleteAsync(cancellationToken);
        }
    }
}
#endif
