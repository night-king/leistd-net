using System.Text.Json;
using System.Text.Json.Serialization;
using Leistd.ServiceClient.Http;
using Refit;

namespace Leistd.ServiceClient.Refit.Options;

/// <summary>创建符合服务客户端错误契约的 <see cref="RefitSettings"/>。</summary>
/// <remarks>非成功响应转换为远端服务异常，不暴露 Refit 的 <see cref="ApiException"/>。</remarks>
public static class ServiceClientRefitSettings
{
    /// <summary>创建统一配置的 <see cref="RefitSettings"/>。</summary>
    /// <param name="jsonOptions">自定义序列化选项；默认见 <see cref="CreateDefaultJsonOptions"/>。</param>
    /// <remarks>
    /// 发送阶段保留标准管道的异常分类与 .NET 原生取消语义，不再包装为 Refit 请求异常。
    /// <c>ExceptionFactory</c> 只作用于由 Refit 反序列化的返回形态（<c>Task&lt;T&gt;</c> 等）。
    /// 返回 <c>Task&lt;HttpResponseMessage&gt;</c> 的方法（文件下载等）拿到的是原始响应，
    /// 须自行调用 <c>EnsureRemoteSuccessAsync()</c> 完成错误还原。
    /// </remarks>
    public static RefitSettings Create(JsonSerializerOptions? jsonOptions = null)
    {
        return new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(
                jsonOptions ?? CreateDefaultJsonOptions()),
            ExceptionFactory = async response =>
                response.IsSuccessStatusCode ? null : await response.CreateRemoteErrorAsync(),
            TransportExceptionFactory = static (_, exception, _) => exception,
        };
    }

    /// <summary>创建 Web 默认值与字符串枚举组合的序列化选项。</summary>
    /// <remarks>
    /// <see cref="JsonSerializerDefaults.Web"/> 不带枚举字符串转换器，而被调方通常把枚举序列化成字符串。
    /// 读取侧对枚举名不区分大小写，数字取值仍然接受。
    /// </remarks>
    public static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
