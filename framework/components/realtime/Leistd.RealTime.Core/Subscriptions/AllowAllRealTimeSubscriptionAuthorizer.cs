namespace Leistd.RealTime.Subscriptions;

/// <summary>允许所有订阅的授权器，须由宿主显式注册。</summary>
public sealed class AllowAllRealTimeSubscriptionAuthorizer : IRealTimeSubscriptionAuthorizer
{
    /// <inheritdoc />
    public Task<bool> AuthorizeAsync(
        RealTimeSubscriptionContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
