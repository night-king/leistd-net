#if (LocalIdentity)
using CompanyName.ProjectName.Api.Auth.Authorization;
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
    IPasswordResetAppService passwordResetAppService,
#endif
#if (Impersonation)
    ITenantImpersonationAppService impersonationAppService,
#endif
    IUserSessionAppService sessionAppService,
    ITwoFactorAppService twoFactorAppService,
    IPasskeyAppService passkeyAppService,
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

    /// <summary>免口令登录：取断言选项与仪式凭据。</summary>
    [AllowAnonymous]
    [HttpPost("passkey-login/options")]
    [IgnoreAntiforgeryToken]
    public Task<PasskeyOptionsOutputDto> BeginPasskeySignInAsync(CancellationToken cancellationToken)
        => authService.BeginPasskeySignInAsync(cancellationToken);

    /// <summary>免口令登录：交回通行密钥断言，通过即签发会话（不再要求第二步）。</summary>
    [AllowAnonymous]
    [HttpPost("passkey-login")]
    [IgnoreAntiforgeryToken]
    public async Task PasskeySignInAsync([FromBody] PasskeySignInInputDto input, CancellationToken cancellationToken)
    {
        var principal = await authService.CompletePasskeySignInAsync(input, cancellationToken);
        await sessionCookieIssuer.SignInNewSessionAsync(HttpContext, principal, cancellationToken);
    }

    /// <summary>登录第二步：取用通行密钥验证的断言选项（本人用通行密钥这种方式时）。</summary>
    [AllowAnonymous]
    [HttpPost("two-factor/passkey-options")]
    [IgnoreAntiforgeryToken]
    public Task<PasskeyOptionsOutputDto> BeginTwoFactorPasskeyAsync(
        [FromBody] TwoFactorPasskeyOptionsInputDto input,
        CancellationToken cancellationToken)
        => authService.BeginTwoFactorPasskeyAsync(input, cancellationToken);

    /// <summary>登录第二步：提交其中一种方式的凭证（通行密钥、身份验证器、邮箱或恢复码）。</summary>
    [AllowAnonymous]
    [HttpPost("two-factor")]
    [IgnoreAntiforgeryToken]
    public async Task TwoFactorLoginAsync([FromBody] TwoFactorLoginInputDto input, CancellationToken cancellationToken)
    {
        var principal = await authService.CompleteTwoFactorLoginAsync(input, cancellationToken);
        await sessionCookieIssuer.SignInNewSessionAsync(HttpContext, principal, cancellationToken);
    }
#if (Email)

    /// <summary>登录第二步：把验证码发到本人的邮箱（本人用邮箱验证码这种方式时）。</summary>
    [AllowAnonymous]
    [HttpPost("two-factor/email-code")]
    [IgnoreAntiforgeryToken]
    public Task<TwoFactorEmailCodeOutputDto> SendTwoFactorEmailCodeAsync(
        [FromBody] TwoFactorEmailCodeInputDto input,
        CancellationToken cancellationToken)
        => authService.SendTwoFactorEmailCodeAsync(input, cancellationToken);
