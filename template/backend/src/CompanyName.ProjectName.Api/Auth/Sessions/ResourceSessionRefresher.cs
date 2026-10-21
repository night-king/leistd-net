#if (RemoteTokenAuth)
using Leistd.Security.AspNetCore.Cookies;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using CompanyName.ProjectName.Application.Shared;
using Leistd.Lock.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using OpenIddict.Validation;
#if (!IncludeMultiTenancy)
using CompanyName.ProjectName.Api.Middlewares;
using Leistd.Security.Claims;
#endif

namespace CompanyName.ProjectName.Api.Auth.Sessions;

/// <summary>机密客户端在服务端续期；同一票据的并发刷新由已有分布式锁串行化。</summary>
internal sealed class ResourceSessionRefresher(
    ITicketStore store,
    IDistributedLock locks,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider clock)
{
    internal static async Task<ClaimsPrincipal> ValidateAccessTokenAsync(HttpContext context, string token, CancellationToken cancellationToken)
    {
        var principal = await context.RequestServices.GetRequiredService<OpenIddictValidationService>()
            .ValidateAccessTokenAsync(token, cancellationToken);
#if (!IncludeMultiTenancy)
        if (!HostPrincipalMiddleware.IsHost(principal, context.RequestServices.GetRequiredService<IOptions<ClaimTypeOptions>>().Value))
            throw new InvalidOperationException("A single-tenant resource cannot accept a tenant identity.");
#endif
        return new ClaimsPrincipal(new ClaimsIdentity(principal.Claims, AuthenticationSchemeNames.SessionCookie, "preferred_username", "role"));
    }

    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var cancellationToken = context.HttpContext.RequestAborted;
        var key = context.Properties.Items.TryGetValue(DistributedTicketStore.TicketKeyProperty, out var ticketKey) ? ticketKey : null;
        if (key is null) { context.RejectPrincipal(); return; }
        if (!DateTimeOffset.TryParse(context.Properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiration))
        {
            await RejectAsync();
            return;
        }
        // 官方 Cookie 处理器已读取票据；远离刷新窗口的请求不再获取锁或重复读取。
        if (expiration > clock.GetUtcNow() + AccessTokenRenewal.Lead) return;
        try
        {
            await using var handle = await locks.LockAsync(key + ":refresh", cancellationToken);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLost);
            var ticket = await store.RetrieveAsync(key, context.HttpContext, operation.Token);
            if (ticket is null) { await RejectAsync(); return; }
            var properties = ticket.Properties;
            var options = oidcOptions.Get(AuthenticationSchemeNames.OpenIdConnect);
            if (!DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiration))
            {
                await RejectAsync();
                return;
            }
            if (expiration <= clock.GetUtcNow() + AccessTokenRenewal.Lead)
            {
                var refresh = properties.GetTokenValue("refresh_token");
                if (string.IsNullOrEmpty(refresh)) { await RejectAsync(); return; }
                var configuration = await options.ConfigurationManager!.GetConfigurationAsync(operation.Token);
                using var response = await options.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = options.ClientId!,
                    ["client_secret"] = options.ClientSecret!, ["refresh_token"] = refresh
                }), operation.Token);
                if (!response.IsSuccessStatusCode) { await RejectAsync(); return; }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(operation.Token));
                var body = json.RootElement;
                var access = body.GetProperty("access_token").GetString()!;
                var principal = await ValidateAccessTokenAsync(context.HttpContext, access, operation.Token);
                if (principal.FindFirst("sub")?.Value != ticket.Principal.FindFirst("sub")?.Value)
                {
                    await RejectAsync();
                    return;
                }
                var tokens = properties.GetTokens().ToDictionary(token => token.Name, token => token.Value, StringComparer.Ordinal);
                tokens["access_token"] = access;
                tokens["expires_at"] = clock.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32()).ToString("o", CultureInfo.InvariantCulture);
                if (body.TryGetProperty("refresh_token", out var replacement)) tokens["refresh_token"] = replacement.GetString()!;
                // 本会话的主体取已验证的访问令牌；登录时的 id_token 保留供服务端审计，不发送到浏览器。
                properties.StoreTokens(tokens.Select(token => new AuthenticationToken { Name = token.Key, Value = token.Value }));
                ticket = new AuthenticationTicket(principal, properties, AuthenticationSchemeNames.SessionCookie);
                await store.RenewAsync(key, ticket);
            }
            // 并发请求可能在等待锁时已经完成刷新；复制最新票据，不能再用旧 refresh_token。
            context.ShouldRenew = context.Properties.GetTokenValue("access_token") != ticket.Properties.GetTokenValue("access_token");
            context.ReplacePrincipal(ticket.Principal);
            context.Properties.StoreTokens(ticket.Properties.GetTokens());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<ResourceSessionRefresher>>()
                .LogWarning(exception, "The server-side OIDC session can no longer be authenticated.");
            await RejectAsync();
        }

        async Task RejectAsync()
        {
            context.RejectPrincipal();
            try { await context.HttpContext.SignOutAsync(AuthenticationSchemeNames.SessionCookie); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 锁或缓存不可用时仍拒绝本请求并清除浏览器引用；清理故障不能恢复认证或变成 500。
                context.HttpContext.RequestServices.GetRequiredService<ILogger<ResourceSessionRefresher>>()
                    .LogWarning(exception, "The rejected server-side session ticket could not be removed.");
                context.Options.CookieManager.DeleteCookie(context.HttpContext, context.Options.Cookie.Name!,
                    context.Options.Cookie.Build(context.HttpContext));
            }
        }
    }
}
#endif
