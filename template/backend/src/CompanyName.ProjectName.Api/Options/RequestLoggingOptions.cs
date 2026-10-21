using Serilog.Events;

namespace CompanyName.ProjectName.Api.Options;

/// <summary>请求日志配置（配置节 <c>RequestLogging</c>）。</summary>
/// <remarks>
/// 宿主管理员可在系统设置的「运维」面板覆盖：宿主级设置经配置源覆盖本配置节，
/// 请求日志中间件每个请求取 <c>IOptionsMonitor</c> 的当前值。
/// </remarks>
public sealed class RequestLoggingOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "RequestLogging";

    /// <summary>正常完成的请求记成哪一级；调到 <see cref="LogEventLevel.Verbose"/> 即等于关掉请求日志。</summary>
    public LogEventLevel Level { get; set; } = LogEventLevel.Information;
}
