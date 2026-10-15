#if (LocalIdentity)
using CompanyName.ProjectName.Api.Auth.Authorization;
using CompanyName.ProjectName.Application.Auth.Constants;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.Dtos;
#if (Impersonation)
using CompanyName.ProjectName.Application.Tenants.AppServices;
using CompanyName.ProjectName.Application.Tenants.Dtos;
#endif
#if (OpenIddictServer)
using Microsoft.AspNetCore.Antiforgery;
#endif
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CompanyName.ProjectName.Api.Auth.Sessions;
#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Auth.OpenIddict;
#endif
#if (OpenIddictServer)
using OpenIddict.Abstractions;
#endif

namespace CompanyName.ProjectName.Api.Controllers;

/// <summary>认证控制器。</summary>
[Route("api/v1/auth")]
public sealed class AuthController(
    IAuthAppService authService,
    ICaptchaAppService captchaAppService,
#if (Email)
    IEmailVerificationAppService emailVerificationAppService,
#endif
#if (Impersonation)
    ITenantImpersonationAppService impersonationAppService,
#endif
    IUserSessionAppService sessionAppService,
    ITwoFactorAppService twoFactorAppService,
    SessionCookieIssuer sessionCookieIssuer) : BaseController
{
    /// <summary>账号密码登录。已启用两步验证时不下发会话，返回第二步凭据。</summary>
    [AllowAnonymous]
    [HttpPost("session-login")]
    [IgnoreAntiforgeryToken]
    public async Task<SessionLoginOutputDto> SessionLoginAsync([FromBody] LoginInputDto input, CancellationToken cancellationToken)
    {
        var result = await authService.AuthenticateSessionAsync(input, cancellationToken);
        return await sessionCookieIssuer.CompleteLoginAsync(HttpContext, result, cancellationToken);
    }

    /// <summary>登录第二步：提交验证码或恢复码。</summary>
    [AllowAnonymous]
    [HttpPost("two-factor")]
    [IgnoreAntiforgeryToken]
    public async Task TwoFactorLoginAsync([FromBody] TwoFactorLoginInputDto input, CancellationToken cancellationToken)
    {
        var principal = await authService.CompleteTwoFactorLoginAsync(input, cancellationToken);
        await sessionCookieIssuer.SignInNewSessionAsync(HttpContext, principal, cancellationToken);
    }

    /// <remarks>
    /// 允许匿名：登出是幂等的 Cookie 清理，不该要求先证明自己有效。挂 [Authorize] 时，
    /// 账号一旦被禁用或锁定，本人反而清不掉服务端 Cookie——登不出去。
    /// 未登录调用同样返回成功，不泄漏"这个会话存不存在"。
    /// </remarks>
    [AllowAnonymous]
    [AllowDuringTwoFactorSetup]
    [HttpPost("logout")]
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        // 先结束服务端会话再删 Cookie：只删 Cookie 的话，被复制走的那份 Cookie 仍然有效
        await sessionAppService.EndCurrentSessionAsync(cancellationToken);
        await HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie);
    }

#if (OpenIddictServer)
    /// <summary>依赖方发起的退出需要确认时，确认页据此展示发起方并取得防伪令牌。</summary>
    /// <remarks>
    /// 只核对确认凭据与当前会话是否匹配，不结束会话；结束会话在 <c>/connect/logout</c> 的确认 POST 里完成。
    /// 允许匿名：会话已失效时返回无效，由页面提示重新发起，而不是跳去登录。
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("logout-confirmation")]
    public async Task<LogoutConfirmationOutputDto> GetLogoutConfirmationAsync(
        [FromQuery(Name = "request_uri")] string? requestUri,
        [FromQuery] string? confirmation,
        [FromServices] ConnectInteractionProtector interactions,
        [FromServices] IAntiforgery antiforgery,
        [FromServices] IOpenIddictApplicationManager applications,
        CancellationToken cancellationToken)
    {
        var session = await HttpContext.AuthenticateAsync(AuthenticationSchemeNames.SessionCookie);
        if (!session.Succeeded || session.Principal is null ||
            interactions.BindLogout(requestUri, session.Principal) is not { } current ||
            interactions.ReadLogoutConfirmation(confirmation, current) is not { } binding)
        {
            return new LogoutConfirmationOutputDto { IsValid = false };
        }

        var application = binding.ClientId is null ? null : await applications.FindByClientIdAsync(binding.ClientId, cancellationToken);
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new LogoutConfirmationOutputDto
        {
            IsValid = true,
            ApplicationName = application is null ? binding.ClientId
                : await applications.GetLocalizedDisplayNameAsync(application, cancellationToken) ?? binding.ClientId,
            AntiforgeryFieldName = tokens.FormFieldName,
            AntiforgeryToken = tokens.RequestToken
        };
    }

