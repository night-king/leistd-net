using System.Net;
using Leistd.Authorization.Resource.Errors;
using Leistd.ExceptionHandling.Options;

namespace Leistd.Authorization.Resource.ExceptionMappings;

// 资源授权组件的非默认 HTTP 错误语义，由 AddResourceAuthorizationCore 登记；宿主用 MapCode 覆盖，与调用顺序无关。
internal static class ResourceAuthorizationExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapDefaultCode(ResourceAuthorizationErrorCodes.ConcurrencyConflict, (int)HttpStatusCode.Conflict);
    }
}
