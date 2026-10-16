#if (LocalIdentity)
using System.Collections.Immutable;
using System.Security.Claims;
using CompanyName.ProjectName.Application.Auth.OAuth;
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Auth.Options;
using CompanyName.ProjectName.Domain.Users.Repositories;
using CompanyName.ProjectName.Domain.Users.Policies;
using CompanyName.ProjectName.Domain.Users.ValueObjects;
using Leistd.Ddd.Domain.Repositories;
using Leistd.Security.Claims;
using Leistd.Timing;
using Leistd.UnitOfWork;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.MultiTenancy.Context;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using System.Globalization;

namespace CompanyName.ProjectName.Application.Auth.SignIn;

public class AuthPrincipalFactory(
    IUserRepository userRepository,
    IRepository<UserSession, Guid> userSessionRepository,
    ILoginSecurityPolicyProvider loginSecurityPolicy,
    IClock clock,
    IOptions<OAuthResourceOptions> oauthOptions,
    IOptions<ClaimTypeOptions> claimTypes,
    ICurrentTenant currentTenant,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
#endif
    IUnitOfWorkManager unitOfWorkManager) : IAuthPrincipalFactory
{
    /// <inheritdoc />
    public async Task<ClaimsPrincipal?> CreateFromTokenAsync(
        ClaimsPrincipal tokenPrincipal,
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(claimTypes.Value.FindUserId(tokenPrincipal), out var userId) ||
            await ResolveTokenTenantAsync(tokenPrincipal, cancellationToken) is not { } tenant)
            return null;

        // 租户切换与工作单元在这里建立而不是放进辅助方法：AsyncLocal 在被 await 的方法里改，回不到调用方
        using (currentTenant.Change(tenant.Id, tenant.Name))
        using (unitOfWorkManager.Begin(requiresNew: true))
        {
            // 会话绑定的授权（签发时带了 sid）：会话已退出、撤销或空闲到期就不再续期。
            // 只判定、不记活跃：后台续期不能替 Identity 会话续命；判定失败一律拒绝（fail closed）。
            var sessionClaim = tokenPrincipal.GetClaim(CustomClaimTypes.SessionId);
            if (sessionClaim is not null && !await IsSessionActiveAsync(sessionClaim, userId, cancellationToken))
                return null;

            var principal = await CreateAsync(userId, scopes, cancellationToken);
            if (principal is null) return null;
            if (tokenPrincipal.GetClaim(Claims.AuthenticationTime) is { } authenticationTime)
                principal.SetClaim(Claims.AuthenticationTime, long.Parse(authenticationTime, CultureInfo.InvariantCulture));
            if (sessionClaim is not null)
                principal.SetClaim(CustomClaimTypes.SessionId, sessionClaim);
            principal.SetDestinations(GetDestinations);
            return principal;
        }
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal?> CreateAsync(
        Guid userId,
        IEnumerable<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        // 签发点自己闭合检查：令牌一旦发出就在有效期内可用，此刻停用的账号不能再拿到新令牌。
        // 这与运行期的撤权是两道独立关卡——签发与访问是两个时刻：停用、删除账号时已签发的令牌被撤销。
        if (user == null || user.GetAccessStatus(clock.Now) != UserAccessStatus.Allowed)
        {
            return null;
        }

        var roleNames = await userRepository.GetRoleNamesAsync(user.Id, cancellationToken);
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);

        SubjectClaims.Set(identity, claimTypes.Value, user.Id.ToString());
        identity.SetClaim(Claims.Name, user.DisplayName ?? user.Username);
        identity.SetClaim(Claims.PreferredUsername, user.Username);
        identity.SetClaim(Claims.Email, user.Email);

        if (AvatarPolicy.IsExternalUrl(user.Avatar))
        {
            identity.SetClaim(Claims.Picture, user.Avatar!);
        }

        identity.SetClaims(Claims.Role, roleNames.ToImmutableArray());
        identity.SetClaim(CustomClaimTypes.IsSuperAdmin, user.IsSuperAdmin ? "true" : "false");
        // 租户 claim：多租户解析链以它定案已登录用户的租户
        if (user.TenantId is { } tenantId)
        {
            identity.SetClaim(claimTypes.Value.TenantId, tenantId.ToString());
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes?.Where(scope => !string.IsNullOrWhiteSpace(scope)) ??
                            [Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.Roles]);
        // 受众由授予的 scope 推出：申请了哪个 API 的 scope，令牌就只能调用那个 API
        principal.SetResources(OAuthScopes.ResourcesOf(oauthOptions.Value, principal.GetScopes()));
        principal.SetDestinations(GetDestinations);

        return principal;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object>?> CreateUserInfoAsync(
        ClaimsPrincipal tokenPrincipal,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(claimTypes.Value.FindUserId(tokenPrincipal), out var userId) ||
            await ResolveTokenTenantAsync(tokenPrincipal, cancellationToken) is not { } tenant)
            return null;

        using var tenantChange = currentTenant.Change(tenant.Id, tenant.Name);
        using var unitOfWork = unitOfWorkManager.Begin(requiresNew: true);

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user == null)
        {
            return null;
        }

        // 这里不重复检查访问状态：停用、删除账号时该用户的令牌已被撤销，携带它们的请求在认证阶段
        // 就被拒绝。本方法只负责按已确认的主体投影 claim，再查一遍是同一件事做两遍。
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = user.Id.ToString()
        };

        if (tokenPrincipal.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName ?? user.Username;
            claims[Claims.PreferredUsername] = user.Username;
            if (AvatarPolicy.IsExternalUrl(user.Avatar))
            {
                claims[Claims.Picture] = user.Avatar!;
            }
        }

        if (tokenPrincipal.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (tokenPrincipal.HasScope(Scopes.Roles))
        {
            // 角色取令牌里的那份而不是回查：userinfo 要反映这枚令牌代表的权限，
            // 回查会让它和令牌内容不一致。
            claims[Claims.Role] = tokenPrincipal.GetClaims(Claims.Role)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return claims;
    }

    // 令牌主体所属租户：租户声明非法、租户已不存在或已停用时返回 null，不签发、不投影。
    // 请求本身解析出的是宿主，调用方据此切换租户并新开工作单元，读取才落到该租户的库与过滤器上
    private Task<TokenTenant?> ResolveTokenTenantAsync(ClaimsPrincipal tokenPrincipal, CancellationToken cancellationToken)
    {
        var tenantClaim = claimTypes.Value.ReadTenant(tokenPrincipal);
        if (!tenantClaim.IsValid) return Task.FromResult<TokenTenant?>(null);
        if (tenantClaim.TenantId is not { } tenantId)
            return Task.FromResult<TokenTenant?>(new TokenTenant(null, null));
#if (IncludeMultiTenancy)
        return ResolveRegisteredTenantAsync(tenantId, cancellationToken);
#else
        return Task.FromResult<TokenTenant?>(null);
#endif
    }
#if (IncludeMultiTenancy)

    private async Task<TokenTenant?> ResolveRegisteredTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await tenantStore.FindAsync(tenantId, cancellationToken);
        return tenant is { IsActive: true } ? new TokenTenant(tenantId, tenant.Name) : null;
    }
