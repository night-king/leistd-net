using System.Security.Claims;
using Leistd.Security.Claims;
using Microsoft.Extensions.Options;

namespace Leistd.Security.Users;

/// <summary>从当前 <see cref="ClaimsPrincipal"/> 提供强类型用户信息。</summary>
/// <param name="principalAccessor">认证主体访问器。</param>
/// <param name="claimTypes">主体标识与租户的 claim 类型。</param>
public class CurrentUser(ICurrentPrincipalAccessor principalAccessor, IOptions<ClaimTypeOptions> claimTypes) : ICurrentUser
{
    private const string NameClaimType = "name";
    private const string PreferredUsernameClaimType = "preferred_username";
    private const string EmailClaimType = "email";

    private ClaimsPrincipal? Principal => principalAccessor.Principal;

    /// <inheritdoc />
    public bool IsAuthenticated => Principal.HasAuthenticatedIdentity();

    /// <inheritdoc />
    public string? SubjectId => claimTypes.Value.FindUserId(Principal);

    /// <inheritdoc />
    // 在原始值之上只接受 GUID：机器主体等非 GUID 标识得到 null
    public Guid? Id => Guid.TryParse(SubjectId, out var id) ? id : null;

    /// <inheritdoc />
    public Guid? TenantId
    {
        get
        {
            var tenant = claimTypes.Value.ReadTenant(Principal);
            // 失败关闭：把非法租户 claim 当成宿主，等于让租户主体看到宿主数据
            return tenant.IsValid
                ? tenant.TenantId
                : throw new InvalidOperationException(
                    $"The principal carries an invalid '{claimTypes.Value.TenantId}' claim: expected at most one GUID.");
        }
    }

    /// <inheritdoc />
    public string? Username =>
        FindFirstValue(PreferredUsernameClaimType, NameClaimType, ClaimTypes.Name);

    /// <inheritdoc />
    public string? Name =>
        FindFirstValue(NameClaimType, ClaimTypes.Name);

    /// <inheritdoc />
    public string? Email =>
        FindFirstValue(EmailClaimType, ClaimTypes.Email);

    /// <inheritdoc />
    public Claim? FindClaim(string claimType) =>
        Principal?.FindFirst(claimType);

    /// <inheritdoc />
    public IReadOnlyList<Claim> FindClaims(string claimType) =>
        Principal?.FindAll(claimType).ToList() ?? [];

    /// <inheritdoc />
    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var principal = Principal;
        if (principal == null)
            return false;

        return claimTypes.Value.FindSubjectIdentity(principal) is { } subject
            ? subject.HasClaim(subject.RoleClaimType, role)
            : principal.IsInRole(role);
    }

    // 描述"这个人"的 claim 取自主体身份，与标识、租户同源：跨身份按类型各取第一个的话，
    // 服务间还原时名字可能来自调用方的机器令牌。没有带标识的身份时不存在拼接，按整个主体读
    private string? FindFirstValue(params string[] types)
    {
        var principal = Principal;
        if (principal == null)
            return null;

        var subject = claimTypes.Value.FindSubjectIdentity(principal);
        foreach (var claimType in types)
        {
            var value = subject is not null
                ? subject.FindFirst(claimType)?.Value
                : principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
