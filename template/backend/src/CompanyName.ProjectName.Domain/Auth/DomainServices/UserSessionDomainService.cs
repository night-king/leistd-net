#if (LocalIdentity)
using Leistd.Data.Querying;
using CompanyName.ProjectName.Domain.Auth.Entities;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Timing;

namespace CompanyName.ProjectName.Domain.Auth.DomainServices;

/// <summary>登录会话的登记与撤销规则。</summary>
/// <remarks>
/// <para>撤销即删除，并由 <see cref="UserSession.Revoke(DateTime)"/> 发出事件：会话校验结果带短缓存，
/// 订阅方在事务提交后作废它，撤销才能对已发出的 Cookie 立即生效。</para>
/// <para>会话落在用户所在租户的库里，登记与撤销跟随调用方当时的租户上下文。空闲时限是租户策略，由调用方读好传入。</para>
/// </remarks>
public class UserSessionDomainService(
    IRepository<UserSession, Guid> userSessionRepository,
    IQueryableAsyncExecuter asyncExecuter,
    IClock clock)
{
    /// <summary>为一次登录登记会话，返回的会话 Id 写进 <c>sid</c> 声明。</summary>
    /// <remarks>顺手删掉此人已过期的会话。不再登录的用户没有这个时机，由每日的过期会话清理作业兜底。</remarks>
    public async Task<UserSession> StartAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        string? impersonatorName,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        var expired = await asyncExecuter.ToListAsync(
            (await userSessionRepository.GetQueryableAsync(cancellationToken))
                .Where(s => s.UserId == userId)
                .Where(UserSession.ExpiredAt(now, idleTimeout)),
            cancellationToken);
        await userSessionRepository.DeleteManyAsync(expired, cancellationToken);

        return await userSessionRepository.InsertAsync(
            new UserSession(userId, now, ipAddress, userAgent, impersonatorName),
            cancellationToken);
    }

    /// <summary>撤销某用户的一个会话；不存在（或不属于该用户）时返回 null。</summary>
    public async Task<UserSession?> RevokeAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await userSessionRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId)
            return null;

        session.Revoke(clock.Now);
        await userSessionRepository.DeleteAsync(session, cancellationToken);
        return session;
    }

    /// <summary>撤销某用户的全部会话（可保留一个），返回被撤销的会话。</summary>
    /// <remarks>
    /// <para>改密码、管理员重置密码、两步验证变更时调用：凭据变了，此前建立的会话不应继续有效。</para>
    /// <para>已过期的会话一并删除（顺手清理，无害）。要给用户看"退出了几台设备"的调用方按租户的空闲时限
    /// 从返回值里数仍有效的那些，与设备列表同一条规则，免得把早已失效的会话也算进去。</para>
    /// </remarks>
    public async Task<IReadOnlyList<UserSession>> RevokeAllAsync(
        Guid userId,
        Guid? exceptSessionId,
        CancellationToken cancellationToken = default)
    {
        var sessions = (await userSessionRepository.GetListAsync(
            s => s.UserId == userId && s.Id != exceptSessionId,
            cancellationToken)).ToList();
        if (sessions.Count == 0)
            return sessions;

        var now = clock.Now;
        foreach (var session in sessions)
        {
            session.Revoke(now);
        }

        await userSessionRepository.DeleteManyAsync(sessions, cancellationToken);
        return sessions;
    }
}
#endif
