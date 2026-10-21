namespace CompanyName.ProjectName.Application.Shared;

/// <summary>认证方案名。</summary>
/// <remarks>
/// <para>方案名同时被三处使用：Api 的认证组装与授权策略、Controller 的
/// <c>SignInAsync</c> / <c>SignOutAsync</c>、以及应用层构造会话主体时的
/// <c>ClaimsIdentity</c> 认证类型。三处必须完全一致——改错一处的表现是
/// "登录成功但后续请求全是匿名"，没有任何编译期信号。</para>
/// <para>常量放在应用层而不是 Api 层：应用层构造主体时需要它，而应用层不能引用 Api。
/// 与 <c>TenantConnectionScopes</c> 同型。</para>
/// <para>刻意不叫 <c>AuthenticationSchemes</c>：那个名字与 <c>System.Net.AuthenticationSchemes</c>
/// 冲突，而 Api 的宿主组装有 <c>using System.Net;</c>——撞名只在生成产物里才报错。</para>
/// </remarks>
public static class AuthenticationSchemeNames
{
#if (SpaFrontend)
    /// <summary>Cookie 会话方案</summary>
    public const string SessionCookie = "MyProjectCookie";
#endif
#if (OpenIddictServer || ResourceBrowserSession)
    /// <summary>按请求选择 Bearer 或 Cookie 的策略方案。</summary>
    public const string Smart = "MyProjectSmart";
#endif
#if (ExternalLogin)
    /// <summary>显式登记用于外部认证的官方远程处理器的方案名前缀。</summary>
    public const string ExternalProviderPrefix = "MyProjectExternal:";
    /// <summary>外部认证完成前的短时服务端票据方案。</summary>
    public const string ExternalCookie = "MyProjectExternal";
#endif
#if (ResourceBrowserSession)
    /// <summary>Resource 宿主的机密 OIDC 客户端方案。</summary>
    public const string OpenIdConnect = "MyProjectOidc";
#endif
}
