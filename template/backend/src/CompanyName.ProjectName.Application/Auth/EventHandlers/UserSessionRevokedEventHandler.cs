#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Domain.Auth.Events;
using Leistd.EventBus.EventHandlers;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.EventHandlers;

/// <summary>会话被撤销后作废它的校验缓存，撤销对已发出的 Cookie 立即生效，而不是等缓存自然过期。</summary>
/// <remarks>事件在事务提交后发布：会话确实删掉了才作废，回滚的撤销不会误伤仍然有效的会话。</remarks>
internal sealed class UserSessionRevokedEventHandler(IDistributedCache distributedCache)
    : IEventHandler<UserSessionRevokedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(UserSessionRevokedEvent @event, CancellationToken cancellationToken = default) =>
        distributedCache.RemoveAsync(UserSessionValidator.CacheKey(@event.SessionId), cancellationToken);
}
#endif
