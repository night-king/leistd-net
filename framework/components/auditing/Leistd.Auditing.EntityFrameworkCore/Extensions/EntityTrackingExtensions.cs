using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Auditing.Abstractions;

namespace Leistd.Auditing.EntityFrameworkCore.Extensions;

/// <summary>实体进入 <see cref="EntityState.Added"/> 时的变更跟踪扩展。</summary>
/// <remarks>创建审计与租户归属在进入跟踪时落定，不随延迟提交跨越当前租户或主体作用域。</remarks>
public static class EntityTrackingExtensions
{
    /// <summary>在实体进入 <see cref="EntityState.Added"/> 时调用处理程序；查询物化的实体不触发。</summary>
    /// <param name="changeTracker">目标上下文的变更跟踪器。</param>
    /// <param name="handler">落值动作；同一实体可能多次进入 <c>Added</c>，因此必须幂等，只在目标值为空时写入。</param>
    /// <remarks>同时订阅 <see cref="ChangeTracker.Tracked"/> 和 <see cref="ChangeTracker.StateChanged"/>，覆盖首次跟踪与后续状态切换。</remarks>
    public static void OnEntityEnteringAdded(this ChangeTracker changeTracker, Action<EntityEntry> handler)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(handler);

        changeTracker.Tracked += (_, e) =>
        {
            // 查询物化的实体不得触发创建审计。
            if (e.FromQuery)
            {
                return;
            }

            Invoke(e.Entry);
        };

        changeTracker.StateChanged += (_, e) =>
        {
            if (e.NewState != EntityState.Added)
            {
                return;
            }

            Invoke(e.Entry);
        };

        void Invoke(EntityEntry entry)
        {
            if (entry.State == EntityState.Added)
            {
                handler(entry);
            }
        }
    }

    /// <summary>为宿主控制面的普通 <see cref="DbContext"/> 启用创建审计。</summary>
    /// <param name="changeTracker">目标上下文的变更跟踪器。</param>
    /// <param name="serviceProvider">用于解析 <see cref="IAuditPropertySetter"/>；为 <see langword="null"/>（设计时工具等）时不订阅。</param>
    /// <remarks>仅供不继承 <c>BaseDbContext</c> 的宿主控制面上下文使用；审计服务延迟到首次写入时解析。</remarks>
    public static void EnableCreationAuditing(
        this ChangeTracker changeTracker,
        IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null)
        {
            return;
        }

        IAuditPropertySetter? setter = null;

        changeTracker.OnEntityEnteringAdded(entry =>
        {
            // 显式启用审计后缺少依赖必须立即失败，不能静默丢失审计。
            setter ??= serviceProvider.GetRequiredService<IAuditPropertySetter>();
            setter.SetCreationProperties(entry);
        });
    }
}
