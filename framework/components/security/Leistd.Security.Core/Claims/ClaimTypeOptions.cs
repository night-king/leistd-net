using System.Security.Claims;

namespace Leistd.Security.Claims;

/// <summary>主体标识与租户的 claim 类型，以及读取它们的唯一规则。</summary>
/// <remarks>
/// <para>框架读这两类 claim 的每一处（当前用户、租户解析、权限判定、SignalR 寻址、操作记录、服务间还原）都按这里解析。</para>
/// <para>只在代码里经 <c>services.Configure&lt;ClaimTypeOptions&gt;(...)</c> 设置，不绑定配置节。</para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services.Configure&lt;ClaimTypeOptions&gt;(options =&gt; options.TenantId = "tid");
/// </code>
/// </example>
public sealed class ClaimTypeOptions
{
    /// <summary>主体标识的读取顺序，取第一个非空白值；默认 <c>sub</c>，其次 <see cref="ClaimTypes.NameIdentifier"/>。</summary>
    /// <remarks>
    /// 原始值可能是 GUID 用户 Id、<see cref="ClientSubject"/> 格式的机器主体或宿主约定的其他标识；
    /// 要自然人用户 Id 读 <c>ICurrentUser.Id</c>（在原始值之上只接受 GUID）。
    /// </remarks>
    public IReadOnlyList<string> UserIds { get; set; } = [CustomClaimTypes.Subject, ClaimTypes.NameIdentifier];

    /// <summary>租户标识的 claim 类型，默认 <see cref="CustomClaimTypes.TenantId"/>（<c>tenant_id</c>）。</summary>
    /// <remarks>值必须是租户 GUID；没有该 claim 表示宿主主体。租户名只经匿名请求提示传递，不进身份。</remarks>
    public string TenantId { get; set; } = CustomClaimTypes.TenantId;

    /// <summary>按 <see cref="UserIds"/> 读取主体标识的原始值。</summary>
    /// <param name="principal">主体；为 <see langword="null"/> 时返回 <see langword="null"/>。</param>
    /// <returns>主体身份（见 <see cref="FindSubjectIdentity"/>）上第一个非空白的值；没有带标识的身份时为 <see langword="null"/>。</returns>
    public string? FindUserId(ClaimsPrincipal? principal)
        => FindSubjectIdentity(principal) is { } identity ? FindUserId(identity) : null;

    /// <summary>取主体身份：按顺序第一个带用户标识（按 <see cref="UserIds"/>）的身份。</summary>
    /// <param name="principal">主体；为 <see langword="null"/> 时返回 <see langword="null"/>。</param>
    /// <returns>主体身份；没有带标识的身份时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 同一主体常合并了多个身份（多个认证方案、服务间还原出的被代表用户与调用方机器身份）。
    /// 标识、租户与名字、邮箱这类描述"这个人"的 claim 都应取自它，跨身份按类型各取第一个会把两个人拼成一个。
    /// </remarks>
    public ClaimsIdentity? FindSubjectIdentity(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        foreach (var identity in principal.Identities)
        {
            if (FindUserId(identity) is not null)
            {
                return identity;
            }
        }

        return null;
    }

    /// <summary>按 <see cref="TenantId"/> 读取主体所属租户。</summary>
    /// <param name="principal">主体；为 <see langword="null"/> 时视为宿主。</param>
    /// <returns>合法时为主体所属租户（宿主为 <see langword="null"/>）；非法由调用方失败关闭。</returns>
    /// <remarks>
    /// <para>用户标识与租户取自同一个身份（主体身份），<see cref="FindUserId(ClaimsPrincipal?)"/> 取它的标识，这里取它的租户；
    /// 分别从整个主体取会把两份凭据拼成另一个人。</para>
    /// <para>非法的情形：同一身份内租户 claim 多于一条（即使值相同，也只可能来自签发错误或拼接篡改）或不是 GUID；
    /// 其他带用户标识的身份带着与主体身份不同的租户（含主体身份为宿主）。同一主体被多个认证方案认证时，
    /// 各身份带同一租户，合法。</para>
    /// <para>不带用户标识的补充身份只在主体身份没有租户时提供租户，且彼此须一致；
    /// 带用户标识却没有租户 claim 的其他身份（如其他认证方案的身份）不参与判定。</para>
    /// </remarks>
    public TenantClaim ReadTenant(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return TenantClaim.Host;
        }

        var hasSubject = false;
        Guid? subjectTenant = null;
        Guid? delegatedTenant = null;
        foreach (var identity in principal.Identities)
        {
            var claims = identity.FindAll(TenantId).Take(2).ToArray();
            Guid? tenant = null;
            if (claims.Length > 1)
            {
                return TenantClaim.Invalid;
            }

            if (claims.Length == 1)
            {
                if (!Guid.TryParse(claims[0].Value, out var value))
                {
                    return TenantClaim.Invalid;
                }

                tenant = value;
            }

            if (FindUserId(identity) is null)
            {
                if (tenant is { } delegated)
                {
                    if (delegatedTenant is { } seen && seen != delegated)
                    {
                        return TenantClaim.Invalid;
                    }

                    delegatedTenant = delegated;
                }

                continue;
            }

            if (!hasSubject)
            {
                hasSubject = true;
                subjectTenant = tenant;
            }
            else if (tenant is { } other && other != subjectTenant)
            {
                return TenantClaim.Invalid;
            }
        }

        if (subjectTenant is { } resolved)
        {
            return delegatedTenant is { } delegated && delegated != resolved
                ? TenantClaim.Invalid
                : new TenantClaim(true, resolved);
        }

        return delegatedTenant is null ? TenantClaim.Host : new TenantClaim(true, delegatedTenant);
    }

    private string? FindUserId(ClaimsIdentity identity)
    {
        foreach (var claimType in UserIds)
        {
            var value = identity.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>主体上租户 claim 的解析结果。</summary>
/// <param name="IsValid">claim 是否合法，规则见 <see cref="ClaimTypeOptions.ReadTenant"/>。</param>
/// <param name="TenantId">合法时的租户；<see langword="null"/> 表示宿主。</param>
public readonly record struct TenantClaim(bool IsValid, Guid? TenantId)
{
    /// <summary>宿主主体（没有租户 claim）。</summary>
    public static TenantClaim Host { get; } = new(true, null);

    /// <summary>非法：同一身份内多条、值不是 GUID，或与主体身份的租户不一致。</summary>
    public static TenantClaim Invalid { get; } = new(false, null);
}