#endif
#if (Email)
    [AllowAnonymous]
    [AllowDuringTwoFactorSetup]
    [HttpGet("security-config")]
    public Task<SecurityConfigOutputDto> GetSecurityConfigAsync(CancellationToken cancellationToken)
        => emailVerificationAppService.GetSecurityConfigAsync(cancellationToken);

#endif
    [AllowAnonymous]
    [HttpGet("captcha")]
    public async Task<CaptchaOutputDto> GetCaptchaAsync(CancellationToken cancellationToken)
    {
        return await captchaAppService.GenerateCaptchaAsync(cancellationToken);
    }
#if (Email)

    [AllowAnonymous]
    [HttpPost("send-email-code")]
    public async Task<EmailVerificationChallengeOutputDto> SendEmailCodeAsync(
        [FromBody] SendEmailCodeInputDto input,
        CancellationToken cancellationToken)
    {
        return await emailVerificationAppService.SendEmailCodeAsync(input, cancellationToken);
    }
#endif

    /// <summary>用户注册。</summary>
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<UserOutputDto> RegisterAsync([FromBody] RegisterInputDto input, CancellationToken cancellationToken)
    {
        return await authService.RegisterAsync(input, cancellationToken);
    }

#if (Impersonation)
    /// <summary>结束模拟登录，会话切回发起人。</summary>
    /// <remarks>
    /// 只要求已认证而不要求 <c>App.Tenants.Impersonation</c>：模拟期间持有的是<b>被模拟者</b>的权限，
    /// 租户管理员没有那条宿主侧权限。要求它会让人退不出去——只能靠退出登录，
    /// 而那等于把"回到自己的账号"变成一次重新登录。
    /// </remarks>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("end-impersonation")]
    public async Task EndImpersonationAsync(CancellationToken cancellationToken)
    {
        var principal = await impersonationAppService.EndImpersonationAsync(cancellationToken);
        await sessionCookieIssuer.ReissueAsync(HttpContext, principal);
    }

    /// <summary>当前会话的模拟状态（供界面在顶栏显示模拟提示）。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpGet("impersonation")]
    public Task<ImpersonationStatusOutputDto> GetImpersonationStatusAsync(CancellationToken cancellationToken)
        => impersonationAppService.GetStatusAsync(cancellationToken);

#endif

    /// <summary>获取当前用户信息。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpGet("me")]
    public async Task<UserOutputDto> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        return await authService.GetCurrentUserAsync(cancellationToken);
    }

    /// <summary>更新个人信息。</summary>
    [Authorize]
    [HttpPut("me")]
    public async Task<UserOutputDto> UpdateCurrentUserAsync([FromBody] UpdateCurrentUserInputDto input, CancellationToken cancellationToken)
    {
        return await authService.UpdateCurrentUserAsync(input, cancellationToken);
    }

    /// <summary>设置或清除自己的头像（图片 data URL；为空即清除）。</summary>
    [Authorize]
    [HttpPut("me/avatar")]
    public async Task<UserOutputDto> SetCurrentUserAvatarAsync([FromBody] SetAvatarInputDto input, CancellationToken cancellationToken)
    {
        return await authService.SetCurrentUserAvatarAsync(input, cancellationToken);
    }
