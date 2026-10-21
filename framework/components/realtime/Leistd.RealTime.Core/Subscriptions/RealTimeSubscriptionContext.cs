namespace Leistd.RealTime.Subscriptions;

/// <summary>实时资源订阅上下文。</summary>
/// <param name="ResourceKey">资源标识，如 <c>product-profile:{id}</c>。</param>
/// <param name="UserId">当前连接用户标识；匿名连接为 <see langword="null"/>。</param>
public sealed record RealTimeSubscriptionContext(
    string ResourceKey,
    string? UserId);
