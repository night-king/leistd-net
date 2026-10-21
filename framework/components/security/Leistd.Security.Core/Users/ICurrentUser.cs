using System.Security.Claims;

namespace Leistd.Security.Users;

/// <summary>当前认证用户的信息。</summary>
/// <remarks>
/// 标识、租户、展示属性与角色都取自主体身份（<c>ClaimTypeOptions.FindSubjectIdentity</c>）：多个认证方案可合并不同身份，
/// 跨身份读会把另一个身份的属性拼到主体身份上。任意 claim 经 <see cref="FindClaim"/> /
/// <see cref="FindClaims"/> 跨全部身份读取；需要整个主体的官方语义时直接读 <see cref="ClaimsPrincipal"/>。
/// </remarks>
public interface ICurrentUser
{
    /// <summary>主体的任一身份已认证即为真，与官方授权管线的口径一致。</summary>
    bool IsAuthenticated { get; }

    /// <summary>主体标识的原始值，按 <c>ClaimTypeOptions.UserIds</c> 的顺序读取。</summary>
    /// <remarks>
    /// 任何主体都有：自然人是 GUID 用户 Id，机器主体是 <c>client:&lt;client_id&gt;</c>，后台作业是宿主约定的标识。
    /// 审计这类"任何主体都要留得下标识"的场景读它；要自然人用户读 <see cref="Id"/>。
    /// </remarks>
    string? SubjectId { get; }

    /// <summary>自然人用户 Id：主体标识能解析为 GUID 时才有值。</summary>
    /// <remarks>
    /// <see langword="null"/> 不代表未认证：机器主体的 <c>sub</c> 不是 GUID，
    /// 其 <see cref="IsAuthenticated"/> 仍可为 <see langword="true"/>；机器写入的用户审计标识为空。
    /// </remarks>
    Guid? Id { get; }

    /// <summary>所属租户 Id，按 <c>ClaimTypeOptions.TenantId</c> 读取；宿主用户为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 这是主体 claim 的直读值，仅反映"签发 token 时用户属于哪个租户"；
    /// 运行时权威的租户上下文是 <c>Leistd.MultiTenancy</c> 的 <c>ICurrentTenant</c>。
    /// 租户 claim 非法（规则见 <c>ClaimTypeOptions.ReadTenant</c>）时抛 <see cref="InvalidOperationException"/>，不当作宿主。
    /// </remarks>
    Guid? TenantId { get; }

    /// <summary>用户名。</summary>
    /// <remarks>
    /// 用户名、显示名称与邮箱都取自主体身份（<c>ClaimTypeOptions.FindSubjectIdentity</c>），与 <see cref="SubjectId"/>、
    /// <see cref="TenantId"/> 同源：服务间还原时不会取到调用方机器令牌上的名字。
    /// </remarks>
    string? Username { get; }

    /// <summary>显示名称。</summary>
    string? Name { get; }

    /// <summary>电子邮箱。</summary>
    string? Email { get; }

    /// <summary>查找指定类型的第一个声明。</summary>
    /// <remarks>按官方 <see cref="ClaimsPrincipal.FindFirst(string)"/> 跨全部身份查找；要限定在主体身份上，经 <c>ClaimTypeOptions.FindSubjectIdentity</c> 取得身份后读取。</remarks>
    /// <param name="claimType">Claim 类型。</param>
    /// <returns>找到的 Claim；不存在时为 <see langword="null"/>。</returns>
    Claim? FindClaim(string claimType);

    /// <summary>查找指定类型的全部声明，如多值的角色、scope、amr。</summary>
    /// <remarks>与 <see cref="FindClaim"/> 同一范围：按官方 <see cref="ClaimsPrincipal.FindAll(string)"/> 跨全部身份查找。</remarks>
    /// <param name="claimType">Claim 类型。</param>
    /// <returns>找到的全部 Claim；没有当前主体时为空。</returns>
    IReadOnlyList<Claim> FindClaims(string claimType);

    /// <summary>判断当前用户是否属于指定角色。</summary>
    /// <remarks>
    /// 只看主体身份，按该身份的 <see cref="ClaimsIdentity.RoleClaimType"/> 精确匹配——与官方
    /// <see cref="ClaimsPrincipal.IsInRole"/> 的差别在于不看其他身份：服务间调用时调用方机器身份上的角色不算用户的角色。
    /// 没有带用户标识的身份时按整个主体判断。授权策略里的 <c>RequireRole</c> 仍是官方的整个主体语义。
    /// </remarks>
    /// <param name="role">角色名。</param>
    bool IsInRole(string role);
}