#if (Email)

    /// <summary>给自己当前的邮箱发验证码。</summary>
    [Authorize]
    [HttpPost("me/email-verification")]
    public async Task<EmailVerificationChallengeOutputDto> SendCurrentUserEmailCodeAsync(CancellationToken cancellationToken)
    {
        return await authService.SendCurrentUserEmailCodeAsync(cancellationToken);
    }

    /// <summary>用验证码确认自己当前的邮箱。</summary>
    [Authorize]
    [HttpPost("me/email-verification/confirm")]
    public async Task<UserOutputDto> ConfirmCurrentUserEmailAsync([FromBody] EmailVerificationInputDto input, CancellationToken cancellationToken)
    {
        return await authService.ConfirmCurrentUserEmailAsync(input, cancellationToken);
    }
#endif

    /// <summary>自己的登录设备（仍然有效的会话），当前设备在前。</summary>
    [Authorize]
    [HttpGet("me/sessions")]
    public Task<IReadOnlyList<UserSessionOutputDto>> GetCurrentUserSessionsAsync(CancellationToken cancellationToken)
        => sessionAppService.GetCurrentUserSessionsAsync(cancellationToken);

    /// <summary>让自己的某台设备退出登录。</summary>
    [Authorize]
    [HttpDelete("me/sessions/{id:guid}")]
    public Task RevokeCurrentUserSessionAsync(Guid id, CancellationToken cancellationToken)
        => sessionAppService.RevokeCurrentUserSessionAsync(id, cancellationToken);

    /// <summary>让除当前设备以外的全部设备退出登录。</summary>
    [Authorize]
    [HttpPost("me/sessions/revoke-others")]
    public Task<int> RevokeOtherCurrentUserSessionsAsync(CancellationToken cancellationToken)
        => sessionAppService.RevokeOtherCurrentUserSessionsAsync(cancellationToken);

    /// <summary>自己的两步验证状态。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpGet("me/two-factor")]
    public Task<TwoFactorStatusOutputDto> GetTwoFactorStatusAsync(CancellationToken cancellationToken)
        => twoFactorAppService.GetStatusAsync(cancellationToken);

    /// <summary>开始设置两步验证：生成待确认的密钥；已启用时为换绑。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/two-factor/setup")]
    public Task<TwoFactorSetupOutputDto> BeginTwoFactorSetupAsync(
        [FromBody] BeginTwoFactorSetupInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.BeginSetupAsync(input, cancellationToken);

    /// <summary>用新验证器上的验证码确认设置（首次启用或换绑），返回新的恢复码。</summary>
    /// <remarks>受限会话（租户要求两步验证而此前未启用）启用成功后换发一个不受限的会话。</remarks>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/two-factor/enable")]
    public async Task<TwoFactorRecoveryCodesOutputDto> EnableTwoFactorAsync(
        [FromBody] EnableTwoFactorInputDto input,
        CancellationToken cancellationToken)
    {
        var result = await twoFactorAppService.EnableAsync(input, cancellationToken);

        if (User.HasClaim(claim => claim.Type == TwoFactorClaimTypes.SetupRequired))
        {
            var principal = await authService.ReissueSessionAsync(cancellationToken);
            await sessionCookieIssuer.ReissueAsync(HttpContext, principal);
        }

        return result;
    }

    /// <summary>停用两步验证（要密码与验证码）。</summary>
    [Authorize]
    [HttpPost("me/two-factor/disable")]
    public Task DisableTwoFactorAsync([FromBody] DisableTwoFactorInputDto input, CancellationToken cancellationToken)
        => twoFactorAppService.DisableAsync(input, cancellationToken);

    /// <summary>重新生成恢复码（要验证码）。</summary>
    [Authorize]
    [HttpPost("me/two-factor/recovery-codes")]
    public Task<TwoFactorRecoveryCodesOutputDto> RegenerateRecoveryCodesAsync(
        [FromBody] TwoFactorCodeInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.RegenerateRecoveryCodesAsync(input, cancellationToken);

    /// <summary>修改密码（其他设备随之退出登录）。</summary>
    [Authorize]
    [HttpPost("change-password")]
    public async Task ChangePasswordAsync([FromBody] ChangePasswordInputDto input, CancellationToken cancellationToken)
    {
        await authService.ChangePasswordAsync(input, cancellationToken);
    }
}
#endif
