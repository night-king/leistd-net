namespace Leistd.ServiceClient.OAuth.Options;

/// <summary>命名客户端的机器令牌范围。</summary>
public sealed class ClientCredentialsOptions
{
    /// <summary>申请的 scope，多个值以空格分隔。</summary>
    public string? Scope { get; set; }
}
