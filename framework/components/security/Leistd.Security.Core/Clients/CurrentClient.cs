using System.Security.Claims;
using Leistd.Security.Claims;

namespace Leistd.Security.Clients;

/// <summary>从当前 <see cref="ClaimsPrincipal"/> 提供机器客户端信息。</summary>
/// <param name="principalAccessor">认证主体访问器。</param>
public class CurrentClient(ICurrentPrincipalAccessor principalAccessor) : ICurrentClient
{
    private ClaimsPrincipal? Principal => principalAccessor.Principal;

    /// <inheritdoc />
    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(ClientId);

    /// <inheritdoc />
    public string? ClientId =>
        Principal?.FindFirst(CustomClaimTypes.ClientId)?.Value;

}
