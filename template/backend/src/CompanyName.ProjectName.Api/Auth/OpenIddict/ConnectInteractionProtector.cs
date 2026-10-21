using System.Security.Claims;
using System.Text.Json;
using Leistd.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace CompanyName.ProjectName.Api.Auth.OpenIddict;

/// <summary>协议端点与 SPA 交互页之间往返的受保护凭据：重新认证已完成的证明、退出确认的绑定上下文。</summary>
/// <remarks>
/// 授权与退出请求启用了 OpenIddict 请求缓存：协议参数只存在于 request token（<c>request_uri</c>）里，
/// 附加在 URL 上的普通参数既不能改写它，也不能代表它。两种凭据都绑定到 <c>request_uri</c>，
/// 一次性由 request token 本身保证——授权完成或退出完成时 OpenIddict 把它标记为已兑现，同一 URI 不能再次进入。
/// 凭据用 Data Protection 加密，多副本部署须共享密钥环与应用名（见部署文档）。
/// </remarks>
public sealed class ConnectInteractionProtector(IDataProtectionProvider provider, IOptions<ClaimTypeOptions> claimTypes)
{
    /// <summary>重新认证证明在授权端点回跳 URL 中的参数名。</summary>
    public const string ReauthenticationParameter = "reauthentication";

    /// <summary>退出确认凭据的参数名（确认页查询串、确认表单字段共用）。</summary>
    public const string LogoutConfirmationParameter = "confirmation";

    /// <summary>退出确认的有效期：确认页停留超过它就要重新发起退出。</summary>
    public static readonly TimeSpan LogoutConfirmationLifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _reauthentication =
        provider.CreateProtector("CompanyName.ProjectName.Connect.Reauthentication.v1");

    private readonly ITimeLimitedDataProtector _logoutConfirmation =
        provider.CreateProtector("CompanyName.ProjectName.Connect.LogoutConfirmation.v1").ToTimeLimitedDataProtector();

    /// <summary>为要求重新认证的授权请求签发证明，记下签发时刻（完整精度）。</summary>
    public string CreateReauthentication(string requestUri, DateTime issuedAt) =>
        _reauthentication.Protect(JsonSerializer.Serialize(new ReauthenticationProof(requestUri, issuedAt)));

    /// <summary>证明属于本授权请求，且当前会话是在证明签发之后才开始的（即此后发生过一次新的登录）。</summary>
    /// <remarks>
    /// 每次登录都新建会话，会话开始时间是完整精度；auth_time 只到秒，同一秒里已经存在的会话凭它分辨不出来。
    /// 证明签发前就存在的任何会话（包括签发时的那个）都兑现不了。前提是各副本的时钟一致（见部署文档）。
    /// </remarks>
    public bool IsReauthenticated(string? proof, string? requestUri, DateTime? sessionStartedAt)
    {
        if (string.IsNullOrEmpty(proof) || string.IsNullOrEmpty(requestUri) || sessionStartedAt is not { } startedAt) return false;
        try
        {
            return JsonSerializer.Deserialize<ReauthenticationProof>(_reauthentication.Unprotect(proof)) is { } value &&
                string.Equals(value.RequestUri, requestUri, StringComparison.Ordinal) &&
                startedAt > value.IssuedAt;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    /// <summary>签发退出确认凭据，有效期见 <see cref="LogoutConfirmationLifetime"/>。</summary>
    public string CreateLogoutConfirmation(LogoutConfirmation confirmation) =>
        _logoutConfirmation.Protect(JsonSerializer.Serialize(confirmation), LogoutConfirmationLifetime);

    /// <summary>退出确认要比对的上下文：本次退出请求与当前会话。会话缺少用户或会话标识时无法绑定，返回 <see langword="null"/>。</summary>
    public LogoutConfirmation? BindLogout(string? requestUri, ClaimsPrincipal session) =>
        string.IsNullOrEmpty(requestUri) || claimTypes.Value.FindUserId(session) is not { } subject ||
        session.FindFirst(CustomClaimTypes.SessionId)?.Value is not { Length: > 0 } sessionId
            ? null
            : new LogoutConfirmation(requestUri, ClientId: null, subject,
                claimTypes.Value.ReadTenant(session).TenantId?.ToString(), sessionId);

    /// <summary>解开确认凭据并与 <paramref name="binding"/> 逐项比对（发起客户端除外）；伪造、篡改、过期或不匹配返回 <see langword="null"/>。</summary>
    public LogoutConfirmation? ReadLogoutConfirmation(string? value, LogoutConfirmation binding)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<LogoutConfirmation>(_logoutConfirmation.Unprotect(value)) is { } confirmed &&
                confirmed with { ClientId = null } == binding with { ClientId = null }
                    ? confirmed
                    : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record ReauthenticationProof(string RequestUri, DateTime IssuedAt);
}

/// <summary>
/// 退出确认绑定的上下文：哪一次退出请求、由哪个会话确认。确认时与当前请求和当前会话逐项比对，
/// 换了用户、换了租户或重新登录（会话标识变化）后，旧确认页不能结束新会话。
/// </summary>
public sealed record LogoutConfirmation(string RequestUri, string? ClientId, string Subject, string? TenantId, string SessionId);
