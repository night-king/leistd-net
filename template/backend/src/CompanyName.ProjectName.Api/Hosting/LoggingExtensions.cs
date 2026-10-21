using CompanyName.ProjectName.Api.Options;
using Leistd.Tracing.AspNetCore;
using Serilog;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>日志与链路标识的注册入口。</summary>
public static class LoggingExtensions
{
    /// <summary>注册 Serilog、请求日志选项与 CorrelationId。</summary>
    public static WebApplicationBuilder AddMyProjectLogging(this WebApplicationBuilder builder)
    {
        // 最小级别交给 Serilog 自己从配置读：它订阅配置重载，设置覆盖 Serilog:MinimumLevel 后立即生效，
        // 不需要另建一个级别开关再手动同步
        builder.Services.AddSerilog((services, lc) =>
        {
            lc.ReadFrom.Configuration(builder.Configuration)
              .ReadFrom.Services(services)
              .Enrich.FromLogContext();
        }, preserveStaticLogger: true);
        // 宿主日志实例独立持有：同进程内其他宿主的启动或释放不切换业务日志管道。
        builder.Services.AddOptions<RequestLoggingOptions>()
            .Bind(builder.Configuration.GetSection(RequestLoggingOptions.SectionName));

        builder.Services.AddCorrelationId();

        return builder;
    }
}
