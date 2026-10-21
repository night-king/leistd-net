using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanyName.ProjectName.Api.Hosting;

/// <summary>Web API 的 JSON 序列化策略。</summary>
public static class WebApiJson
{
    /// <summary>
    /// Web API 序列化策略（枚举字符串化 + 驼峰命名 + 忽略空值）的唯一数据源：应用到给定 options 实例，
    /// 供 MVC（<c>AddJsonOptions</c>）与 HTTP 管道（<c>ConfigureHttpJsonOptions</c>，ProblemDetails /
    /// <c>IProblemDetailsService</c> 走此配置）共用，确保业务响应与各类失败响应的命名策略一致；宿主改此处即全部跟随。
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // 用名称而非序号稳定枚举契约，并与 ServiceClient 的 camelCase 策略保持一致。
        // 拒绝整数入参，避免枚举声明顺序进入线上契约。
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }
}
