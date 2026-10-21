namespace Leistd.RealTime.Subscriptions;

/// <summary>实时资源订阅授权器；宿主必须注册一个实现。</summary>
public interface IRealTimeSubscriptionAuthorizer
{
    /// <summary>判断当前用户是否允许订阅指定资源。</summary>
    Task<bool> AuthorizeAsync(
        RealTimeSubscriptionContext context,
        CancellationToken cancellationToken = default);
}
