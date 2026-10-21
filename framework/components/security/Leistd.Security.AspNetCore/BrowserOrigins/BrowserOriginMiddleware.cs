using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Leistd.Security.AspNetCore.BrowserOrigins;

/// <summary>检查选定路径的浏览器来源；Authorization 不豁免，拒绝时返回403。</summary>
/// <remarks>宿主在受信转发头、Routing 和 CORS 之后启用；无浏览器头的客户端仍可调用。</remarks>
public sealed class BrowserOriginMiddleware(RequestDelegate next, IOptions<BrowserOriginProtectionOptions> options,
    ILogger<BrowserOriginMiddleware> logger)
{
    /// <summary>以原生端点 CORS 策略检查 Origin，无 Origin 时检查 Fetch Metadata。</summary>
    public async Task InvokeAsync(HttpContext context, ICorsPolicyProvider policyProvider, ICorsService corsService)
    {
        var request = context.Request;
        var settings = options.Value;
        var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method);
        if (!Matches(settings.AllMethodPaths, request.Path) && (safeMethod || !Matches(settings.WritePaths, request.Path)))
        {
            await next(context);
            return;
        }

        var sameOrigin = $"{request.Scheme}://{request.Host}";
        var hasOrigin = request.Headers.TryGetValue("Origin", out var origin);
        var hasSite = request.Headers.TryGetValue("Sec-Fetch-Site", out var site);
        var rejected = hasOrigin
            ? origin.Count != 1 || !IsHttpOrigin(origin[0]) ||
                !string.Equals(origin[0], sameOrigin, StringComparison.OrdinalIgnoreCase) &&
                !await IsAllowedByCorsAsync(context, policyProvider, corsService, settings.CorsPolicyName)
            : hasSite && (site.Count != 1 || site[0] is not ("same-origin" or "none"));
        if (rejected)
        {
            logger.LogWarning("Browser request rejected: {Method} {Path}, Origin {Origin}, Sec-Fetch-Site {FetchSite}, request origin {RequestOrigin}.",
                request.Method, request.Path.Value, origin.ToString(), site.ToString(), sameOrigin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private static bool Matches(string[] prefixes, PathString path) =>
        prefixes.Any(prefix => prefix == "/" || path.StartsWithSegments(prefix));

    private static bool IsHttpOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static async Task<bool> IsAllowedByCorsAsync(HttpContext context, ICorsPolicyProvider provider,
        ICorsService service, string? defaultPolicyName)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<ICorsMetadata>();
        if (metadata is IDisableCorsAttribute) return false;
        var policy = metadata is ICorsPolicyMetadata explicitPolicy
            ? explicitPolicy.Policy
            : await provider.GetPolicyAsync(context, (metadata as IEnableCorsAttribute)?.PolicyName ?? defaultPolicyName);
        return policy is { AllowAnyOrigin: false, SupportsCredentials: true } && service.EvaluatePolicy(context, policy).IsOriginAllowed;
    }
}