#endif

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
    [AllowAnonymous]
    [AllowDuringTwoFactorSetup]
    [HttpGet("security-config")]
    public Task<SecurityConfigOutputDto> GetSecurityConfigAsync(CancellationToken cancellationToken)
        => authService.GetSecurityConfigAsync(cancellationToken);

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

    /// <summary>忘记口令：发送验证码。不论邮箱是否对应能找回的账号，响应都一样。</summary>
    [AllowAnonymous]
    [HttpPost("password-reset/send-code")]
    public async Task<EmailVerificationChallengeOutputDto> SendPasswordResetCodeAsync(
        [FromBody] SendEmailCodeInputDto input,
        CancellationToken cancellationToken)
    {
        return await passwordResetAppService.SendCodeAsync(input, cancellationToken);
    }

    /// <summary>忘记口令：凭验证码设置新口令，并退出该账号的全部会话。不签发新会话。</summary>
    [AllowAnonymous]
    [HttpPost("password-reset")]
    public async Task ResetPasswordAsync([FromBody] PasswordResetInputDto input, CancellationToken cancellationToken)
    {
        await passwordResetAppService.ResetAsync(input, cancellationToken);
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
    /// <remarks>受限会话（组织要求两步验证而此前未启用）启用成功后，下一个请求即不再受限：会话与 Cookie 都不变。</remarks>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/two-factor/enable")]
    public Task<TwoFactorRecoveryCodesOutputDto> EnableTwoFactorAsync(
        [FromBody] EnableTwoFactorInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.EnableAsync(input, cancellationToken);

    /// <summary>停用两步验证（有口令的账号要密码与验证码，没有口令的只要验证码）。</summary>
    [Authorize]
    [HttpPost("me/two-factor/disable")]
    public Task DisableTwoFactorAsync([FromBody] DisableTwoFactorInputDto input, CancellationToken cancellationToken)
        => twoFactorAppService.DisableAsync(input, cancellationToken);

    /// <summary>重新生成恢复码（要身份验证器验证码或通行密钥）。</summary>
    [Authorize]
    [HttpPost("me/two-factor/recovery-codes")]
    public Task<TwoFactorRecoveryCodesOutputDto> RegenerateRecoveryCodesAsync(
        [FromBody] RegenerateRecoveryCodesInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.RegenerateRecoveryCodesAsync(input, cancellationToken);
#if (Email)

    /// <summary>开始开启邮箱验证码：确认是本人后把确认码发到当前邮箱。</summary>
    [Authorize]
    [HttpPost("me/two-factor/email/setup")]
    public Task<EmailVerificationChallengeOutputDto> BeginEmailTwoFactorSetupAsync(
        [FromBody] EmailTwoFactorChangeInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.BeginEmailSetupAsync(input, cancellationToken);

    /// <summary>用发到邮箱的确认码开启邮箱验证码。</summary>
    [Authorize]
    [HttpPost("me/two-factor/email/enable")]
    public Task EnableEmailTwoFactorAsync([FromBody] EmailVerificationInputDto input, CancellationToken cancellationToken)
        => twoFactorAppService.EnableEmailAsync(input, cancellationToken);

    /// <summary>关闭邮箱验证码。</summary>
    [Authorize]
    [HttpPost("me/two-factor/email/disable")]
    public Task DisableEmailTwoFactorAsync([FromBody] EmailTwoFactorChangeInputDto input, CancellationToken cancellationToken)
        => twoFactorAppService.DisableEmailAsync(input, cancellationToken);
#endif

    /// <summary>开始添加通行密钥：确认是本人后返回交给浏览器的创建选项。</summary>
    /// <remarks>受限会话（组织要求两步验证而此前没有独立因子）也能添加，添加完即解除限制。</remarks>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/two-factor/passkeys/options")]
    public Task<PasskeyOptionsOutputDto> BeginPasskeyRegistrationAsync(
        [FromBody] BeginPasskeyRegistrationInputDto input,
        CancellationToken cancellationToken)
        => passkeyAppService.BeginRegistrationAsync(input, cancellationToken);

    /// <summary>完成添加通行密钥；它是第一个独立因子时一并返回新发的恢复码。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/two-factor/passkeys")]
    public Task<PasskeyRegistrationOutputDto> CompletePasskeyRegistrationAsync(
        [FromBody] CompletePasskeyRegistrationInputDto input,
        CancellationToken cancellationToken)
        => passkeyAppService.CompleteRegistrationAsync(input, cancellationToken);

    /// <summary>确认身份：取用通行密钥确认的断言选项；断言随要求确认的请求一起提交（<c>passkey</c> 字段）。</summary>
    [Authorize]
    [AllowDuringTwoFactorSetup]
    [HttpPost("me/reauthentication/passkey-options")]
    public Task<PasskeyOptionsOutputDto> BeginPasskeyReauthenticationAsync(CancellationToken cancellationToken)
        => passkeyAppService.BeginReauthenticationAsync(cancellationToken);

    /// <summary>给通行密钥改名。</summary>
    [Authorize]
    [HttpPut("me/two-factor/passkeys/{id:guid}/name")]
    public Task<PasskeyOutputDto> RenamePasskeyAsync(Guid id, [FromBody] RenamePasskeyInputDto input, CancellationToken cancellationToken)
        => passkeyAppService.RenameAsync(id, input, cancellationToken);

    /// <summary>删除通行密钥（会话不够新时带上本人的一种凭证）。</summary>
    [Authorize]
    [HttpPost("me/two-factor/passkeys/{id:guid}/remove")]
    public Task RemovePasskeyAsync(Guid id, [FromBody] RemovePasskeyInputDto input, CancellationToken cancellationToken)
        => passkeyAppService.RemoveAsync(id, input, cancellationToken);

    /// <summary>指定登录第二步的首选方式。</summary>
    [Authorize]
    [HttpPut("me/two-factor/preferred-method")]
    public Task SetPreferredTwoFactorMethodAsync(
        [FromBody] SetPreferredTwoFactorMethodInputDto input,
        CancellationToken cancellationToken)
        => twoFactorAppService.SetPreferredMethodAsync(input, cancellationToken);

    /// <summary>修改密码（其他设备随之退出登录）。</summary>
    [Authorize]
    [HttpPost("change-password")]
    public async Task ChangePasswordAsync([FromBody] ChangePasswordInputDto input, CancellationToken cancellationToken)
    {
        await authService.ChangePasswordAsync(input, cancellationToken);
    }
#if (ExternalLogin)

    /// <summary>给只经外部登录建立、没有口令的账号设置口令（其他设备随之退出登录）。</summary>
    [Authorize]
    [HttpPost("me/password")]
    public Task SetPasswordAsync([FromBody] SetPasswordInputDto input, CancellationToken cancellationToken)
        => authService.SetPasswordAsync(input, cancellationToken);
#endif
}
#endif
