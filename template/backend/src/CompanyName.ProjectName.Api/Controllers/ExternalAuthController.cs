#if (ExternalLogin)
using Leistd.Security.AspNetCore.Cookies;
using System.Text.Json;
using CompanyName.ProjectName.Api.Auth.Authentication;
using CompanyName.ProjectName.Api.Auth.Sessions;
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Shared;
using CompanyName.ProjectName.Application.Auth.Dtos;
using CompanyName.ProjectName.Domain.Auth.Abstractions;
using CompanyName.ProjectName.Domain.Auth.Errors;
using Leistd.ExceptionHandling;
using Leistd.Lock.Abstractions;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore.Options;
#endif
using Leistd.MultiTenancy.Context;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Stores;
#endif
using Leistd.Security.Claims;
using Leistd.Timing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.Api.Controllers;

[Route("api/v1/external-auth")]
public sealed class ExternalAuthController(
    IExternalAuthAppService externalAuthAppService,
    IAuthenticationSchemeProvider schemes,
    IOptions<ClaimTypeOptions> claimTypes,
    ICurrentTenant currentTenant,
    ITicketStore ticketStore,
    IDistributedLock distributedLock,
    SessionCookieIssuer sessionCookieIssuer,
#if (IncludeMultiTenancy)
    ITenantStore tenantStore,
    IOptions<MultiTenancyOptions> multiTenancyOptions,
#endif
    IClock clock) : BaseController
{
    [AllowAnonymous]
    [HttpGet("{provider}/challenge")]
    public Task<IActionResult> StartAsync(string provider, string? returnUrl = null) => StartCoreAsync(provider, "login", returnUrl);

    [Authorize]
    [HttpGet("{provider}/link/challenge")]
    public Task<IActionResult> StartLinkAsync(string provider) => StartCoreAsync(provider, "link", null);

    private async Task<IActionResult> StartCoreAsync(string provider, string intent, string? returnUrl)
    {
        provider = provider.ToLowerInvariant();
        var scheme = (await ExternalSchemesAsync()).SingleOrDefault(scheme =>
            scheme.Name == AuthenticationSchemeNames.ExternalProviderPrefix + provider);
        if (scheme is null)
            throw new BusinessException(ExternalAuthErrorCodes.ProviderNotConfigured, "The external provider is not configured.")
                .WithData("Provider", provider);
        if (returnUrl is not null && !Url.IsLocalUrl(returnUrl)) throw InvalidIntent();
        if (returnUrl is not null) returnUrl = Url.Content(returnUrl);
        var properties = new AuthenticationProperties
        {
            RedirectUri = $"/auth/external-callback/{provider}?intent={intent}",
            ExpiresUtc = new DateTimeOffset(DateTime.SpecifyKind(clock.Now, DateTimeKind.Utc)).AddMinutes(5)
        };
        properties.Items["external.returnUrl"] = returnUrl;
        properties.Items["external.provider"] = provider;
        properties.Items["external.intent"] = intent;
        properties.Items["external.initiator"] = intent == "link" ? claimTypes.Value.FindUserId(User) : null;
        properties.Items["external.tenant"] = currentTenant.Id?.ToString();
        properties.Items["external.tenant.name"] = currentTenant.Name;
        return Challenge(properties, scheme.Name);
    }

    /// <summary>外部登录：返回最终会话结果或第二步凭据，附带受保护的站内回跳地址。</summary>
    [AllowAnonymous]
    [HttpPost("{provider}/complete")]
    public Task<SessionLoginOutputDto> CompleteAsync(string provider, CancellationToken cancellationToken) =>
        ConsumeTicketAsync(provider, "login", async (user, returnUrl, operationToken) =>
        {
            var result = await externalAuthAppService.AuthenticateExternalUserAsync(provider, user, operationToken);
            var session = await sessionCookieIssuer.CompleteLoginAsync(HttpContext, result, operationToken);
            return session with { ReturnUrl = returnUrl };
        }, cancellationToken);

    /// <summary>把外部账号绑定到当前用户，成功时为空响应。</summary>
    /// <remarks>
    /// 需要确认身份时（<c>Auth:ReauthenticationRequired</c> 等）拒绝且<b>不消费票据</b>：本人出示当前口令或验证码后带上它重试即可。
    /// </remarks>
    [Authorize]
    [HttpPost("{provider}/link/complete")]
    public async Task CompleteLinkAsync(
        string provider,
        [FromBody] CompleteExternalLinkInputDto? input,
        CancellationToken cancellationToken)
    {
        await externalAuthAppService.EnsureLinkAllowedAsync(input, cancellationToken);
        await ConsumeTicketAsync(provider, "link", async (user, _, operationToken) =>
        {
            await externalAuthAppService.LinkCurrentUserAsync(provider, user, operationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>核对外部票据的提供商、意图、绑定发起者与租户，先一次消费票据，再在票据记录的租户作用域内执行账号政策。</summary>
    private async Task<TResult> ConsumeTicketAsync<TResult>(
        string provider,
        string intent,
        Func<ExternalUserInfo, string?, CancellationToken, Task<TResult>> completeAsync,
        CancellationToken cancellationToken)
    {
        var ticket = await HttpContext.AuthenticateAsync(AuthenticationSchemeNames.ExternalCookie);
        if (!ticket.Succeeded || ticket.Properties is null) throw InvalidIntent();
        var items = ticket.Properties.Items;
        string? Read(string name) => items.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        if (Read("external.provider") != provider || Read("external.intent") != intent ||
            intent is not ("login" or "link") ||
            intent == "link" && (User.Identity?.IsAuthenticated != true ||
                Read("external.initiator") != claimTypes.Value.FindUserId(User) ||
                Read("external.tenant") != currentTenant.Id?.ToString())) throw InvalidIntent();
        var tenantId = Guid.TryParse(Read("external.tenant"), out var id) ? id : (Guid?)null;
#if (IncludeMultiTenancy)
        if (tenantId is { } tenant &&
            await tenantStore.FindAsync(tenant, cancellationToken) is not { IsActive: true }) throw InvalidIntent();
        if (Request.Headers.ContainsKey(multiTenancyOptions.Value.HeaderName) && tenantId != currentTenant.Id) throw InvalidIntent();
#else
        if (Read("external.tenant") is not null) throw InvalidIntent();
#endif
        var key = Read(DistributedTicketStore.TicketKeyProperty) ?? throw InvalidIntent();
        await using var handle = await distributedLock.TryLockAsync(key + ":complete", TimeSpan.FromSeconds(2), cancellationToken);
        if (handle is null || await ticketStore.RetrieveAsync(key) is null) throw InvalidIntent();
        // 一次消费发生在业务前：失败也不能重用这张外部票据。
        await ticketStore.RemoveAsync(key);
        await HttpContext.SignOutAsync(AuthenticationSchemeNames.ExternalCookie);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLost);
        using var tenantChange = currentTenant.Change(tenantId, Read("external.tenant.name"));
        var user = JsonSerializer.Deserialize<ExternalUserInfo>(items[ExternalAuthenticationExtensions.UserInfoKey]!) ?? throw InvalidIntent();
        return await completeAsync(user, Read("external.returnUrl"), operation.Token);
    }

    /// <summary>登录页据此渲染入口；与 challenge、links 读同一份已登记的提供商目录。</summary>
    [AllowAnonymous]
    [HttpGet("providers")]
    public async Task<ExternalLoginProvidersOutputDto> GetProvidersAsync() =>
        new() { Providers = await ProviderNamesAsync() };

    [Authorize]
    [HttpGet("links")]
    public async Task<ExternalLoginsOutputDto> GetLinksAsync(CancellationToken cancellationToken) =>
        await externalAuthAppService.GetCurrentUserExternalLoginsAsync(await ProviderNamesAsync(), cancellationToken);

    private async Task<IEnumerable<AuthenticationScheme>> ExternalSchemesAsync() =>
        (await schemes.GetAllSchemesAsync()).Where(scheme =>
            scheme.Name.StartsWith(AuthenticationSchemeNames.ExternalProviderPrefix, StringComparison.Ordinal) &&
            typeof(IAuthenticationRequestHandler).IsAssignableFrom(scheme.HandlerType));

    private async Task<IReadOnlyList<string>> ProviderNamesAsync() =>
        (await ExternalSchemesAsync())
            .Select(scheme => scheme.Name[AuthenticationSchemeNames.ExternalProviderPrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>解绑一个外部账号；会话不够新时请求体带上本人的当前口令或验证码。</summary>
    [Authorize]
    [HttpDelete("links/{id:guid}")]
    public Task UnlinkAsync(Guid id, [FromBody] UnlinkExternalLoginInputDto? input, CancellationToken cancellationToken) =>
        externalAuthAppService.UnlinkCurrentUserAsync(id, input, cancellationToken);

    private static BusinessException InvalidIntent() => new(ExternalAuthErrorCodes.InvalidState, "Invalid or expired external authentication intent.");
}
#endif
