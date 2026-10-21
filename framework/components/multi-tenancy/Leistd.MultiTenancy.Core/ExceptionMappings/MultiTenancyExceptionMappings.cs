using System.Net;
using Leistd.ExceptionHandling.Options;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.ExceptionMappings;

// 多租户组件的非默认 HTTP 错误语义，由 AddMultiTenancyCore 登记；宿主用 MapCode 覆盖，与调用顺序无关。
internal static class MultiTenancyExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapDefaultCode(MultiTenancyErrorCodes.NotActive, (int)HttpStatusCode.Forbidden);
        options.MapDefaultCode(MultiTenancyErrorCodes.NotFound, (int)HttpStatusCode.NotFound);
        options.MapDefaultCode(MultiTenancyErrorCodes.ConcurrencyConflict, (int)HttpStatusCode.Conflict);
        options.MapDefaultCode(MultiTenancyErrorCodes.DuplicateName, (int)HttpStatusCode.Conflict);
        options.MapDefaultCode(MultiTenancyErrorCodes.ConnectionChangeRequiresInactiveTenant, (int)HttpStatusCode.Conflict);
        options.MapDefaultCode(MultiTenancyErrorCodes.ConnectionVersionConflict, (int)HttpStatusCode.Conflict);
    }
}
