using System.Security.Claims;
using Leistd.Security.Claims;
using Leistd.Security.Users;

namespace Leistd.TestBase.Doubles;

/// <summary>可控当前用户：测试中设定 Id / Claims，替代真实 <c>ICurrentUser</c> 实现。</summary>
public sealed class FakeCurrentUser : ICurrentUser
{
    private readonly Claim[] _claims;

    public FakeCurrentUser(
        Guid? id = null,
        string? username = null,
        string? name = null,
        string? email = null,
        Claim[]? claims = null,
        Guid? tenantId = null)
    {
        Id = id;
        Username = username;
        Name = name;
        Email = email;
        TenantId = tenantId;
        // 与真实实现同源：Id 取自主体标识 claim。只给 Id 时补上 sub，读原始标识的代码才看得到它
        _claims = claims ?? [];
        if (id is { } userId && DefaultClaimTypes.FindUserId(new ClaimsPrincipal(new ClaimsIdentity(_claims))) is null)
        {
            _claims = [new Claim(CustomClaimTypes.Subject, userId.ToString()), .. _claims];
        }
    }

    private static readonly ClaimTypeOptions DefaultClaimTypes = new();

    // 与真实实现同一语义：有主体即已认证，机器主体没有 GUID 用户 Id 也是已认证的
    public bool IsAuthenticated => Id.HasValue || _claims.Length > 0;
    public string? SubjectId => DefaultClaimTypes.FindUserId(new ClaimsPrincipal(new ClaimsIdentity(_claims)));
    public Guid? Id { get; }
    public Guid? TenantId { get; }
    public string? Username { get; }
    public string? Name { get; }
    public string? Email { get; }

    public Claim? FindClaim(string claimType) =>
        _claims.FirstOrDefault(c => c.Type == claimType);

    public IReadOnlyList<Claim> FindClaims(string claimType) =>
        [.. _claims.Where(c => c.Type == claimType)];

    // 角色按官方默认的 ClaimTypes.Role
    public bool IsInRole(string role) =>
        _claims.Any(c => c.Type == ClaimTypes.Role && c.Value == role);
}
