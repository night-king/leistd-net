using Leistd.Tracing.AspNetCore.Middlewares;
using Leistd.Tracing.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Leistd.Tracing.AspNetCore;

/// <summary>关联标识的 ASP.NET Core 接入入口：入站中间件与配置绑定。</summary>
public static class DependencyInjection
{
    /// <summary>注册关联标识（含核心能力）。</summary>
    /// <remarks>重复调用的规则同 <c>AddCorrelationIdCore</c>。</remarks>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">在配置节绑定之后应用的覆盖。</param>
    /// <param name="configSectionPath">选项绑定的配置节路径。</param>
    /// <example>
    /// <code>
    /// builder.Services.AddCorrelationId();
    ///
    /// app.UseCorrelationId();   // 尽量靠近管道前端，使后续中间件的日志都带上关联标识
    /// </code>
    /// </example>
    public static IServiceCollection AddCorrelationId(
        this IServiceCollection services,
        Action<CorrelationIdOptions>? configure = null,
        string configSectionPath = CorrelationIdOptions.SectionName)
    {
        return services.AddCorrelationIdCore(configure, configSectionPath);
    }

    /// <summary>把入站关联标识中间件接入请求管道。应尽量靠近管道前端。</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
