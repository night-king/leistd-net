#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.DomainServices;
using Leistd.Security.Users;
using Leistd.UnitOfWork;

namespace CompanyName.ProjectName.Application.Auth.Sessions;

/// <summary>结束当前请求所属的会话；退出登录与换发会话共用。</summary>
internal sealed class CurrentSessionTerminator(
    UserSessionDomainService userSessionDomainService,
    ICurrentUser currentUser,
    IUnitOfWorkManager unitOfWorkManager)
{
    /// <summary>未登录或会话已不存在时什么也不做。</summary>
    /// <remarks>在独立工作单元里撤销：调用方随后失败也不能让已退出的会话复活。</remarks>
    public async Task EndAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is not { } userId || currentUser.GetSessionId() is not { } sessionId)
            return;

        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);
        await userSessionDomainService.RevokeAsync(userId, sessionId, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);
    }
}
#endif
