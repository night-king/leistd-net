#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Events;
using Leistd.EventBus.EventHandlers;
using Microsoft.Extensions.Caching.Distributed;

namespace CompanyName.ProjectName.Application.Auth.EventHandlers;

/// <summary>启用或换绑提交之后删除待确认的设置密钥，同一份密钥不能再被确认第二次。</summary>
/// <remarks>删除失败时密钥仍按设置有效期自然过期；确认时安全版本已经换过，再次确认会被当作已失效拒绝。</remarks>
internal sealed class TwoFactorSetupCompletedEventHandler(IDistributedCache cache)
    : IEventHandler<TwoFactorSetupCompletedEvent>
{
    /// <inheritdoc />
    public Task HandleAsync(TwoFactorSetupCompletedEvent @event, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(TwoFactorAppService.SetupKey(@event.UserId), cancellationToken);
}
#endif
