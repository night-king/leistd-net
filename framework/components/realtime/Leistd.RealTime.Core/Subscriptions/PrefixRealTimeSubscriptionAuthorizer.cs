namespace Leistd.RealTime.Subscriptions;

// 只放行指定前缀的资源键：公共资源集中在少数前缀下，其余资源由宿主自己的授权器按业务规则判定。
// 按序数比较：资源键是协议标识，不是可本地化的文本。
internal sealed class PrefixRealTimeSubscriptionAuthorizer(IReadOnlyList<string> prefixes) : IRealTimeSubscriptionAuthorizer
{
    public Task<bool> AuthorizeAsync(RealTimeSubscriptionContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(prefixes.Any(prefix => context.ResourceKey.StartsWith(prefix, StringComparison.Ordinal)));
}
