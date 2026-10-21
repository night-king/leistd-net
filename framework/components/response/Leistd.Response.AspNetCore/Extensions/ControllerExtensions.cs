using Microsoft.AspNetCore.Mvc;
using Leistd.Response.Wrappers;

namespace Leistd.Response.AspNetCore.Extensions;

/// <summary>控制器统一响应扩展。</summary>
/// <remarks>
/// 失败一律经异常处理管道写出（<c>throw</c> 业务异常），启用 <c>AddResponseWrapper()</c> 时由信封写入器包成数字信封，
/// 不在控制器里手工构造失败响应。
/// </remarks>
public static class ControllerExtensions
{
    /// <summary>返回带数据的成功响应。</summary>
    public static IActionResult OkResult<T>(this ControllerBase controller, T data, string? message = null)
    {
        return controller.Ok(Result<T>.Ok(data, message));
    }

    /// <summary>返回无数据的成功响应。</summary>
    public static IActionResult OkResult(this ControllerBase controller, string? message = null)
    {
        return controller.Ok(Result.Ok(message));
    }
}
