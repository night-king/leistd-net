using System.Security.Cryptography;
using Leistd.Lock.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Leistd.Security.AspNetCore.Cookies;

/// <summary>将受保护票据留在服务端缓存，浏览器仅携带带版本的引用。</summary>
/// <remarks>
/// <para>宿主提供共享缓存、数据保护密钥环和锁；直接服务端重载可信，HTTP 读取、续期与删除校验 Cookie 引用版本，显式登录由原生事件标记。</para>
/// <para>各操作传递取消令牌并抛出 <see cref="OperationCanceledException"/>，持锁操作同时响应失锁。唯一例外是请求已中止时的被动 HTTP 续期：调用方令牌与 <see cref="HttpContext.RequestAborted"/> 均已取消且不是显式登录时，不再续期，按原票据版本写出引用后正常返回；此时缓存写入可能已完成，也可能未开始。</para>
/// </remarks>
public sealed class DistributedTicketStore(
    IDistributedCache cache,
    IDataProtectionProvider protection,
    TimeProvider clock,
    IDistributedLock locks,
    IOptions<DistributedTicketStoreOptions> options) : ITicketStore
{
    /// <summary>保存缓存引用键的票据属性；使用本元数据的宿主替换实现须同样提供它。</summary>
    public const string TicketKeyProperty = "ticket.key";
    internal const string ReferenceVersion = "ticket.reference.version";
    private readonly IDataProtector _protector = protection.CreateProtector("Leistd.Security.AspNetCore.Cookies.DistributedTicketStore.v1");

    internal static string NewVersion() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static string ExplicitSignInKey(string scheme) => ReferenceVersion + ":explicit:" + scheme;
    private static string ResponseVersionKey(string scheme) => ReferenceVersion + ":" + scheme;

    internal static void ConfigureCookie(CookieAuthenticationOptions cookie)
    {
        cookie.CookieManager = new ReferenceCookieManager(cookie.CookieManager ?? new ChunkingCookieManager(), cookie);
        cookie.Events = new CookieTicketEvents(cookie.Events, cookie.EventsType);
        cookie.EventsType = null;
    }

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);
    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) => StoreCoreAsync(ticket, null, cancellationToken);
    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) => StoreCoreAsync(ticket, httpContext, cancellationToken);
    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);
    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) => ReadAsync(key, cancellationToken);
    /// <inheritdoc />
    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var ticket = await ReadAsync(key, cancellationToken);
        return ticket is not null && MatchesReference(httpContext, ticket) ? ticket : null;
    }
    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);
    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) => RenewCoreAsync(key, ticket, null, cancellationToken);
    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) => RenewCoreAsync(key, ticket, httpContext, cancellationToken);
    /// <inheritdoc />
    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);
    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => RemoveCoreAsync(key, null, cancellationToken);
    /// <inheritdoc />
    public Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) => RemoveCoreAsync(key, httpContext, cancellationToken);

    private async Task<string> StoreCoreAsync(AuthenticationTicket ticket, HttpContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ticket.Properties.Items.ContainsKey(ReferenceVersion)) ticket.Properties.Items[ReferenceVersion] = NewVersion();
        var key = options.Value.KeyPrefix + NewVersion();
        if (context is not null) context.Items[ResponseVersionKey(ticket.AuthenticationScheme)] = ticket.Properties.Items[ReferenceVersion];
        await WriteAsync(key, ticket, cancellationToken);
        return key;
    }

    private async Task RenewCoreAsync(string key, AuthenticationTicket ticket, HttpContext? context, CancellationToken cancellationToken)
    {
        try
        {
            await RenewLockedAsync(key, ticket, context, cancellationToken);
        }
        // 被动续期在响应启动回调里执行，异常逃出后由服务器记为应用错误；请求已放弃时结果到不了客户端，保留原版本后安静返回。
        catch (OperationCanceledException) when (context is not null && context.RequestAborted.IsCancellationRequested &&
            cancellationToken.IsCancellationRequested && !context.Items.ContainsKey(ExplicitSignInKey(ticket.AuthenticationScheme)))
        {
            context.Items[ResponseVersionKey(ticket.AuthenticationScheme)] = ticket.Properties.Items[ReferenceVersion];
        }
    }

    private async Task RenewLockedAsync(string key, AuthenticationTicket ticket, HttpContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var handle = await locks.LockAsync(key + ":ticket", cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLost);
        operation.Token.ThrowIfCancellationRequested();
        var current = await ReadAsync(key, operation.Token);
        var explicitSignIn = context?.Items.ContainsKey(ExplicitSignInKey(ticket.AuthenticationScheme)) == true;
        if (explicitSignIn || current is not null && SameVersion(current, ticket) &&
            (context is null || MatchesReference(context, current)))
            await WriteAsync(key, ticket, operation.Token);
        // 滑动续期不能复活已删除或到期的票据。
        if (context is not null) context.Items[ResponseVersionKey(ticket.AuthenticationScheme)] = ticket.Properties.Items[ReferenceVersion];
    }

    private async Task RemoveCoreAsync(string key, HttpContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var handle = await locks.LockAsync(key + ":ticket", cancellationToken);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.LockLost);
        operation.Token.ThrowIfCancellationRequested();
        if (context is not null)
        {
            var current = await ReadAsync(key, operation.Token);
            if (current is null || !MatchesReference(context, current)) return;
        }
        await cache.RemoveAsync(key, operation.Token);
    }

    private async Task<AuthenticationTicket?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await cache.GetAsync(key, cancellationToken);
        if (bytes is null) return null;
        try
        {
            var ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect(bytes));
            return ticket is not null && ticket.Properties.ExpiresUtc > clock.GetUtcNow() ? ticket : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private Task WriteAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ticket.Properties.Items[TicketKeyProperty] = key;
        ticket.Properties.ExpiresUtc ??= clock.GetUtcNow() + options.Value.FallbackLifetime;
        return cache.SetAsync(key, _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            new DistributedCacheEntryOptions { AbsoluteExpiration = ticket.Properties.ExpiresUtc }, cancellationToken);
    }

    private static bool SameVersion(AuthenticationTicket left, AuthenticationTicket right) =>
        left.Properties.Items.TryGetValue(ReferenceVersion, out var current) && current is not null &&
        right.Properties.Items.TryGetValue(ReferenceVersion, out var provided) && current == provided;

    private static bool MatchesReference(HttpContext context, AuthenticationTicket ticket)
    {
        var cookie = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ticket.AuthenticationScheme);
        var value = cookie.CookieManager.GetRequestCookie(context, cookie.Cookie.Name!);
        var reference = value is null ? null : cookie.TicketDataFormat.Unprotect(value);
        return reference is not null && SameVersion(reference, ticket);
    }

    internal sealed class ReferenceCookieManager(ICookieManager inner, CookieAuthenticationOptions options) : ICookieManager
    {
        public string? GetRequestCookie(HttpContext context, string key) => inner.GetRequestCookie(context, key);
        public void DeleteCookie(HttpContext context, string key, CookieOptions cookieOptions) => inner.DeleteCookie(context, key, cookieOptions);
        public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions cookieOptions)
        {
            var reference = value is null ? null : options.TicketDataFormat.Unprotect(value);
            if (reference is not null && context.Items.TryGetValue(ResponseVersionKey(reference.AuthenticationScheme), out var version))
            {
                reference.Properties.Items[ReferenceVersion] = (string?)version;
                value = options.TicketDataFormat.Protect(reference);
            }
            inner.AppendResponseCookie(context, key, value, cookieOptions);
        }
    }
}
