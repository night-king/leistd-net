namespace Leistd.Security.Claims;

/// <summary>机器主体（OAuth2 client credentials 令牌代表的工作负载）的 <c>sub</c> 契约。</summary>
/// <remarks>
/// 自然人主体的 <c>sub</c> 是 GUID 用户 Id；机器主体使用带前缀的独立命名空间，
/// 防止可自定义的 <c>client_id</c> 被解析为用户身份。
/// 签发端按 <see cref="Format"/> 构造，消费端按 <see cref="Matches"/> 判定。
/// </remarks>
public static class ClientSubject
{
    /// <summary>机器主体 <c>sub</c> 的前缀。</summary>
    public const string Prefix = "client:";

    /// <summary>由 <c>client_id</c> 构造机器主体的 <c>sub</c> 值。</summary>
    /// <param name="clientId">客户端标识。</param>
    public static string Format(string clientId) => Prefix + clientId;

    /// <summary>判断 <paramref name="subject"/> 是否为机器主体（任意客户端）。</summary>
    /// <remarks>仅按前缀区分工作负载与自然人；允许哪个客户端由 scope 判定。</remarks>
    /// <param name="subject">待判定的 <c>sub</c> 值。</param>
    public static bool IsMachine(string? subject) =>
        subject is not null && subject.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>判断 <paramref name="subject"/> 是否正是 <paramref name="clientId"/> 对应的机器主体 <c>sub</c>。</summary>
    /// <param name="subject">待判定的 <c>sub</c> 值。</param>
    /// <param name="clientId">客户端标识。</param>
    /// <returns>两者均非空且完全匹配时为 <see langword="true"/>。</returns>
    public static bool Matches(string? subject, string? clientId) =>
        !string.IsNullOrEmpty(subject) &&
        !string.IsNullOrEmpty(clientId) &&
        string.Equals(subject, Format(clientId), StringComparison.Ordinal);
}
