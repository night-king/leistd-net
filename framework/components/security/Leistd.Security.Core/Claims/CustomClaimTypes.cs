namespace Leistd.Security.Claims;

/// <summary>Leistd 使用的自定义声明类型；标准字段直接使用 <see cref="System.Security.Claims.ClaimTypes"/>。</summary>
public static class CustomClaimTypes
{
    /// <summary>主体标识（OIDC <c>sub</c>）。</summary>
    /// <remarks>
    /// 自然人主体是 GUID 用户 Id；机器主体按 <see cref="ClientSubject"/> 带 <c>client:</c> 前缀；
    /// 后台作业等非请求入口由宿主自行约定前缀。<c>ICurrentUser.Id</c> 只在能解析成 GUID 时有值，
    /// 因此需要"任何主体都留得下标识"的场景（审计）应读本 claim 的原始值。
    /// </remarks>
    public const string Subject = "sub";

    /// <summary>OAuth2/OIDC 客户端标识符。</summary>
    public const string ClientId = "client_id";

    /// <summary>OIDC 会话标识符。</summary>
    public const string SessionId = "sid";

    /// <summary>身份提供者。</summary>
    public const string IdentityProvider = "idp";

    /// <summary>是否超级管理员（<c>true</c> / <c>false</c>），由认证端签发时写入。</summary>
    public const string IsSuperAdmin = "is_super_admin";

    /// <summary>所属租户 Id（租户 GUID 字符串；宿主用户无此 claim），由认证端签发时写入。</summary>
    /// <remarks>多租户解析链以它为最高优先来源，请求头与查询串无法改写。</remarks>
    public const string TenantId = "tenant_id";

    /// <summary>模拟登录时的真实操作人标识（用户 GUID 字符串；非模拟场景无此 claim）。</summary>
    /// <remarks>模拟登录时主体上的用户是被模拟者，审计据此还原实际操作人。</remarks>
    public const string ImpersonatorUserId = "impersonator_id";

    /// <summary>模拟登录时真实操作人的显示名快照（非模拟场景无此 claim）。</summary>
    public const string ImpersonatorUserName = "impersonator_name";
}
