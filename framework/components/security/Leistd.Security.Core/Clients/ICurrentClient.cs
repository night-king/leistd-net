namespace Leistd.Security.Clients;

/// <summary>当前 OAuth2/OIDC 客户端信息，从主体的 <c>client_id</c> claim 读取。</summary>
public interface ICurrentClient
{
    /// <summary>是否存在非空的 <c>client_id</c> claim。</summary>
    bool IsAuthenticated { get; }

    /// <summary>客户端标识符；没有时为 <see langword="null"/>。</summary>
    string? ClientId { get; }

}
