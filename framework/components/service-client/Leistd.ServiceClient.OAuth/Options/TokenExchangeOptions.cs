namespace Leistd.ServiceClient.OAuth.Options;

/// <summary>命名客户端的单跳用户委托目标。</summary>
public sealed class TokenExchangeOptions
{
    /// <summary>目标 API 的唯一受众。</summary>
    public string Audience { get; set; } = string.Empty;
    /// <summary>目标 API scope。</summary>
    public string Scope { get; set; } = string.Empty;
}
