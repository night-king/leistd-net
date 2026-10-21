using Leistd.OperationRecords.AspNetCore.Middlewares;
using Microsoft.AspNetCore.Builder;

namespace Leistd.OperationRecords.AspNetCore;

/// <summary>操作记录的 HTTP 管道入口。</summary>
public static class DependencyInjection
{
    /// <summary>启用授权通过后的业务失败记录。</summary>
    /// <remarks>
    /// 必须紧接 UseAuthorization 且位于租户作用域内；全局异常处理时租户作用域已退出，不能在那里补记。
    /// 仅记录带操作注解端点的业务错误码与可公开参数；匿名请求跳过，已记录的失败按动作与目标去重。
    /// 宿主需先注册操作记录服务；异常原样交给外层处理。
    /// </remarks>
    /// <example>
    /// <code>
    /// app.UseAuthorization();
    /// app.UseOperationFailureRecording();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseOperationFailureRecording(this IApplicationBuilder app)
        => app.UseMiddleware<OperationFailureRecordingMiddleware>();
}
