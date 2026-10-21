using System.Net;
using Leistd.ExceptionHandling.Options;
using Leistd.Settings.Errors;

namespace Leistd.Settings.ExceptionMappings;

// 设置组件的非默认 HTTP 错误语义，由 AddSettingsCore 登记；宿主用 MapCode 覆盖，与调用顺序无关。
internal static class SettingsExceptionMappings
{
    public static void Configure(GlobalExceptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MapDefaultCode(SettingErrorCodes.HostOnly, (int)HttpStatusCode.Forbidden);
        options.MapDefaultCode(SettingErrorCodes.IdentityCannotOperate, (int)HttpStatusCode.Forbidden);
        options.MapDefaultCode(SettingErrorCodes.NotAvailable, (int)HttpStatusCode.NotFound);
    }
}
