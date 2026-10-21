using global::OpenIddict.Abstractions;
using global::OpenIddict.Server;

namespace Leistd.Security.OpenIddict.Server.Handlers;

// 官方 PrepareIssuedTokenPrincipal 重置签发时间和到期时间后，再约束最终用于生成 JWT 的主体。
internal sealed class TokenExchangeExpirationHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.IsTokenExchangeGrantType() && context.IssuedTokenPrincipal is { } output &&
            context.Principal?.GetExpirationDate() is { } subjectExpiry &&
            output.GetExpirationDate() is { } outputExpiry && subjectExpiry < outputExpiry)
            output.SetExpirationDate(subjectExpiry);
        return ValueTask.CompletedTask;
    }
}
