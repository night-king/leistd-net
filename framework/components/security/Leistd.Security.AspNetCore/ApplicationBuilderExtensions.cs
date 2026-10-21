using Leistd.Security.AspNetCore.BrowserOrigins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Security.AspNetCore;

/// <summary>HTTP 安全管道入口。</summary>
public static class ApplicationBuilderExtensions
{
    private const string BrowserOriginMarker = "Leistd.Security.BrowserOriginProtection";

    /// <summary>在受信转发头、Routing 和 CORS 之后检查已配置路径的浏览器来源。</summary>
    /// <remarks>须先调用 AddBrowserOriginProtection，否则抛配置异常。以当前 IApplicationBuilder.Properties 标记防止重复挂载；重复调用返回同一构建器。</remarks>
    public static IApplicationBuilder UseBrowserOriginProtection(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<BrowserOriginRegistration>() is null)
            throw new InvalidOperationException("Call AddBrowserOriginProtection before UseBrowserOriginProtection.");
        if (app.Properties.ContainsKey(BrowserOriginMarker)) return app;
        app.Properties[BrowserOriginMarker] = true;
        return app.UseMiddleware<BrowserOriginMiddleware>();
    }
}
