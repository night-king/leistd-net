#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.SignIn;
using System.Security.Claims;
using Leistd.Timing;
using System.Text.Json.Nodes;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Api.Auth.OpenIddict;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application.Auth.OAuth;
using CompanyName.ProjectName.Application.OpenApplications;
using CompanyName.ProjectName.Application.Auth.Options;
using Leistd.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>OpenID Connect 协议端点：授权、令牌、注销与 userinfo。</summary>
/// <remarks>
/// 本类是全项目唯一不继承 <c>BaseController</c> 的控制器，属规范允许的例外：这些端点要返回
/// <c>SignIn</c> / <c>SignOut</c> / <c>Challenge</c> / <c>Redirect</c> 这类结果并与 Cookie 方案交互，
/// 需要 <see cref="Controller"/> 而不是统一信封的基类；响应形状由 OpenIddict 与 OIDC 规范决定，
/// 不能被包成项目的统一信封。
///
/// 端点只做协议映射：主体装配与用户解析在 <see cref="IAuthPrincipalFactory"/>，
/// 这里只把"装配不出来"翻译成对应的协议响应。<b>签发点</b>的账号状态检查在那个工厂里；
/// 运行期的撤权靠停用、删除账号时撤销令牌，由令牌记录校验在认证阶段拒绝，与本控制器无关。
/// </remarks>
public sealed class ConnectController(
    IAuthPrincipalFactory principalFactory,
    IUserSessionAppService sessionAppService,
    IUserSessionValidator sessionValidator,
    ICurrentSessionState sessionState,
    IOptions<OAuthResourceOptions> oauthOptions,
    IOptions<OAuthServerOptions> serverOptions,
    IOptions<ClaimTypeOptions> claimTypes,
    IOpenIddictApplicationManager applications,
    ConnectInteractionProtector interactions,
    IAntiforgery antiforgery,
    IClock clock) : Controller
{
    [AllowAnonymous]
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenID Connect authorization request is unavailable. "
                + "This means the OpenIddict server middleware is not wired for this endpoint.");

        var result = await HttpContext.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
        // 端点匿名可达（未登录的人要能开始认证），受限会话的拦截因此不能交给中间件，在这里按这个 Cookie 主体判定
        var session = result.Succeeded && result.Principal is not null
            ? await ReadSessionAsync(result.Principal, cancellationToken)
            : SessionValidationResult.Invalid;
        var authenticationTime = result.Principal?.GetClaim(Claims.AuthenticationTime);
        var fresh = long.TryParse(authenticationTime, out var authenticatedAt);
        var now = new DateTimeOffset(DateTime.SpecifyKind(clock.Now, DateTimeKind.Utc)).ToUnixTimeSeconds();
        // 请求缓存下 prompt/max_age 存在 request token 里，从 URL 删掉它们改变不了请求本身。
        // prompt=login 与 max_age=0 每次都要求认证，靠"本请求的重新认证已完成"的证明兑现（证明之后新建的会话）；
        // 正数 max_age 始终按当前认证年龄判断，证明不能让一次早先的认证一直算数。
        var loginPrompted = request.HasPromptValue(PromptValues.Login);
        var maxAgeExceeded = request.MaxAge is { } maxAge && (!fresh || now - authenticatedAt >= maxAge);
        var proof = Request.Query[ConnectInteractionProtector.ReauthenticationParameter].ToString();
        var proven = (loginPrompted || request.MaxAge == 0) && result.Succeeded && !string.IsNullOrEmpty(proof) &&
            interactions.IsReauthenticated(proof, request.RequestUri, await sessionAppService.GetCurrentSessionStartTimeAsync(cancellationToken));
        var demanded = loginPrompted || maxAgeExceeded;
        var reauthenticate = loginPrompted && !proven || maxAgeExceeded && !(proven && request.MaxAge == 0);
        if (!session.IsValid || result.Principal == null || reauthenticate)
        {
            if (request.HasPromptValue(PromptValues.None))
                return ProtocolError(Errors.LoginRequired);
            // 回到同一个缓存请求，需要时附上证明
            var returnUrl = AuthorizationReturnUrl(request,
                demanded ? interactions.CreateReauthentication(CachedRequestUri(request), clock.Now) : null);

            return Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}" +
                (reauthenticate ? "&reauthenticate=true" : string.Empty));
        }

        // 受限会话（组织要求两步验证而本人尚未启用）不签发授权码：静默请求按协议要求交互，
        // 交互请求先去设置页，完成后整页回到同一个缓存请求继续授权（本请求已带的重新认证证明原样保留）
        if (session.TwoFactorSetupRequired)
        {
            if (request.HasPromptValue(PromptValues.None))
                return ProtocolError(Errors.InteractionRequired);
            var returnUrl = AuthorizationReturnUrl(request, string.IsNullOrEmpty(proof) ? null : proof);
            return Redirect($"/auth/two-factor-setup?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var subject = claimTypes.Value.FindUserId(result.Principal);
        if (!Guid.TryParse(subject, out var userId))
        {
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var principal = await principalFactory.CreateAsync(userId, request.GetScopes(), cancellationToken);
        if (principal == null)
        {
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (fresh)
            principal.SetClaim(Claims.AuthenticationTime, authenticatedAt);
        // 会话绑定的客户端：授权码与刷新令牌带上当前 Identity 会话，续期时据此判定会话是否仍有效
        if (await IsSessionBoundAsync(request.ClientId!, cancellationToken))
        {
            if (result.Principal.GetClaim(CustomClaimTypes.SessionId) is not { Length: > 0 } sessionId)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            principal.SetClaim(CustomClaimTypes.SessionId, sessionId);
        }
        principal.SetDestinations(claim => claim.Type is Claims.AuthenticationTime or CustomClaimTypes.SessionId
            ? [Destinations.IdentityToken] : claim.GetDestinations());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <remarks>
    /// 依赖方发起的退出（RP-Initiated Logout 1.0 §2）：id_token_hint 指向的正是当前会话（sid 相同）时直接退出；
    /// 没有 hint 或 hint 属于别的会话时须征得用户同意，转到 SPA 的确认页。初始协议请求可以跨源（依赖方的表单 POST），
    /// 由 OpenIddict 校验并缓存；用户确认则是本源表单 POST，显式校验官方防伪令牌与确认凭据的绑定。
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenID Connect end session request is unavailable. "
                + "This means the OpenIddict server middleware is not wired for this endpoint.");

        var session = await HttpContext.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
        // 没有可结束的会话：按协议直接回到依赖方登记的退出回调
        if (!session.Succeeded || session.Principal is null)
            return SignOut(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        var sessionId = session.Principal.GetClaim(CustomClaimTypes.SessionId);
        var hint = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (!string.IsNullOrEmpty(sessionId) &&
            string.Equals(hint?.GetClaim(CustomClaimTypes.SessionId), sessionId, StringComparison.Ordinal))
            return await EndSessionAsync(cancellationToken);

        var binding = interactions.BindLogout(request.RequestUri, session.Principal);
        if (binding is not null && HttpMethods.IsPost(Request.Method) && Request.HasFormContentType &&
            Request.Form.TryGetValue(ConnectInteractionProtector.LogoutConfirmationParameter, out var confirmation) &&
            await antiforgery.IsRequestValidAsync(HttpContext) &&
            interactions.ReadLogoutConfirmation(confirmation, binding) is not null)
            return await EndSessionAsync(cancellationToken);

        // 无法绑定确认（Cookie 校验已要求会话标识，此处只作防御）：不结束会话，按协议回跳
        if (binding is null)
            return SignOut(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        // 确认页只拿到不透明的引用：request_uri 与受保护的确认凭据，hint 不出现在 URL 里
        return Redirect("/auth/logout-confirm" + QueryString.Create(new Dictionary<string, string?>
        {
            [Parameters.RequestUri] = binding.RequestUri,
            [ConnectInteractionProtector.LogoutConfirmationParameter] = interactions.CreateLogoutConfirmation(binding with
            {
                ClientId = request.ClientId ?? hint?.GetPresenters().FirstOrDefault()
            })
        }));
    }

    private async Task<IActionResult> EndSessionAsync(CancellationToken cancellationToken)
    {
        // 先结束服务端会话再删 Cookie：只删 Cookie 的话，被复制走的那份 Cookie 仍然有效
        await sessionAppService.EndCurrentSessionAsync(cancellationToken);
        await HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
        return SignOut(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> ExchangeAsync(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenID Connect token request is unavailable. "
                + "This means the OpenIddict server middleware is not wired for this endpoint.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
            {
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            // 现为会话绑定的客户端，授权里却没有会话（开启绑定前签发）：拒绝，让它重新走授权码流程。
            // 反过来，签发时带了会话的授权始终受会话约束，之后关闭绑定也不会放宽。
            if (result.Principal.GetClaim(CustomClaimTypes.SessionId) is null &&
                await IsSessionBoundAsync(request.ClientId!, cancellationToken))
                return ProtocolError(Errors.InvalidGrant);

            var scopes = request.GetScopes().Any()
                ? request.GetScopes()
                : result.Principal.GetScopes();
            // 用户与租户都取自授权码/刷新令牌的主体：本请求没有用户身份，解析链只能得出宿主
            var principal = await principalFactory.CreateFromTokenAsync(result.Principal, scopes, cancellationToken);
            if (principal == null)
            {
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsTokenExchangeGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var subject = result.Principal;
            var app = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken);
            // 官方验证管线完成归属验证后，仍保留控制器的严格单跳约束。
            if (subject is null || app is null ||
                !await applications.HasClientTypeAsync(app, ClientTypes.Confidential, cancellationToken) ||
                subject.HasClaim(claim => claim.Type == "act") ||
                request.GetAudiences().Length != 1 ||
                request.GetScopes().Length != 1 ||
                !OAuthScopes.ResourcesOf(oauthOptions.Value, request.GetScopes()).SequenceEqual(request.GetAudiences()) ||
                !OAuthScopes.All(oauthOptions.Value).Any(scope => !scope.MachineOnly &&
                    scope.Name == request.GetScopes()[0] && scope.Resources.Count == 1) ||
                subject.GetExpirationDate() is not { } expiry)
                return ProtocolError(Errors.InvalidGrant);
            var principal = await principalFactory.CreateFromTokenAsync(subject, request.GetScopes(), cancellationToken);
            if (principal is null) return ProtocolError(Errors.InvalidGrant);
            var identity = (ClaimsIdentity)principal.Identity!;
            foreach (var claim in identity.Claims.Where(claim => claim.Type is Claims.Role or CustomClaimTypes.IsSuperAdmin or Claims.AuthenticationTime).ToArray())
                identity.RemoveClaim(claim);
            identity.SetClaim("act", new JsonObject { [Claims.Subject] = ClientSubject.Format(request.ClientId!) });
            // 用户资料重新取自身份库，目标 scope 不含 profile/email 时仍保留投影所需的资料。
            principal.SetResources(request.GetAudiences());
            principal.SetDestinations(_ => [Destinations.AccessToken]);
            principal.SetAccessTokenLifetime(TimeSpan.FromSeconds(120));
            principal.SetExpirationDate(expiry);
            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // client_credentials 流程：token 代表客户端应用自身，无用户上下文
            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                Claims.Name,
                Claims.Role);

            // 机器主体的 sub 契约由框架 ClientSubject 定义（client:<client_id>），签发端与
            // 消费端（机器端点授权）共用同一处定义，理由见该类型的注释。
            SubjectClaims.Set(identity, claimTypes.Value, ClientSubject.Format(request.ClientId!));
            identity.AddClaim(new Claim(Claims.Name, request.ClientId!));

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            principal.SetResources(OAuthScopes.ResourcesOf(oauthOptions.Value, principal.GetScopes()));
            principal.SetAccessTokenLifetime(serverOptions.Value.ClientCredentialsAccessTokenLifetime);

            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // OpenIddict 只放行上面登记的四种授权类型，其余在进入本端点前已按协议回 unsupported_grant_type。
        // 走到这里说明登记了新的授权类型却没写处理分支，是编程错误而不是客户端的业务错误。
        throw new InvalidOperationException(
            $"Grant type '{request.GrantType}' is enabled on the OpenIddict server but has no handler in {nameof(ExchangeAsync)}.");
    }

    // 同一个缓存请求的本地地址：只带 client_id 与 request_uri（协议参数全在 request token 里），需要时附上重新认证证明
    private string AuthorizationReturnUrl(OpenIddictRequest request, string? reauthenticationProof) =>
        Request.PathBase + Request.Path + QueryString.Create(new Dictionary<string, string?>
        {
            [Parameters.ClientId] = request.ClientId,
            [Parameters.RequestUri] = CachedRequestUri(request),
            [ConnectInteractionProtector.ReauthenticationParameter] = reauthenticationProof
        }.Where(parameter => parameter.Value is not null));

    private static string CachedRequestUri(OpenIddictRequest request) => request.RequestUri ?? throw new InvalidOperationException(
        "The authorization request has no request_uri. This means authorization request caching is not enabled.");

    // 本请求的会话状态由会话 Cookie 校验写入，但只在它属于这个 Cookie 主体时才能用：默认认证方案可能选了 Bearer，
    // 那时状态没有写入，缺失不代表不受限。对不上就按这个主体重新校验，无效即当作未登录
    private async Task<SessionValidationResult> ReadSessionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Guid.TryParse(principal.GetClaim(CustomClaimTypes.SessionId), out var sessionId) && sessionState.SessionId == sessionId
            ? SessionValidationResult.Valid(sessionState.TwoFactorSetupRequired)
            : await sessionValidator.ValidateAsync(principal, cancellationToken);

    private ForbidResult ProtocolError(string error) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error
    }), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    // 读法与登记端共用：无法识别的值按绑定处理；未设置（登记早于该设置）按未绑定处理，由管理员升级时显式选择。
    private async Task<bool> IsSessionBoundAsync(string clientId, CancellationToken cancellationToken) =>
        await applications.FindByClientIdAsync(clientId, cancellationToken) is { } application &&
        OpenApplicationSettings.ReadSessionBound(await applications.GetSettingsAsync(application, cancellationToken)) == true;

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> UserInfoAsync(CancellationToken cancellationToken)
    {
        var claims = await principalFactory.CreateUserInfoAsync(User, cancellationToken);
        if (claims == null)
        {
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Ok(claims);
    }
}
#endif
