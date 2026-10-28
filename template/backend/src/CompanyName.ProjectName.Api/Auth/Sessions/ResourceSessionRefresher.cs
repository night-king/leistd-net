#if (RemoteTokenAuth)
using Leistd.Security.AspNetCore.Cookies;
using Leistd.ServiceClient.Exceptions;
using CompanyName.ProjectName.Api.Options;
using Microsoft.Extensions.Caching.Distributed;
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
    TimeProvider clock,
    IDistributedCache cache,
    IOptions<SessionCookieOptions> sessionOptions)
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
            if (!DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiration))
            {
                await RejectAsync();
                return;
            }
            if (expiration <= clock.GetUtcNow() + AccessTokenRenewal.Lead)
            {
                var deadline = expiration + sessionOptions.Value.RefreshGracePeriod;
                var retryKey = key + ":refresh:retry";
                var retryText = await cache.GetStringAsync(retryKey, operation.Token);
                if (DateTimeOffset.TryParse(retryText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var retryAt) && retryAt > clock.GetUtcNow())
                {
                    if (clock.GetUtcNow() >= deadline) { await RejectAsync(); return; }
                }
                else
                {
                    var refresh = properties.GetTokenValue("refresh_token");
                    if (string.IsNullOrEmpty(refresh)) { await RejectAsync(); return; }
                    var options = oidcOptions.Get(AuthenticationSchemeNames.OpenIdConnect);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, timeout.Token);
                    var renewed = false;
                    try
                    {
                        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(request.Token);
                        using var response = await options.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["grant_type"] = "refresh_token", ["client_id"] = options.ClientId!,
                            ["client_secret"] = options.ClientSecret!, ["refresh_token"] = refresh
                        }), request.Token);
                        var payload = await response.Content.ReadAsStringAsync(request.Token);
                        if (!response.IsSuccessStatusCode)
                        {
                            string? error = null;
                            try
                            {
                                using var rejected = JsonDocument.Parse(payload);
                                if (rejected.RootElement.ValueKind == JsonValueKind.Object &&
                                    rejected.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String)
                                    error = value.GetString();
                            }
                            catch (JsonException) { /* 网关错误页不一定是 JSON，仍保留真实 HTTP 状态。 */ }
                            throw new RemoteServiceException("The session token endpoint rejected renewal.",
                                (int)response.StatusCode, error) { IsOAuthError = !string.IsNullOrEmpty(error) };
                        }
                        using var json = JsonDocument.Parse(payload);
                        var body = json.RootElement;
                        var access = body.GetProperty("access_token").GetString()!;
                        var principal = await ValidateAccessTokenAsync(context.HttpContext, access, request.Token);
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
                        renewed = true;
                    }
                    catch (Exception exception) when (!operation.IsCancellationRequested &&
                        (exception is ServiceClientException failure && ServiceClientFailureClassifier.IsUnavailable(failure) ||
                         exception is HttpRequestException && ServiceClientFailureClassifier.Classify(exception) is ServiceClientFailureKind.Unavailable or ServiceClientFailureKind.Timeout ||
                         exception is OperationCanceledException && timeout.IsCancellationRequested))
                    {
                        if (clock.GetUtcNow() >= deadline) { await RejectAsync(); return; }
                        await cache.SetStringAsync(retryKey, clock.GetUtcNow().AddSeconds(30).ToString("o", CultureInfo.InvariantCulture),
                            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) }, operation.Token);
                        context.HttpContext.RequestServices.GetRequiredService<ILogger<ResourceSessionRefresher>>()
                            .LogWarning("Identity is unavailable; using the server-side session until {Deadline} (stale for {Staleness}).", deadline, clock.GetUtcNow() - expiration);
                    }
                    if (renewed)
                    {
                        await store.RenewAsync(key, ticket, operation.Token);
                        await cache.RemoveAsync(retryKey, operation.Token);
                    }
                }
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
