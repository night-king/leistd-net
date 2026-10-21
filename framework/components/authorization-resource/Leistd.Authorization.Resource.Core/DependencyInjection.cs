using Leistd.ExceptionHandling.Options;
using Leistd.Authorization.Resource.ExceptionMappings;
using Microsoft.Extensions.DependencyInjection;
using Leistd.Authorization.Resource.Errors;
using Leistd.Localization;

namespace Leistd.Authorization.Resource;

/// <summary>资源实例授权核心注册入口。</summary>
public static class DependencyInjection
{
    /// <summary>登记资源实例授权组件的错误码默认译文与默认 HTTP 状态。</summary>
    /// <remarks>
    /// 判定入口与 ACL 处理器在 <c>Leistd.Authorization.Resource.AspNetCore</c> 的 <c>AddResourceAuthorization()</c>，
    /// 资源 ACL 存储在 EF 包；两者都会调用本方法。可重复调用，结果与调用一次相同。
    /// </remarks>
    public static IServiceCollection AddResourceAuthorizationCore(this IServiceCollection services)
    {
        services.AddJsonLocalizationResources(typeof(ResourceAuthorizationErrorCodes).Assembly);
        // 错误码的状态语义与默认译文属于本组件默认值，在此登记，避免宿主漏配时静默回落成 400。
        // 宿主的 MapCode / MapException 覆盖同一码或同一类型，与调用顺序无关。
        services.Configure<GlobalExceptionOptions>(ResourceAuthorizationExceptionMappings.Configure);
        return services;
    }
}
