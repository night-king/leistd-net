using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace Leistd.TestBase.Doubles;

/// <summary>
/// 可控的 <see cref="HubCallerContext"/>：不起真实连接就能给 Hub、过滤器和
/// <c>IUserIdProvider</c> 喂一个确定的调用上下文。
/// </summary>
/// <remarks>
/// <see cref="Abort"/> 只置 <see cref="Aborted"/> 而不真的中断——被测代码"调没调 Abort"
/// 是可断言的行为，真去中断反而会让后续断言拿不到状态。
/// </remarks>
public sealed class TestHubCallerContext(
    ClaimsPrincipal? user = null,
    string connectionId = "conn-test",
    string? userIdentifier = null,
    CancellationToken connectionAborted = default) : HubCallerContext
{
    /// <summary>被测代码是否调用过 <see cref="Abort"/>。</summary>
    public bool Aborted { get; private set; }

    /// <inheritdoc />
    public override string ConnectionId { get; } = connectionId;

    /// <inheritdoc />
    public override string? UserIdentifier { get; } = userIdentifier;

    /// <inheritdoc />
    public override ClaimsPrincipal? User { get; } = user;

    /// <inheritdoc />
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <inheritdoc />
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc />
    public override CancellationToken ConnectionAborted { get; } = connectionAborted;

    /// <inheritdoc />
    public override void Abort() => Aborted = true;
}
