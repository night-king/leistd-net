#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Users.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Timing;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.Application.Auth.Policies;

/// <summary>一次认证失败被累计之后的结果。</summary>
/// <param name="User">累计之后的用户行；<see langword="null"/> 表示这一行已不存在，没有计数。</param>
/// <param name="LockoutTriggered">本次失败恰好触发了锁定。</param>
internal readonly record struct AccessFailureOutcome(User? User, bool LockoutTriggered);

/// <summary>认证失败的累计与锁定：口令登录、两步验证登录、再认证三条路径共用同一套计数。</summary>
/// <remarks>
/// <para><b>为什么要有这一层。</b>这三处此前各写一份"累计、写入、然后紧接着抛出"，
/// 正确性都挂在同一个隐含前提上——调用方不在工作单元内，写入才会即时落库。
/// 谁给其中任何一处加了 <c>[UnitOfWork]</c>，计数就随那次抛出一并回滚，而接口返回一模一样、
/// 界面毫无异常，只有真被爆破时才发现锁定从未生效。收成一处之后，这个前提是结构，不是约定。</para>
/// <para><b>计数不在领域层做。</b>事务边界是应用层的职责；让领域服务持有工作单元管理器，
/// 等于把"这次写入要不要独立提交"这种编排决定塞进领域模型。领域只判定"口令对不对"。</para>
/// </remarks>
internal interface IAccessFailureCounter
{
    /// <summary>在<b>独立工作单元</b>里累计一次失败并提交，与调用方的事务无关。</summary>
    /// <remarks>
    /// 边界只包这一次写入：审计按组件契约本就独立提交，安全提醒是提交后副作用，
    /// 包进来只会把无关失败拖进同一个回滚范围。
    /// </remarks>
    /// <param name="userId">被累计的用户标识。行已不存在时不计数，返回的 <c>User</c> 为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AccessFailureOutcome> CountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>这一次恰好触发锁定时的后续：给本人发安全提醒，并留一条锁定审计。</summary>
    /// <remarks>
    /// 只在触发的那一次调用，一个锁定期内至多一条，写入量有界；锁定期内的后续尝试不再记。
    /// </remarks>
    /// <param name="user">已被 <see cref="CountAsync"/> 累计过的那一行（锁定截止时间从它读）。</param>
    /// <param name="authorizationBasis">授权依据：登录路径尚无主体，再认证路径是本人。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RecordLockoutAsync(User user, string authorizationBasis, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccessFailureCounter" />
internal sealed class AccessFailureCounter(
    IRepository<User, Guid> userRepository,
    IUnitOfWorkManager unitOfWorkManager,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IOperationRecorder operationRecorder,
    ISecurityAlertPublisher securityAlerts,
    IClock clock) : IAccessFailureCounter
{
    /// <inheritdoc />
    public async Task<AccessFailureOutcome> CountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var policy = await loginSecurityPolicy.GetAsync(cancellationToken);
        var now = clock.Now;

        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);

        // 在这个边界内重读：计数要落在这一行的最新状态上，调用方手里那个实例可能已被改过。
        var counted = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (counted is null)
        {
            // 行在本次请求中途被删了。没有可累计的对象，也不能把调用方手里那个陈旧实例
            // 塞进新上下文（那会变成一次插入或并发异常，把一次拒绝变成 500）。
            return new AccessFailureOutcome(null, LockoutTriggered: false);
        }

        var lockedOut = counted.RecordAccessFailed(now, policy.Lockout);
        await userRepository.UpdateAsync(counted, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);

        return new AccessFailureOutcome(counted, lockedOut);
    }

    /// <inheritdoc />
    public async Task RecordLockoutAsync(
        User user,
        string authorizationBasis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var policy = await loginSecurityPolicy.GetAsync(cancellationToken);

        // 再认证路径（改口令、停用两步验证）处在随后要回滚的工作单元里：提醒的写入放进独立工作单元，
        // 否则会随那次拒绝一起回滚，本人收不到"账号已锁定"
        using (var unitOfWork = unitOfWorkManager.Begin(requiresNew: true))
        {
            await securityAlerts.PublishAsync(
                user.Id,
                new SecurityAlert(SecurityAlertKind.LockedOut, Until: user.Lockout.End),
                cancellationToken);
            await unitOfWork.CompleteAsync(cancellationToken);
        }

        await operationRecorder.RecordFailedAsync(
            OperationRecordActions.AuthLockedOut,
            OperationTarget.For(user.Id, user.DisplayName ?? user.Username),
            authorizationBasis,
            OperationFailure.FromCode(AuthErrorCodes.UserTemporarilyLockedOut, new Dictionary<string, object?>
            {
                ["maxFailedAttempts"] = policy.Lockout.MaxFailedAttempts,
                ["minutes"] = (int)policy.Lockout.Duration.TotalMinutes
            }));
    }
}
#endif
