using System.Net.Http.Headers;
using Leistd.ServiceClient.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Leistd.ServiceClient.AspNetCore;

/// <summary>ASP.NET Core 用户访问令牌适配。</summary>
public static class DependencyInjection
{
    /// <summary>注册请求时读取的访问令牌适配器。必须传入宿主的 Bearer 验证方案，不能传 Cookie 方案。</summary>
    /// <remarks>
    /// 可重复调用：适配器只注册一次。验证方案以首次调用为准，之后传入的不同方案被忽略；
    /// 已注册其他 <see cref="IUserAccessTokenAccessor"/> 时不覆盖。
    /// 指定方案必须认证成功，且返回主体至少有一个已认证身份；只读取票据保存的 access_token。
    /// </remarks>
    public static IServiceCollection AddUserAccessTokenAccessor(this IServiceCollection services, string authenticationSchemeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationSchemeName);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IUserAccessTokenAccessor>(provider =>
            new HttpUserAccessTokenAccessor(provider.GetRequiredService<IHttpContextAccessor>(), authenticationSchemeName));
        return services;
    }

    /// <summary>从指定方案的已认证票据读取访问令牌，支持服务端会话与策略方案。</summary>
    /// <remarks>
    /// 宿主必须提供验证票据并保存 access_token 的可信方案；组件不读取原始请求头或回退其他方案。
    /// 与 AddUserAccessTokenAccessor 二选一；首次登记的入口与方案生效，不覆盖宿主实现。
    /// 无上下文、认证失败、未认证主体或缺失令牌时返回 null；请求期读取，不捕获请求作用域。
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddAuthenticatedUserAccessTokenAccessor("ResourceSession");
    /// </code>
    /// </example>
    public static IServiceCollection AddAuthenticatedUserAccessTokenAccessor(this IServiceCollection services, string authenticationSchemeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationSchemeName);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IUserAccessTokenAccessor>(provider =>
            new AuthenticatedUserAccessTokenAccessor(provider.GetRequiredService<IHttpContextAccessor>(), authenticationSchemeName));
        return services;
    }

    private static async ValueTask<string?> ReadAuthenticatedTokenAsync(HttpContext context, string scheme, CancellationToken cancellationToken)
    {
        var result = await context.AuthenticateAsync(scheme);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded && result.Principal?.Identities.Any(identity => identity.IsAuthenticated) == true
            ? result.Properties?.GetTokenValue("access_token") : null;
    }

    private sealed class HttpUserAccessTokenAccessor(IHttpContextAccessor contexts, string scheme) : IUserAccessTokenAccessor
    {
        public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = contexts.HttpContext;
            if (context is null || !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
                !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
                return null;
            // Cookie 认证成功不能替代 Bearer 验证；只读取认证票据，避免请求头被改写后交换未验证的值。
            return await ReadAuthenticatedTokenAsync(context, scheme, cancellationToken);
        }
    }

    private sealed class AuthenticatedUserAccessTokenAccessor(IHttpContextAccessor contexts, string scheme) : IUserAccessTokenAccessor
    {
        public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return contexts.HttpContext is { } context
                ? await ReadAuthenticatedTokenAsync(context, scheme, cancellationToken) : null;
        }
    }
}
