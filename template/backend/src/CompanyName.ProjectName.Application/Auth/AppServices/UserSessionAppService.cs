#if (LocalIdentity)
using Leistd.Data.Querying;
using CompanyName.ProjectName.Domain.Auth.Errors;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Application.Auth.Mappings;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Leistd.Ddd.Application.AppServices;
using Leistd.Ddd.Domain.Repositories;
using Leistd.ExceptionHandling;
using Leistd.ObjectMapping.Abstractions;
using Leistd.OperationRecords.Models;
using Leistd.OperationRecords.Recording;
using Leistd.Security.Users;
using Leistd.Timing;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.Application.Auth.AppServices;

/// <inheritdoc cref="IUserSessionAppService" />
internal sealed class UserSessionAppService(
    IRepository<UserSession, Guid> userSessionRepository,
    IQueryableAsyncExecuter asyncExecuter,
    UserSessionDomainService userSessionDomainService,
    ICurrentUser currentUser,
    IUnitOfWorkManager unitOfWorkManager,
    IOperationRecorder operationRecorder,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IClock clock,
    IObjectMapper objectMapper,
    CurrentSessionTerminator currentSessionTerminator) : BaseAppService, IUserSessionAppService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSessionOutputDto>> GetCurrentUserSessionsAsync(CancellationToken cancellationToken = default)
    {
        var userId = currentUser.Id!.Value;
        var currentSessionId = currentUser.GetSessionId();
        var idleTimeout = (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout;
        var sessions = await asyncExecuter.ToListAsync(
            (await userSessionRepository.GetQueryableAsync(cancellationToken))
                .Where(s => s.UserId == userId)
                .Where(UserSession.ActiveAt(clock.Now, idleTimeout)),
            cancellationToken);

        var context = new Dictionary<string, object>();
        if (currentSessionId is { } current)
            context[AuthMappings.CurrentSessionIdKey] = current;

        return sessions
            .Select(s => objectMapper.Map<UserSession, UserSessionOutputDto>(s, context))
            // 当前设备置顶：用户找"这是哪台"的第一眼总是自己手上这台；其余按最近活跃
            .OrderByDescending(s => s.IsCurrent)
            .ThenByDescending(s => s.LastSeenTime)
            .ToList();
    }

    /// <inheritdoc />
    public async Task RevokeCurrentUserSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == currentUser.GetSessionId())
        {
            throw new BusinessException(AuthErrorCodes.CannotRevokeCurrentSession, "Use sign-out to end the current session.");
        }

        var revoked = await userSessionDomainService.RevokeAsync(currentUser.Id!.Value, sessionId, cancellationToken);
        if (revoked is null)
            return;

        // 目标取那台设备的 IP：会话 Id 对看记录的人没有意义，IP 至少能和登录记录对上
        await operationRecorder.RecordSucceededAsync(
            OperationRecordActions.AuthSessionRevoked,
            OperationTarget.For(revoked.Id.ToString(), revoked.IpAddress ?? "-"),
            OperationRecordAuthorizations.AuthenticatedSelf,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RevokeOtherCurrentUserSessionsAsync(CancellationToken cancellationToken = default)
    {
        var idleTimeout = (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout;
        var revoked = await userSessionDomainService.RevokeAllAsync(
            currentUser.Id!.Value,
            currentUser.GetSessionId(),
            cancellationToken);
        // 只数仍有效的设备：与设备列表同一条规则，已过期的会话顺手删掉但不算"退出了几台"
        var now = clock.Now;
        var count = revoked.Count(session => !session.IsExpired(now, idleTimeout));

        if (count > 0)
        {
            await operationRecorder.RecordSucceededAsync(
                OperationRecordActions.AuthOtherSessionsRevoked,
                OperationTarget.For(currentUser.Id!.Value, currentUser.Name ?? currentUser.Username),
                OperationRecordAuthorizations.AuthenticatedSelf,
                cancellationToken);
        }

        return count;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 新开工作单元：调用处可能刚在别的租户上下文里写过库，请求作用域里的上下文未必绑着当前租户的库。
    /// 租户上下文沿用环境值——多租户中间件已按会话主体的租户声明设好。
    /// </remarks>
    public async Task<DateTime?> GetCurrentSessionStartTimeAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is not { } userId || currentUser.GetSessionId() is not { } sessionId)
            return null;

        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
        var session = await userSessionRepository.GetByIdAsync(sessionId, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);
        return session?.UserId == userId ? session.CreationTime : null;
    }

    /// <inheritdoc />
    public Task EndCurrentSessionAsync(CancellationToken cancellationToken = default) =>
        currentSessionTerminator.EndAsync(cancellationToken);
}
#endif