#endif

    private sealed record TokenTenant(Guid? Id, string? Name);

    // 在令牌所属租户的上下文与工作单元里调用：空闲时限按那个租户的策略
    private async Task<bool> IsSessionActiveAsync(string sessionClaim, Guid userId, CancellationToken cancellationToken) =>
        Guid.TryParse(sessionClaim, out var sessionId) &&
        await userSessionRepository.GetByIdAsync(sessionId, cancellationToken) is { } session &&
        session.UserId == userId &&
        !session.IsExpired(clock.Now, (await loginSecurityPolicy.GetAsync(cancellationToken)).SessionIdleTimeout);

    private IEnumerable<string> GetDestinations(Claim claim)
    {
        return claim.Type switch
        {
            var type when type == Claims.Subject || SubjectClaims.IsMirror(claimTypes.Value, type) =>
            [
                Destinations.AccessToken,
                Destinations.IdentityToken
            ],
            Claims.AuthenticationTime => [Destinations.IdentityToken],
            // 会话标识只给依赖方的 id_token（退出时比对会话），不进访问令牌
            CustomClaimTypes.SessionId => [Destinations.IdentityToken],
            Claims.Name or Claims.PreferredUsername or Claims.Picture
                when claim.Subject?.HasScope(Scopes.Profile) == true =>
            [
                Destinations.AccessToken,
                Destinations.IdentityToken
            ],
            Claims.Email when claim.Subject?.HasScope(Scopes.Email) == true =>
            [
                Destinations.AccessToken,
                Destinations.IdentityToken
            ],
            Claims.Role when claim.Subject?.HasScope(Scopes.Roles) == true =>
            [
                Destinations.AccessToken,
                Destinations.IdentityToken
            ],
            CustomClaimTypes.IsSuperAdmin =>
            [
                Destinations.AccessToken
            ],
            var type when type == claimTypes.Value.TenantId =>
            [
                Destinations.AccessToken
            ],
            _ => []
        };
    }
}
#endif
