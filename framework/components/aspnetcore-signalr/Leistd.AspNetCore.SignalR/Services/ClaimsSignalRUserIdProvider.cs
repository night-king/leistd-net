using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Leistd.Security.Claims;

namespace Leistd.AspNetCore.SignalR.Services;

/// <summary>在 SignalR 连接边界按 <see cref="ClaimTypeOptions.UserIds"/> 解析 UserIdentifier，与框架其他组件读主体标识同一规则。</summary>
/// <remarks>读取 <see cref="HubConnectionContext.User"/>；业务代码仍通过 ICurrentUser 获取当前用户。</remarks>
public class ClaimsSignalRUserIdProvider(IOptions<ClaimTypeOptions> claimTypes) : IUserIdProvider
{
    /// <inheritdoc />
    public string? GetUserId(HubConnectionContext connection)
        => claimTypes.Value.FindUserId(connection.User);
}
